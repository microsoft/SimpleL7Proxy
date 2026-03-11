using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Backend.Iterators;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Queue;
using SimpleL7Proxy.User;
using SimpleL7Proxy.ServiceBus;
using SimpleL7Proxy.StreamProcessor;
using Shared.RequestAPI.Models;

namespace SimpleL7Proxy.Proxy;



// Review DISPOSAL_ARCHITECTURE.MD in the root for details on disposal flow

// The ProxyWorker class has the following main objectives:
// 1. Read incoming requests from the queue, prioritizing the highest priority requests.
// 2. Proxy the request to the backend with the lowest latency.
// 3. Retry against the next backend if the current backend fails.
// 4. Return a 502 Bad Gateway if all backends fail.
// 5. Return a 200 OK with backend server stats if the request is for /health.
// 6. Log telemetry data for each request.
public class ProxyWorker
{
    private readonly int _preferredPriority;
    private readonly CancellationToken _cancellationToken;
    private static bool s_debug = false;            // dev time debug flag
    private static IConcurrentPriQueue<RequestData>? s_requestsQueue;
    private static IRequeueWorker? s_requeueDelayWorker; // Initialized in constructor, only one instance
    private readonly IBackendService _backends;
    private readonly BackendOptions _options;
    private readonly IEventClient _eventClient;
    private readonly IAsyncWorkerFactory _asyncWorkerFactory; // Just inject the factory
    private readonly ILogger<ProxyWorker> _logger;
    private readonly StreamProcessorFactory _streamProcessorFactory;
    private readonly RequestLifecycleManager _lifecycleManager;
    private readonly EventDataBuilder _eventDataBuilder;
    //private readonly ProxyStreamWriter _proxyStreamWriter;
    private readonly IUserPriorityService _userPriority;
    private readonly IUserProfileService _profiles;
    private readonly string _timeoutHeaderName;
    private readonly int _id;
    private readonly string _idStr;
    private static bool s_readyToWork;
    public static bool IsReadyToWork => s_readyToWork;
    private CancellationTokenSource? _asyncExpelSource;
    private bool _isEvictingAsyncRequest;
    private readonly HealthCheckService _healthCheckService;

    private static List<string> s_backendKeys = [];
    private readonly ISharedIteratorRegistry? _sharedIteratorRegistry;

    // Static pre-allocated ProxyEvent objects for error scenarios to avoid expensive copy constructor
    // private static readonly ProxyEvent s_backendRequestAttemptEvent = new ProxyEvent(25);  // Base eventData (~20) + attempt fields (7)
    private static readonly ProxyEvent s_finallyBlockErrorEvent = new ProxyEvent(18);

    public ProxyWorker(
        int id,
        int priority,
        IConcurrentPriQueue<RequestData> requestsQueue,
        BackendOptions backendOptions,
        IBackendService? backends,
        IUserProfileService? profiles,
        IUserPriorityService? userPriority,
        IRequeueWorker requeueDelayWorker,
        IEventClient eventClient,
        IAsyncWorkerFactory asyncWorkerFactory,
        ILogger<ProxyWorker> logger,
        StreamProcessorFactory streamProcessorFactory,
        RequestLifecycleManager lifecycleManager,
        EventDataBuilder eventDataBuilder,
        HealthCheckService healthCheckService,
        ISharedIteratorRegistry? sharedIteratorRegistry,
        //ProxyStreamWriter proxyStreamWriter,
        CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        s_requestsQueue = requestsQueue ?? throw new ArgumentNullException(nameof(requestsQueue));
        _backends = backends ?? throw new ArgumentNullException(nameof(backends));
        _eventClient = eventClient;
        _asyncWorkerFactory = asyncWorkerFactory;
        s_requeueDelayWorker = requeueDelayWorker;
        _logger = logger;
        _streamProcessorFactory = streamProcessorFactory ?? throw new ArgumentNullException(nameof(streamProcessorFactory));
        _lifecycleManager = lifecycleManager ?? throw new ArgumentNullException(nameof(lifecycleManager));
        _eventDataBuilder = eventDataBuilder ?? throw new ArgumentNullException(nameof(eventDataBuilder));
        //_proxyStreamWriter = proxyStreamWriter;
        //_eventHubClient = eventHubClient;
        _userPriority = userPriority ?? throw new ArgumentNullException(nameof(userPriority));
        _options = backendOptions ?? throw new ArgumentNullException(nameof(backendOptions));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _timeoutHeaderName = _options.TimeoutHeader;
        if (_options.Client == null) throw new ArgumentNullException(nameof(_options.Client));
        s_backendKeys = _options.DependancyHeaders;
        _id = id;
        _idStr = id.ToString();
        _preferredPriority = priority;
        _healthCheckService = healthCheckService ?? throw new ArgumentNullException(nameof(healthCheckService));
        _sharedIteratorRegistry = sharedIteratorRegistry; // Optional - null if UseSharedIterators is false
    }

    /// <summary>
    /// Main worker loop: dequeues requests from priority queue and processes them through the proxy pipeline.
    /// Runs until cancellation is requested AND queue is empty (graceful shutdown).
    /// </summary>
    /// <remarks>
    /// <code>
    /// WORKER LIFECYCLE:
    /// ┌─────────────────────────────────────────────────────────────────────────────────┐
    /// │  STARTUP                                                                        │
    /// │  └─ IncrementActiveWorkers() ──► when all workers ready ──► s_readyToWork=true  │
    /// └────────────────────────────────────────┬────────────────────────────────────────┘
    ///                                          ▼
    /// ┌─────────────────────────────────────────────────────────────────────────────────┐
    /// │  MAIN LOOP (while !cancelled OR queue.Count > 0):                               │
    /// │  ┌───────────────────────────────────────────────────────────────────────────┐  │
    /// │  │ 1. DEQUEUE (blocks until request available or cancelled)                  │  │
    /// │  │    └─ DequeueAsync(_preferredPriority) ──► RequestData                    │  │
    /// │  │                                                                           │  │
    /// │  │ 2. HYDRATE (if recovered from blob)                                       │  │
    /// │  │    └─ RecoveryProcessor?.HydrateRequestAsync()                            │  │
    /// │  │                                                                           │  │
    /// │  │ 3. VALIDATE                                                               │  │
    /// │  │    ├─ Health probe? ──► HandleProbeRequestAsync() ──► CONTINUE            │  │
    /// │  │    └─ Invalid context? ──► skip ──► CONTINUE                              │  │
    /// │  │                                                                           │  │
    /// │  │ 4. PROCESS                                                                │  │
    /// │  │    ├─ TransitionToProcessing()                                            │  │
    /// │  │    ├─ EnrichRequestHeaders()                                              │  │
    /// │  │    └─ ProxyToBackEndAsync() ──► ProxyData                                 │  │
    /// │  │                                                                           │  │
    /// │  │ 5. HANDLE RESPONSE                                                        │  │
    /// │  │    ├─[412/408] ──► TransitionToExpired()                                  │  │
    /// │  │    ├─[200]     ──► TransitionToSuccess()                                  │  │
    /// │  │    └─[other]   ──► TransitionToFailed()                                   │  │
    /// │  │                                                                           │  │
    /// │  │ 6. WRITE RESPONSE                                                         │  │
    /// │  │    └─ WriteResponseAsync() ──► StreamResponseAsync()                      │  │
    /// │  │                                                                           │  │
    /// │  │ 7. FINALIZE                                                               │  │
    /// │  │    └─ FinalizeStatus() + asyncWorker?.UpdateBackup()                      │  │
    /// │  └───────────────────────────────────────────────────────────────────────────┘  │
    /// │                                          │                                      │
    /// │  EXCEPTION HANDLERS:                     │                                      │
    /// │  ├─ S7PRequeueException ──► DelayAsync() ──► re-enqueue after retry-after       │
    /// │  ├─ ProxyErrorException ──► TransitionToFailed() ──► write error to client      │
    /// │  ├─ IOException         ──► TransitionToFailed() ──► 408 timeout                │
    /// │  ├─ TaskCanceledException (evicting) ──► AbortAsync()                           │
    /// │  └─ Exception           ──► TransitionToFailed() ──► 500 error                  │
    /// │                                          │                                      │
    /// │  FINALLY: Cleanup() + Dispose() if not requeued/evicting                        │
    /// └────────────────────────────────────────┬────────────────────────────────────────┘
    ///                                          ▼
    /// ┌─────────────────────────────────────────────────────────────────────────────────┐
    /// │  SHUTDOWN                                                                       │
    /// │  └─ DecrementActiveWorkers() ──► log worker stopped                             │
    /// └─────────────────────────────────────────────────────────────────────────────────┘
    /// </code>
    /// </remarks>
    public async Task TaskRunnerAsync()
    {
        bool doUserconfig = _options.UseProfiles;
        string workerState = string.Empty;

        if (doUserconfig && _profiles == null) throw new ArgumentNullException(nameof(_profiles));
        if (s_requestsQueue == null) throw new ArgumentNullException(nameof(s_requestsQueue));

        // Only for use during shutdown after graceseconds have expired
        // CancellationTokenSource cts = new CancellationTokenSource();
        // CancellationToken token = cts.Token;

        // increment the active workers count.   When all workers are active, the startup probe allows traffic. 
        if (_options.Workers == HealthCheckService.IncrementActiveWorkers(_options.Workers))
        {
            s_readyToWork = true;
            // Always display
            _logger.LogInformation("[READY] ✓ All workers ready to work");
        }

        // Run until cancellation is requested. (Queue emptiness is handled by the blocking DequeueAsync call.)
        while (!_cancellationToken.IsCancellationRequested || s_requestsQueue.thrdSafeCount > 0)
        {
            RequestData incomingRequest;

            try
            {
                HealthCheckService.EnterState(_id, WorkerState.Dequeuing);
                workerState = "Waiting";

                // This will block until an item is available or the token is cancelled
                incomingRequest = await s_requestsQueue.DequeueAsync(_preferredPriority).ConfigureAwait(false);
                if (incomingRequest == null)
                {
                    continue;
                }
                _logger.LogTrace("[Worker:{Id}] Dequeued request {Guid} - Priority: {Priority}, Type: {Type}",
                    _id, incomingRequest.Guid, incomingRequest.Priority, incomingRequest.Type);
            }
            catch (OperationCanceledException)
            {
                //_logger.LogInformation("Operation was cancelled. Stopping the worker.");
                break; // Exit the loop if the operation is cancelled
            }

            if (incomingRequest.RecoveryProcessor != null)
            {
                incomingRequest.DequeueTime = DateTime.UtcNow;
                // Call the recovery processor to rehydrate the request from Blob storage
                await incomingRequest.RecoveryProcessor.HydrateRequestAsync(incomingRequest);
            }

            if (!incomingRequest.Requeued)
            {
                incomingRequest.DequeueTime = DateTime.UtcNow;
            }
            incomingRequest.Requeued = false;  // reset this flag for this round of activity
            bool abortTask = false;

            await using (incomingRequest)
            {
                bool isExpired = false;

                HealthCheckService.EnterState(_id, WorkerState.PreProcessing);
                workerState = "Processing";

                var lcontext = incomingRequest.Context;

                if (!incomingRequest.AsyncHydrated && (lcontext == null || incomingRequest == null))
                {
                    _logger.LogWarning("[Worker:{Id}] Skipping invalid request {Guid} - Context or Request is null.", _id, incomingRequest!.Guid);
                    HealthCheckService.EnterState(_id, WorkerState.Cleanup);
                    continue;
                }

                var eventData = incomingRequest.EventData;
                ProxyData pr = null!;
                try
                {
                    if (Constants.probes.Contains(incomingRequest.Path))
                    {
                        await HandleProbeRequestAsync(incomingRequest, lcontext!);
                        HealthCheckService.EnterState(_id, WorkerState.Cleanup);

                        continue;
                    }


                    // Set the initial status based on request type
                    _lifecycleManager.TransitionToProcessing(incomingRequest);

                    // Enrich headers and populate initial event data
                    _eventDataBuilder.EnrichRequestHeaders(incomingRequest, _idStr);
                    _eventDataBuilder.PopulateInitialEventData(incomingRequest);

                    HealthCheckService.EnterState(_id, WorkerState.Proxying);
                    workerState = "Read Proxy";

                    //  Do THE WORK:  FIND A BACKEND AND SEND THE REQUEST
                    try
                    {
                        pr = await ProxyToBackEndAsync(incomingRequest).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (!_isEvictingAsyncRequest)
                        {
                            _eventDataBuilder.PopulateProxyEventData(incomingRequest, pr);
                        }
                    }

                    // POST PROCESSING ... logging
                    HealthCheckService.EnterState(_id, WorkerState.Writing);
                    workerState = "Write Response";

                    //                    Task.Yield(); // Yield to the scheduler to allow other tasks to run

                    var statusCodeInt = (int)pr.StatusCode;
                    eventData.Status = pr.StatusCode;
                    _eventDataBuilder.PopulateHeaderEventData(incomingRequest, pr.Headers);

                    // Update status based on response ( in async mode )
                    switch (pr.StatusCode)
                    {
                        case HttpStatusCode.PreconditionFailed:
                        case HttpStatusCode.RequestTimeout: // 412 or 408
                            isExpired = true;
                            _lifecycleManager.TransitionToExpired(incomingRequest);
                            eventData.Type = EventType.ProxyRequestExpired;
                            break;

                        case HttpStatusCode.OK:
                            _lifecycleManager.TransitionToSuccess(incomingRequest, pr.StatusCode);
                            break;

                        default:  // Non-200, non-expired response - handle failures
                            _lifecycleManager.TransitionToFailed(incomingRequest, pr.StatusCode);
                            break;
                    }
                    

                    // Connect the streams and write the response to the client
                    await WriteResponseAsync(incomingRequest, pr).ConfigureAwait(false);

                    //                    Task.Yield(); // Yield to the scheduler to allow other tasks to run
                    HealthCheckService.EnterState(_id, WorkerState.Reporting);
                    workerState = "Finalize";

                    var conlen = pr.ContentHeaders?["Content-Length"] ?? "N/A";
                    var proxyLatency = (DateTime.UtcNow - incomingRequest.DequeueTime).TotalMilliseconds.ToString("F3");

                    _logger.LogCritical("Pri: {Priority}, Stat: {StatusCode}, User: {user} Guid: {Guid} Type: {RequestType}, Processor: {Processor}, Len: {ContentLength}, {FullURL}, Deq: {DequeueTime}, Lat: {ProxyTime} ms",
                        incomingRequest.Priority, statusCodeInt,
                        incomingRequest.UserID ?? "N/A",
                        incomingRequest.Guid,
                        incomingRequest.Type,
                        pr.StreamingProcessor,
                        conlen, pr.FullURL, incomingRequest.DequeueTime.ToLocalTime().ToString("T"), proxyLatency);

                    // Log circuit breaker details when status code is -1
                    if (statusCodeInt == -1 || statusCodeInt == 503)
                    {
                        _logger.LogCritical("[CircuitBreaker] Status {StatusCode} detected for request {Guid}. Backend host: {HFstreamost}",
                            statusCodeInt, incomingRequest.Guid, pr.BackendHostname);

                        // Log circuit breaker status for all hosts
                        var activeHosts = _backends.GetActiveHosts();
                        foreach (var host in activeHosts)
                        {
                            var cbStatus = host.Config.GetCircuitBreakerStatusString();
                            _logger.LogCritical("[CircuitBreaker] Guid: {guid} Host {HostName}: {status}",
                                incomingRequest.Guid, host.Host, cbStatus);
                        }
                    }

                    // Populate final event data
                    _eventDataBuilder.PopulateFinalEventData(incomingRequest, lcontext);

                    HealthCheckService.EnterState(_id, WorkerState.Cleanup);
                    workerState = "Cleanup";

                    // Finalize status for non-background requests
                    if (_lifecycleManager.ShouldFinalize(incomingRequest))
                    {
                        var isSuccessfulResponse = ((int)pr.StatusCode == 200 ||
                                                    (int)pr.StatusCode == 206 || // Partial Content
                                                    (int)pr.StatusCode == 201 || // Created
                                                    (int)pr.StatusCode == 202);  // Accepted

                        // For async requests, wait for blob writes to complete BEFORE
                        // sending "Completed" status — otherwise the client may try to
                        // read the blob before it exists in storage.
                        if (incomingRequest.runAsync && incomingRequest.asyncWorker != null)
                        {
                            await incomingRequest.asyncWorker.WaitForBlobWritesAsync().ConfigureAwait(false);
                        }

                        _lifecycleManager.FinalizeStatus(incomingRequest, isSuccessfulResponse);
                        incomingRequest.asyncWorker?.UpdateBackup();
                    }
                    // Background check requests skip ShouldFinalize but still need
                    // Completed status after blob writes confirm
                    else if (incomingRequest.Type == RequestType.AsyncBackgroundCheck &&
                             incomingRequest.BackgroundRequestCompleted &&
                             incomingRequest.asyncWorker != null)
                    {
                        await incomingRequest.asyncWorker.WaitForBlobWritesAsync().ConfigureAwait(false);
                        _lifecycleManager.FinalizeBackgroundCheckStatus(incomingRequest);
                        incomingRequest.asyncWorker?.UpdateBackup();
                    }

                }
                catch (S7PRequeueException e)
                {
                    // launches a delay task while the current worker goes back to the top of the loop for more work
                    _lifecycleManager.TransitionToRequeued(incomingRequest);
                    s_requeueDelayWorker!.DelayAsync(incomingRequest, e.RetryAfter);

                }
                catch (ProxyErrorException e)
                {
                    _lifecycleManager.TransitionToFailed(incomingRequest, e.StatusCode, e.Message);

                    // Handle proxy error
                    eventData.Status = e.StatusCode;
                    eventData["Error"] = "Proxy Exception";
                    eventData.Type = EventType.Exception;
                    eventData.Exception = e;

                    if (lcontext == null)
                    {
                        _logger.LogError("Context is null in ProxyErrorException");
                        continue;
                    }

                    if (await WriteErrorToClientAsync(lcontext, e.StatusCode, e.Message, eventData, incomingRequest?.Guid))
                    {
                        _logger.LogWarning("Proxy error: {Message}", e.Message);
                    }
                }

                catch (IOException ioEx)
                {
                    if (isExpired)
                    {
                        _logger.LogError("IoException on an expired request");
                    }
                    else
                    {
                        _lifecycleManager.TransitionToFailed(incomingRequest, HttpStatusCode.RequestTimeout, $"IO Exception: {ioEx.Message}");

                        eventData.Status = HttpStatusCode.RequestTimeout; // 408 Request Timeout
                        eventData.Type = EventType.Exception;
                        eventData.Exception = ioEx;
                        var errorMessage = $"IO Exception: {ioEx.Message}";
                        eventData["ErrorDetails"] = errorMessage;

                        if (lcontext == null)
                        {
                            _logger.LogError("Context is null in IOException");
                            continue;
                        }

                        if (await WriteErrorToClientAsync(lcontext, HttpStatusCode.RequestTimeout, errorMessage, eventData, incomingRequest?.Guid))
                        {
                            _logger.LogError(ioEx, "An IO exception occurred for request {Guid}", incomingRequest?.Guid);
                        }
                    }
                }
                catch (TaskCanceledException)
                {
                    if (_isEvictingAsyncRequest)
                    {
                        abortTask = true;
                    }
                    else
                    {
                        _lifecycleManager.TransitionToFailed(incomingRequest, HttpStatusCode.RequestTimeout, "Task cancelled");
                    }
                }
                catch (Exception ex)
                {
                    if (isExpired)
                    {
                        _logger.LogError("Exception on an expired request");
                    }
                    else
                    {
                        _logger.LogError(ex, "Unhandled exception in worker for request {Guid}", incomingRequest?.Guid);

                        if (incomingRequest != null)
                        {
                            _lifecycleManager.TransitionToFailed(incomingRequest, HttpStatusCode.InternalServerError, ex.Message);
                        }

                        eventData.Status = HttpStatusCode.InternalServerError; // 500 Internal Server Error
                        eventData.Type = EventType.Exception;
                        eventData.Exception = ex;
                        eventData["WorkerState"] = workerState;

                        if (ex.Message.StartsWith("Cannot access a disposed object") || ex.Message.StartsWith("Unable to write data") || ex.Message.Contains("Broken Pipe")) // The client likely closed the connection
                        {
                            _logger.LogInformation("Client closed connection: {FullURL}", incomingRequest?.FullURL ?? "Unknown");
                            eventData["InnerErrorDetail"] = "Client Disconnected";
                        }
                        else
                        {
                            // Set an appropriate status code for the error
                            var errorMessage = $"Exception: {ex.Message}";
                            eventData["ErrorDetails"] = errorMessage;

                            if (lcontext == null)
                            {
                                _logger.LogError("Context is null in General Exception");
                                continue;
                            }

                            await WriteErrorToClientAsync(lcontext, HttpStatusCode.InternalServerError, errorMessage, eventData, incomingRequest?.Guid);
                        }
                    }
                }
                finally
                {
                    try
                    {
                        // Dispose ProxyData to release HttpResponseMessage and body byte arrays.
                        // Must be in finally — exception paths were previously leaking this.
                        pr?.Dispose();

                        if (abortTask)
                        {
                            if (incomingRequest.asyncWorker != null)
                            {
                                await incomingRequest.asyncWorker.AbortAsync().ConfigureAwait(false);
                                incomingRequest.asyncWorker = null;
                            }
                            else
                            {
                                _logger.LogError("Task was aborted but asyncWorker is null");
                            }
                        }

                        // Cleanup request if appropriate
                        if (_lifecycleManager.ShouldCleanup(incomingRequest, incomingRequest.Requeued, _isEvictingAsyncRequest))
                        {
                            _logger.LogDebug("[Worker:{Id}] Performing cleanup for request {Guid}", _id, incomingRequest.Guid);

                            if (workerState != "Cleanup")
                                eventData["WorkerState"] = workerState;

                            incomingRequest.Cleanup();

                            try
                            {
                                incomingRequest.Dispose(); // Dispose of the request data
                            }
                            catch (Exception disposeEx)
                            {
                                _logger.LogError(disposeEx, "Failed to dispose of request data for {Guid}", incomingRequest?.Guid);
                            }
                        }
                        else
                        {
                            _logger.LogDebug("[Worker:{Id}] Cleanup skipped for request {Guid} - Requeued: {IsRequeued}, Evicting: {IsEvicting}",
                                _id, incomingRequest.Guid, incomingRequest.Requeued, _isEvictingAsyncRequest);
                        }
                    }
                    catch (Exception e)
                    {
                        // Reuse static error event
                        s_finallyBlockErrorEvent.Clear();
                        
                        // Copy fields from eventData
                        foreach (var kvp in eventData)
                        {
                            s_finallyBlockErrorEvent[kvp.Key] = kvp.Value;
                        }
                        
                        // Set error-specific properties
                        s_finallyBlockErrorEvent.Type = EventType.Exception;
                        s_finallyBlockErrorEvent.Exception = e;
                        s_finallyBlockErrorEvent.Status = HttpStatusCode.InternalServerError;
                        s_finallyBlockErrorEvent.MID = eventData.MID;
                        s_finallyBlockErrorEvent.ParentId = eventData.ParentId;
                        s_finallyBlockErrorEvent.Method = eventData.Method;
                        s_finallyBlockErrorEvent.Duration = eventData.Duration;
                        s_finallyBlockErrorEvent.Uri = eventData.Uri;
                        s_finallyBlockErrorEvent["WorkerState"] = workerState;
                        s_finallyBlockErrorEvent["Message"] = e.Message;
                        s_finallyBlockErrorEvent["StackTrace"] = e.StackTrace ?? "No Stack Trace";
                        
                        s_finallyBlockErrorEvent.SendEvent();
                        _logger.LogError(e, "[Worker:{Id}] CRITICAL: Unhandled error in finally block for request {Guid}", _id, incomingRequest!.Guid);
                    }

                }
            }   // lifespan of incomingRequest
        }       // while running loop

        HealthCheckService.DecrementActiveWorkers(_id);

        _logger.LogDebug("[SHUTDOWN] ✓ Worker {IdStr} stopped", _idStr);

    }


    private async Task WriteResponseAsync(RequestData request, ProxyData pr)
    {
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentNullException.ThrowIfNull(request, "Request context is null.");
        ArgumentNullException.ThrowIfNull(request.Context, "Request context is null.");

        var context = request.Context;

        // For async requests that triggered, the 202 Accepted response was already sent and 
        // the connection was closed by AsyncWorker. Skip writing to the HttpListenerResponse.
        // The actual backend response will be streamed to blob storage in StreamResponseAsync.
        if (!request.AsyncTriggered)
        {
            // Set the response status code
            context.Response.StatusCode = (int)pr.StatusCode;

            // Copy headers to the response
            //ProxyHelperUtils.CopyHeaders(request.Headers, proxyRequest, true, _options.StripRequestHeaders);

            //CopyHeadersToResponse(pr.Headers, context.Response.Headers);            // Already done?

            // Set content-specific headers
            if (pr.ContentHeaders != null)
            {
                foreach (var key in pr.ContentHeaders.AllKeys)
                {
                    switch (key.ToLower())
                    {
                        case "content-length":
                            var length = pr.ContentHeaders[key];
                            if (long.TryParse(length, out var contentLength))
                            {
                                context.Response.ContentLength64 = contentLength;
                            }
                            else
                            {
                                _logger.LogWarning("Invalid Content-Length: {Length}", length);
                            }
                            break;

                        case "content-type":
                            context.Response.ContentType = pr.ContentHeaders[key];
                            break;

                        default:
                            context.Response.Headers[key] = pr.ContentHeaders[key];
                            break;
                    }
                }
            }

            context.Response.KeepAlive = false;
        }

        // we need 3 things:
        // 1. The processor to use                      => pr.StreamingProcessor
        // 2. The source stream (from backend)          => pr.BodyResponseMessage
        // 3. The destination stream (to client/blob)   => incomingRequest.OutputStream        

        // Stream response from backend to client/blob
        try
        {
            await StreamResponseAsync(request, pr).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[WriteResponseAsync:{Guid}] Error streaming response to {FullURL}",
                request.Guid, request.FullURL);
            throw;
        }

        try
        {
            _logger.LogDebug("[WriteResponseAsync:{Guid}] Flushing output stream", request.Guid);
            if (request.OutputStream != null)
            {
                await request.OutputStream.FlushAsync().ConfigureAwait(false);

                if (request.OutputStream is BufferedStream bufferedStream)
                {
                    await bufferedStream.FlushAsync().ConfigureAwait(false);
                }
            }
            _logger.LogDebug("[WriteResponseAsync:{Guid}] Output stream flushed successfully", request.Guid);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "[WriteResponseAsync:{Guid}] Unable to flush output stream", request.Guid);
        }
    }

    public void ExpelAsyncRequest()
    {
        if (_asyncExpelSource != null)
        {
            // Called during shutdown to evict any in-progress async requests ... then worker will exit
            _isEvictingAsyncRequest = true;
            _logger.LogDebug("Expelling async request in progress, cancelling the token.");
            try
            {
                _asyncExpelSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // CTS was already disposed - worker has finished or encountered an error
                _logger.LogDebug("AsyncExpelSource already disposed - worker has completed");
            }
        }

    }

    private async Task HandleProbeRequestAsync(RequestData req, HttpListenerContext lcontext)
    {
        int hostCount = _backends.ActiveHostCount();
        bool hasFailedHosts = _backends.CheckFailedStatus();
        _healthCheckService.BuildHealthResponse(req.Path, hostCount, hasFailedHosts, out int probeStatus, out string probeMessage);

        lcontext.Response.StatusCode = probeStatus;
        lcontext.Response.ContentType = "text/plain";
        lcontext.Response.Headers.Add("Cache-Control", "no-cache");
        lcontext.Response.KeepAlive = false;

        var healthMessage = Encoding.UTF8.GetBytes(probeMessage);
        lcontext.Response.ContentLength64 = healthMessage.Length;

        await lcontext.Response.OutputStream.WriteAsync(
            healthMessage,
            0,
            healthMessage.Length).ConfigureAwait(false);
    }


    /// <summary>
    /// Routes a request to an available backend host using configured load balancing and iteration strategies.
    /// Iterates through matching hosts until one succeeds or all fail. Handles circuit breaker checks,
    /// OAuth token injection, request timeout management, and async worker coordination for long-running requests.
    /// </summary>
    /// <param name="request">The request containing body, headers, method, and execution mode flags (runAsync, IsBackground, IsBackgroundCheck)</param>
    /// <returns>ProxyData with response status, headers, content metadata, and backend hostname</returns>
    /// <exception cref="ArgumentNullException">When request, Body, Headers, or Method is null</exception>
    /// <exception cref="ProxyErrorException">When all hosts fail, request TTL expires (412), or no matching hosts found</exception>
    /// <exception cref="S7PRequeueException">When backend returns 429 with S7PREQUEUE header; includes retry-after delay</exception>
    /// <remarks>
    /// <para>For async requests (runAsync=true), creates AsyncWorker to write response to blob storage.
    /// Supports SinglePass (try each host once) and MultiPass (retry with MaxAttempts) iteration modes.</para>
    /// <code>
    /// ALGORITHM FLOW:
    /// ┌─────────────────────────────────────────────────────────────────────────┐
    /// │  REQUEST ENTRY                                                          │
    /// │  ├─ Validate: Body, Headers, Method not null                            │
    /// │  └─ Create host iterator (SinglePass or MultiPass mode)                 │
    /// └───────────────────────────────┬─────────────────────────────────────────┘
    ///                                 ▼
    /// ┌─────────────────────────────────────────────────────────────────────────┐
    /// │  FOR EACH HOST in iterator:                                             │
    /// │  ┌───────────────────────────────────────────────────────────────────┐  │
    /// │  │ 1. Circuit Breaker Check ──[OPEN]──► SKIP to next host            │  │
    /// │  │         │                                                         │  │
    /// │  │      [CLOSED]                                                     │  │
    /// │  │         ▼                                                         │  │
    /// │  │ 2. TTL Check ──[EXPIRED]──► throw ProxyErrorException (412)       │  │
    /// │  │         │                                                         │  │
    /// │  │      [VALID]                                                      │  │
    /// │  │         ▼                                                         │  │
    /// │  │ 3. OAuth Token? ──[YES]──► Inject Bearer token                    │  │
    /// │  │         │                                                         │  │
    /// │  │         ▼                                                         │  │
    /// │  │ 4. Setup AsyncWorker (if runAsync) + CancellationToken            │  │
    /// │  │         │                                                         │  │
    /// │  │         ▼                                                         │  │
    /// │  │ 5. SEND REQUEST ──────────────────────────────────────────────►   │  │
    /// │  │         │                                          [Backend]      │  │
    /// │  │         ◄─────────────────────────────────────────────────────    │  │
    /// │  │         │                                                         │  │
    /// │  │         ▼                                                         │  │
    /// │  │ 6. Response Status Check:                                         │  │
    /// │  │    ├─[3xx, 404, 412, 5xx]──► CONTINUE to next host                │  │
    /// │  │    ├─[429 + S7PREQUEUE]───► Collect for retry, CONTINUE           │  │
    /// │  │    └─[2xx SUCCESS]────────► Capture response, RETURN ProxyData    │  │
    /// │  └───────────────────────────────────────────────────────────────────┘  │
    /// └───────────────────────────────┬─────────────────────────────────────────┘
    ///                                 ▼
    /// ┌─────────────────────────────────────────────────────────────────────────┐
    /// │  ALL HOSTS EXHAUSTED:                                                   │
    /// │  ├─ If 429s collected ──► throw S7PRequeueException (shortest retry)    │
    /// │  └─ Else ──► throw ProxyErrorException (503 ServiceUnavailable)         │
    /// └─────────────────────────────────────────────────────────────────────────┘
    /// </code>
    /// </remarks>
    public async Task<ProxyData> ProxyToBackEndAsync(RequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Body, nameof(request.Body));
        ArgumentNullException.ThrowIfNull(request.Headers, nameof(request.Headers));
        ArgumentNullException.ThrowIfNull(request.Method, nameof(request.Method));

        _logger.LogDebug("[ProxyToBackEnd:{Guid}] Starting proxy attempt - Path: {Path}, Method: {Method}",
            request.Guid, request.Path, request.Method);

        List<Dictionary<string, string>> incompleteRequests = request.incompleteRequests;

        request.Debug = s_debug || (request.Headers["S7PDEBUG"] != null && string.Equals(request.Headers["S7PDEBUG"], "true", StringComparison.OrdinalIgnoreCase));
        HttpStatusCode lastStatusCode = HttpStatusCode.ServiceUnavailable;
        var requestSummary = request.EventData;
        int intCode = 0;

        // Read the body stream once and reuse it
        //byte[] bodyBytes = await request.CachBodyAsync().ConfigureAwait(false);
        List<S7PRequeueException> retryAfter = new();

        var (hostIterator, sharedIterator, modifiedPath) = CreateHostIterator(request);

        request.Path = modifiedPath;

        // Use the host count from the already-created iterator (avoids redundant GetActiveHosts call
        // and fixes a bug where the old code compared stripped path against configured PartialPath)
        var matchingHostCount = sharedIterator?.HostCount ?? hostIterator?.HostCount ?? 0;
        _logger.LogDebug("[ProxyToBackEnd:{Guid}] Found {HostCount} backend hosts for path {Path}",
            request.Guid, matchingHostCount, request.Path);

        if (matchingHostCount == 0)
        {
            _logger.LogWarning("[ProxyToBackEnd:{Guid}] ⚠ NO BACKEND HOSTS matched path {Path} - Request will fail",
                request.Guid, request.Path);
            
            // Log all available hosts and their paths for debugging
            var activeHosts = _backends.GetActiveHosts();
            _logger.LogCritical("[ProxyToBackEnd:{Guid}] Available hosts and their paths:", request.Guid);
            foreach (var h in activeHosts)
            {
                var cbStatus = h.Config.GetCircuitBreakerStatusString();
                _logger.LogCritical("[ProxyToBackEnd:{Guid}]   - Host: {Host}, Path: {PartialPath}, CB-Status: {CBStatus}",
                    request.Guid, h.Host, h.Config.PartialPath, cbStatus);
            }
        }

        // Try the request on each active host, stop if it worked
        // Use helper method to abstract over shared vs per-request iterators
        BaseHostHealth? host;
        while (TryGetNextHost(hostIterator, sharedIterator, out host) && host != null)
        {
            DateTime proxyStartDate = DateTime.UtcNow;

            if (host.Config.CheckFailedStatus())
            {
                var cbStatus = host.Config.GetCircuitBreakerStatusString();
                _logger.LogCritical("[ProxyToBackEnd:{Guid}] ⚠ Circuit breaker BLOCKING host: {Host} - CB-Status: {CBStatus}",
                    request.Guid, host.Host, cbStatus);
                continue;
            }

            // track the number of attempts
            request.BackendAttempts++;
            _logger.LogDebug("[ProxyToBackEnd:{Guid}] Attempting backend host: {Host} (Attempt #{Attempt})",
                request.Guid, host.Host, request.BackendAttempts);
            bool SuccessfulRequest = false;
            bool TriggerHostCB = true;
            string requestState = "Init";
            // bool newcode = false;
            ProxyEvent requestAttempt = null!;

            requestAttempt = new ProxyEvent(request.EventData)
            {
                Type = EventType.BackendRequest,
                ParentId = request.ParentId,
                MID = $"{request.MID}-{request.BackendAttempts}",
                Method = request.Method,
                ["Request-Date"] = DateTime.UtcNow.ToString("o"),
                ["Backend-Host"] = host.Host,
                ["Host-URL"] = host.Url,
                ["Attempt"] = request.BackendAttempts.ToString()
            };

            // Tracked as an attempt
            try
            {
                // if (request.Context?.Request.Url != null)
                //     requestAttempt.Uri = request.Context!.Request.Url!;
                // else
                requestAttempt.Uri = new Uri(modifiedPath);

                if (host.Config.UseOAuth)
                {
                    // Get a token
                    var oaToken = await host.Config.OAuth2Token().ConfigureAwait(false);
                    if (request.Debug)
                    {
                        _logger.LogDebug("OAuth Token retrieved for backend {BackendHost}", host.Host);
                    }
                    // Set the token in the headers
                    request.Headers.Set("Authorization", $"Bearer {oaToken}");
                }

                requestState = "Calc ExpiresAt";

                // Validate request hasn't expired
                _lifecycleManager.ValidateRequestNotExpired(request);  // throws ProxyErrorException

                var minDate = request.ExpiresAt < DateTime.UtcNow.AddMilliseconds(request.defaultTimeout)
                    ? request.ExpiresAt
                    : DateTime.UtcNow.AddMilliseconds(request.defaultTimeout);
                request.Timeout = (int)(minDate - DateTime.UtcNow).TotalMilliseconds;

                request.Headers.Set("Host", host.Hostname);
                request.FullURL = host.Config.BuildDestinationUrl(request.Path);

                requestState = "Cache Body";
                // Read the body stream once and reuse it
                byte[] bodyBytes = await request.CacheBodyAsync().ConfigureAwait(false);

                requestState = "Create Backend Request";

                using (ByteArrayContent bodyContent = new(bodyBytes))
                using (HttpRequestMessage proxyRequest = new(new(request.Method), request.FullURL))
                {
                    proxyRequest.Content = bodyContent;

                    proxyRequest.Headers.Add("x-PolicyCycleCounter", request.TotalDownstreamAttempts.ToString());
                    ProxyHelperUtils.CopyHeaders(request.Headers, proxyRequest, true, _options.StripRequestHeaders);

                    var contentType = request.Context?.Request.ContentType ?? "application/json";
                    if (!MediaTypeHeaderValue.TryParse(contentType, out var req_mediaType))
                    {
                        _logger.LogInformation("Invalid content type '{ContentType}', defaulting to application/json", contentType);
                        req_mediaType = new MediaTypeHeaderValue("application/json");
                    }
                    req_mediaType.CharSet ??= "utf-8";
                    proxyRequest.Content.Headers.ContentType = req_mediaType;

                    if (bodyBytes.Length > 0)
                        proxyRequest.Content.Headers.ContentLength = bodyBytes.Length;

                    //proxyRequest.Headers.ConnectionClose = true;

                    // Log request headers if debugging is enabled
                    if (request.Debug)
                    {
                        _logger.LogDebug("> {Method} {FullURL} {BodyLength} bytes",
                            request.Method, request.FullURL, bodyBytes.Length);
                        ProxyHelperUtils.LogHeaders(proxyRequest.Headers, ">", _logger);
                        ProxyHelperUtils.LogHeaders(proxyRequest.Content.Headers, "  >", _logger);
                        //string bodyString = System.Text.Encoding.UTF8.GetString(bodyBytes);
                        //Console.WriteLine($"Body Content: {bodyString}");
                    }

                    // Send the request and get the response
                    proxyStartDate = DateTime.UtcNow;
                    HealthCheckService.EnterState(_id, WorkerState.Sending);
                    try
                    {
                        // ASYNC: Calculate the timeout, start async worker
                        _isEvictingAsyncRequest = false;
                        requestState = "Backend Attempt ";


                        // Create ASYNC Worker if needed, and setup the timeout
                        // SEND THE REQUEST TO THE BACKEND USING THE APROPRIATE TIMEOUT.
                        // TO DO:   reuse the cts instead of creating a new one each time.
                        var (requestCts, rTimeout) = await SetupAsyncWorkerAndTimeout(request).ConfigureAwait(false);
                        DateTime responseDate;
                        using (requestCts)
                        {
                            HealthCheckService.EnterState(_id, WorkerState.Receiving);

                            // DO NOT ADD A USING BLOCK HERE - we need to process the response outside of this block

                            var proxyResponse = await _options.Client!.SendAsync(
                                proxyRequest, HttpCompletionOption.ResponseHeadersRead, requestCts.Token).ConfigureAwait(false);
                            responseDate = DateTime.UtcNow;
                            lastStatusCode = proxyResponse.StatusCode;
                            requestAttempt.Status = proxyResponse.StatusCode;

                            _logger.LogDebug("[ProxyToBackEnd:{Guid}] Received response from {Host} - Status: {StatusCode}, Duration: {Duration}ms",
                                request.Guid, host.Host, lastStatusCode, (responseDate - proxyStartDate).TotalMilliseconds);

                            requestState = "Process Backend Response";

                            // Check if the status code of the response is in the set of allowed status codes, else try the next host
                            intCode = (int)proxyResponse.StatusCode;
                            if ((intCode > 300 && intCode < 400) || intCode == 404 || intCode == 412 || intCode >= 500)
                            {
                                requestState = $"Backend proxy status code: {intCode}";

                                // 404 is not considered an error for circuit breaker purposes
                                if (intCode == 404)
                                    TriggerHostCB = false;

                                foreach (var header in proxyResponse.Headers)
                                {
                                    if (s_excludedHeaders.Contains(header.Key)) continue;
                                    requestAttempt[header.Key] = string.Join(", ", header.Value);
                                    //Console.WriteLine("requestAttempt[{0}] = {1}", header.Key, header.Value);
                                }

                                // The request did not succeed, try the next host
                                continue;
                            }

                            // Capture the response
                            ProxyData pr = new()
                            {
                                ResponseDate = responseDate,
                                StatusCode = lastStatusCode,
                                FullURL = request.FullURL,
                                CalculatedHostLatency = host.CalculatedAverageLatency,
                                BackendHostname = host.Host
                            };

                            host.AddPxLatency((responseDate - proxyStartDate).TotalMilliseconds);

                            // Capture the response
                            try
                            {
                                // ASYNC: Synchronize with the asyncWorker to clean up output stream assignments:
                                // Either abort overriding the stream or be ready to write to the blob.
                                if (request.runAsync && request.asyncWorker != null && !await request.asyncWorker.Synchronize())
                                {
                                    _logger.LogWarning("[ProxyToBackEnd:{Guid}] AsyncWorker synchronization failed - Error: {Error}",
                                        request.Guid, request.asyncWorker.ErrorMessage);
                                    pr.Headers["x-Async-Error"] = request.asyncWorker.ErrorMessage;
                                    request.SBStatus = ServiceBusMessageStatusEnum.AsyncProcessingError;
                                }

                                requestState = "Capture Proxy Response";

                                string resp_mediaType = proxyResponse.Content?.Headers?.ContentType?.MediaType ?? string.Empty;

                                _logger.LogDebug("[GetProxyResponseAsync:{Guid}] Processor: {Processor}, MediaType: {MediaType}",
                                    request.Guid, pr.StreamingProcessor, resp_mediaType);

                                // Determine stream processor
                                pr.StreamingProcessor = host.Config.DirectMode
                                    ? host.Config.Processor
                                    : StreamProcessorFactory.DetermineStreamProcessor(proxyResponse, resp_mediaType);

                                requestState = $"{(host.Config.DirectMode ? "Direct Mode Processor" : "Stream Proxy Response")} : {pr.StreamingProcessor}";
                                await CaptureResponseStream(proxyResponse, request, pr).ConfigureAwait(false);
                                requestState = "Finalize Proxy Response";
                            }
                            finally
                            {
                                requestSummary["Backend-Host"] = pr.BackendHostname;
                                requestSummary["Request-Queue-Duration"] = request.Headers["x-Request-Queue-Duration"] ?? "N/A";
                                requestSummary["Request-Process-Duration"] = request.Headers["x-Request-Process-Duration"] ?? "N/A";
                                requestSummary["Total-Latency"] = (DateTime.UtcNow - request.EnqueueTime).TotalMilliseconds.ToString("F3");
                            }


                            if (proxyResponse.Headers.TryGetValues("x-PolicyCycleCounter", out var policyAttempts))
                            {
                                if (int.TryParse(policyAttempts.FirstOrDefault(), out var pAttempts))
                                {
                                    request.TotalDownstreamAttempts = pAttempts;
                                }
                            }

                            var (shouldRequeue, retryMs) = CheckRequeueResponse(proxyResponse, intCode, requestAttempt, ref requestState);

                            if (shouldRequeue)
                            {
                                throw new S7PRequeueException("Requeue request", pr, retryMs);
                            }
                            else if (intCode == 429)
                            {
                                // S7PREQUEUE was not "true" — try next host
                                continue;
                            }
                            else
                            {
                                // request was successful, so we can disable the skip
                                request.SkipDispose = false;
                                requestAttempt["RequestSuccess"] = "true"; // Track success in event data
                                bodyBytes = [];
                            }

                            pr.Headers["BackendHost"] = requestSummary["Backend-Host"] = pr.BackendHostname;
                            pr.Headers["Request-Queue-Duration"] = requestSummary["Request-Queue-Duration"] = request.Headers["x-Request-Queue-Duration"] ?? "N/A";
                            pr.Headers["Request-Process-Duration"] = requestSummary["Request-Process-Duration"] = request.Headers["x-Request-Process-Duration"] ?? "N/A";
                            pr.Headers["Total-Latency"] = requestSummary["Total-Latency"] = (DateTime.UtcNow - request.EnqueueTime).TotalMilliseconds.ToString("F3");

                            // Strip headers from pr.Headers that are not allowed in the response
                            foreach (var header in _options.StripResponseHeaders)
                            {
                                pr.Headers.Remove(header);
                            }

                            // Log the response if debugging is enabled
                            if (request.Debug)
                            {
                                _logger.LogDebug("Got: {StatusCode} {FullURL} {ContentLength} Body: {BodyLength} bytes",
                                    pr.StatusCode, pr.FullURL, pr.ContentHeaders["Content-Length"], pr?.Body?.Length);
                            }

                            SuccessfulRequest = true;
                            TriggerHostCB = false;
                            return pr ?? throw new ArgumentNullException(nameof(pr));
                        }   // closes the using on requestCts
                    }
                    finally
                    {
                        // State will be automatically cleaned up when entering next state
                        // or when worker shuts down via DecrementActiveWorkers
                    }
                }
            }
            catch (S7PRequeueException e)
            {
                TriggerHostCB = false;
                intCode = (int)HttpStatusCode.TooManyRequests; // 429
                PopulateRequestAttemptError(requestAttempt, HttpStatusCode.TooManyRequests,
                    $"Requeue request: Retry-After = {e.RetryAfter}",
                    "Will retry if no other hosts are available");

                // Try all the hosts before sleeping
                retryAfter.Add(e);
                continue;
            }
            catch (ProxyErrorException e)
            {
                PopulateRequestAttemptError(requestAttempt, e.StatusCode, e.Message);
                intCode = (int)e.StatusCode;

                if (e.Type == ProxyErrorException.ErrorType.TTLExpired)
                {
                    intCode = 412;//(int)HttpResponseCode.PreconditionFailed; // 412
                    lastStatusCode = HttpStatusCode.PreconditionFailed;
                    TriggerHostCB = false;

                    break;
                }

                continue;
            }
            catch (TaskCanceledException) when (_isEvictingAsyncRequest)
            {
                TriggerHostCB = false;
                _logger.LogWarning("[Worker:{Id}] Request {Guid} was intentionally expelled to prioritize a new async request.", _id, request.Guid);
                // Handle async expel case - request being evicted from memory
                if (request.asyncWorker != null)
                {
                    request.asyncWorker.ShouldReprocess = true;
                }

                PopulateRequestAttemptError(requestAttempt, HttpStatusCode.ServiceUnavailable,
                    "Request being expelled",
                    "Request will rehydrate on startup");

                throw;
            }
            catch (Exception ex) when (ex is TaskCanceledException || ex is OperationCanceledException)
            {
                TriggerHostCB = false;
                // 408 Request Timeout - consolidates both TaskCanceledException and OperationCanceledException
                intCode = (int)HttpStatusCode.RequestTimeout; // 408
                PopulateTimeoutError(requestAttempt, request, proxyStartDate, ex is OperationCanceledException);
                continue;
            }
            catch (HttpRequestException e)
            {
                HttpStatusCode statusCode = ResolveHttpRequestErrorStatus(e);
                intCode = (int)statusCode;

                PopulateRequestAttemptError(requestAttempt, statusCode,
                    $"Bad Request: {e.Message}",
                    "Operation Exception: HttpRequest");

                requestState += $", statusCode = {statusCode}, HTTP Error Message: {e.Message}";
                continue;
            }
            catch (Exception e)
            {
                TriggerHostCB = false;

                if (IsInvalidHeaderException(e))
                {
                    throw new ProxyErrorException(ProxyErrorException.ErrorType.InvalidHeader,
                        HttpStatusCode.BadRequest, $"Bad header: {e.Message}");
                }
                
                // 500 Internal Server Error
                _logger.LogError(e, "Internal server error processing request {Guid} to {FullURL}",
                    request.Guid, request.FullURL);

                PopulateRequestAttemptError(requestAttempt, HttpStatusCode.InternalServerError,
                    $"Internal Error: {e.Message}");

                intCode = (int)HttpStatusCode.InternalServerError;
                requestState += ", Internal Error: " + e.Message;

                continue;
            }
            finally
            {
                // Add the request attempt to the summary
                requestAttempt.Duration = DateTime.UtcNow - proxyStartDate;
                requestAttempt.SendEvent();  // Log the dependent request attempt
                
                // Record result for iterator (shared or per-request)
                if (sharedIterator != null)
                    sharedIterator.RecordResult(host, SuccessfulRequest);
                else
                    hostIterator?.RecordResult(host, SuccessfulRequest);

                // Track host status for circuit breaker
                if (intCode != 412 && intCode != 429)
                    host.Config.TrackStatus(intCode, TriggerHostCB, "Attempt-" + request.BackendAttempts);

                if (!SuccessfulRequest)
                {
                    var miniDict = requestAttempt.ToDictionary(s_backendKeys);
                    miniDict["State"] = requestState;
                    incompleteRequests.Add(miniDict);

                    _logger.LogDebug(JsonSerializer.Serialize(miniDict));
                }

            }

            // continue to next host

        }

        // all hosts exhausted

        // If we get here, then no hosts were able to handle the request

        if (retryAfter.Count > 0)
        {
            // If we have retry after values, return the smallest one
            var exc = retryAfter.MinBy(x => x.RetryAfter);
            if (exc != null)
            {
                // NOTE:  this throws an S7PRequeueException which is caught in the main loop
                throw exc;
            }
        }

        StringBuilder sb;
        bool statusMatches;
        int currentStatusCode;
        ProxyHelperUtils.GenerateErrorMessage(incompleteRequests, out sb, out statusMatches, out currentStatusCode);

        // 502 Bad Gateway  or   call status code form all attempts ( if they are the same )
        lastStatusCode = (statusMatches) ? (HttpStatusCode)currentStatusCode : HttpStatusCode.BadGateway;
        // requestSummary.Type = EventType.ProxyError;

        // ASYNC: Synchronize with AsyncWorker if it was started, even for error responses
        // This ensures the 202 response was sent to client and blob streams are ready
        if (request.runAsync && request.asyncWorker != null)
        {
            _logger.LogDebug("[ProxyToBackEnd:{Guid}] Synchronizing with AsyncWorker before writing error response", request.Guid);
            if (!await request.asyncWorker.Synchronize())
            {
                _logger.LogWarning("[ProxyToBackEnd:{Guid}] AsyncWorker synchronization failed - Error: {Error}",
                    request.Guid, request.asyncWorker.ErrorMessage);
                // AsyncWorker failed to start, so AsyncTriggered will be false
                // Error response will go to HTTP context instead of blob
            }
            else
            {
                _logger.LogDebug("[ProxyToBackEnd:{Guid}] AsyncWorker synchronized successfully for error response", request.Guid);
            }
        }

        // Write error response to client (sync HTTP) or blob (async)
        await WriteExhaustedHostsErrorAsync(request, lastStatusCode, sb.ToString()).ConfigureAwait(false);


        return new ProxyData
        {
            FullURL = request.FullURL,

            CalculatedHostLatency = (DateTime.UtcNow - request.EnqueueTime).TotalMilliseconds,
            BackendHostname = "No Active Hosts Available",
            ResponseDate = DateTime.UtcNow,
            StatusCode = ProxyHelperUtils.RecordIncompleteRequests(requestSummary, lastStatusCode, "No active hosts were able to handle the request", incompleteRequests),
            Body = Encoding.UTF8.GetBytes(sb.ToString())
        };
    }


    private async Task CaptureResponseStream(HttpResponseMessage proxyResponse, RequestData request, ProxyData pr)
    {
        if (request.Debug)
        {
            _logger.LogInformation("< " + request.Guid);
            foreach (var header in proxyResponse.Headers)
            {
                _logger.LogInformation("  < {Key}: {Value}", header.Key, string.Join(", ", header.Value));
            }
        }

        // copy headers from the response to the ProxyData object
        ProxyHelperUtils.CopyResponseHeaders(proxyResponse, pr);
        pr.BodyResponseMessage = proxyResponse;

        // ASYNC: If the request was triggered asynchronously, we need to write the response to the async worker blob
        // For background checks, skip header writing here - will be written in StreamResponseAsync if completed

        if (request.AsyncTriggered && !request.IsBackgroundCheck)
        {
            _logger.LogDebug("[GetProxyResponseAsync:{Guid}] Writing headers to AsyncWorker blob", request.Guid);
            if (!await request.asyncWorker!.WriteHeaders(proxyResponse.StatusCode, pr.Headers))
            {
                throw new ProxyErrorException(ProxyErrorException.ErrorType.AsyncWorkerError,
                                            HttpStatusCode.InternalServerError, "Failed to write headers to async worker");
            }
            _logger.LogDebug("[GetProxyResponseAsync:{Guid}] Headers written successfully to AsyncWorker", request.Guid);
        }
        else if (!request.AsyncTriggered)
        {
            request.Context!.Response.StatusCode = (int)proxyResponse.StatusCode;
            request.Context.Response.Headers = pr.Headers;
        }

        return;
    }


    private void PopulateRequestAttemptError(
        ProxyEvent requestAttempt,
        HttpStatusCode status,
        string error,
        string? message = null)
    {
        requestAttempt.Status = status;
        requestAttempt["Error"] = error;
        if (message != null)
            requestAttempt["Message"] = message;
    }

    /// <summary>
    /// Creates a host iterator for routing requests to backend hosts.
    /// Uses shared iterators (fair distribution across concurrent requests) or per-request iterators
    /// based on configuration.
    /// </summary>
    /// <returns>A tuple of (per-request iterator, shared iterator, modified path). Exactly one iterator will be non-null.</returns>
    private (IHostIterator? hostIterator, ISharedHostIterator? sharedIterator, string modifiedPath) CreateHostIterator(RequestData request)
    {
        string modifiedPath = "";
        IHostIterator? hostIterator = null;
        ISharedHostIterator? sharedIterator = null;

        if (_options.UseSharedIterators && _sharedIteratorRegistry != null)
        {
            // Use shared iterator - multiple requests to same path share the same iterator
            // The modifiedPath is stored on the iterator itself, so we don't need a second filtering call
            sharedIterator = _sharedIteratorRegistry.GetOrCreate(
                request.Path,
                () =>
                {
                    var iterator = IteratorFactory.CreateSinglePassIterator(
                        _backends,
                        _options.LoadBalanceMode,
                        request.Path,
                        out var mp);
                    return (iterator, mp);
                });

            // Read modifiedPath from the shared iterator (computed once, cached)
            modifiedPath = sharedIterator.ModifiedPath;

            _logger.LogDebug(
                "[ProxyToBackEnd:{Guid}] Using SHARED iterator for path '{Path}' with {HostCount} hosts",
                request.Guid, request.Path, sharedIterator.HostCount);
        }
        else
        {
            // Use per-request iterator (original behavior)
            hostIterator = _options.IterationMode switch
            {
                IterationModeEnum.SinglePass => IteratorFactory.CreateSinglePassIterator(
                    _backends,
                    _options.LoadBalanceMode,
                    request.Path,
                    out modifiedPath),

                IterationModeEnum.MultiPass => IteratorFactory.CreateMultiPassIterator(
                    _backends,
                    _options.LoadBalanceMode,
                    _options.MaxAttempts,
                    request.Path,
                    out modifiedPath),

                _ => IteratorFactory.CreateSinglePassIterator(
                    _backends,
                    _options.LoadBalanceMode,
                    request.Path,
                    out modifiedPath)
            };
        }

        return (hostIterator, sharedIterator, modifiedPath);
    }

    /// <summary>
    /// Maps an HttpRequestException to the most appropriate HTTP status code by inspecting
    /// the exception's StatusCode, inner SocketException error codes, and error message text.
    /// </summary>
    private static HttpStatusCode ResolveHttpRequestErrorStatus(HttpRequestException e)
    {
        HttpStatusCode statusCode = e.StatusCode ?? HttpStatusCode.BadGateway;

        if (e.StatusCode != null)
            return statusCode;

        // Infer from inner SocketException
        if (e.InnerException is SocketException socketEx)
        {
            switch (socketEx.SocketErrorCode)
            {
                case SocketError.HostNotFound:
                case SocketError.TryAgain:
                case SocketError.NoData:
                    return HttpStatusCode.ServiceUnavailable; // 503
                case SocketError.TimedOut:
                    return HttpStatusCode.RequestTimeout; // 408
                case SocketError.ConnectionRefused:
                    return HttpStatusCode.BadGateway; // 502
            }
        }

        // Fallback to message parsing
        if (statusCode == HttpStatusCode.BadGateway)
        {
            if (e.Message.Contains("name or service not known", StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains("No such host is known", StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains("Temporary failure in name resolution", StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains("Name resolution failed", StringComparison.OrdinalIgnoreCase))
            {
                return HttpStatusCode.ServiceUnavailable; // 503
            }
            else if (e.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            {
                return HttpStatusCode.RequestTimeout; // 408
            }
        }

        return statusCode;
    }

    /// <summary>
    /// Checks whether a 429 response with the S7PREQUEUE header should trigger a requeue.
    /// Copies response headers into the request attempt event and parses retry-after timing.
    /// </summary>
    /// <returns>(shouldRequeue: true if S7PREQUEUE="true", retryMs: delay before requeue)</returns>
    private (bool shouldRequeue, int retryMs) CheckRequeueResponse(
        HttpResponseMessage proxyResponse,
        int intCode,
        ProxyEvent requestAttempt,
        ref string requestState)
    {
        if (intCode != 429 || !proxyResponse.Headers.TryGetValues("S7PREQUEUE", out var values))
            return (false, 0);

        requestState = "Process 429";

        foreach (var header in proxyResponse.Headers.ToList())
        {
            if (s_excludedHeaders.Contains(header.Key)) continue;
            requestAttempt[header.Key] = string.Join(", ", header.Value);
        }

        if (!string.Equals(values.FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase))
            return (false, 0);

        // Try retry-after-ms (milliseconds), then retry-after (seconds), default to 1000ms
        int retryMs = 1000;
        if (proxyResponse.Headers.TryGetValues("retry-after-ms", out var retryAfterValuesMS) &&
            int.TryParse(retryAfterValuesMS.FirstOrDefault(), out var retryAfterValueMS))
        {
            retryMs = retryAfterValueMS;
        }
        else if (proxyResponse.Headers.TryGetValues("retry-after", out var retryAfterValues) &&
                 int.TryParse(retryAfterValues.FirstOrDefault(), out var retryAfterValue))
        {
            retryMs = retryAfterValue * 1000;
        }

        return (true, retryMs);
    }

    /// <summary>
    /// Writes the error response when all backend hosts have been exhausted.
    /// Routes to blob storage (for async requests) or HTTP context (for sync requests).
    /// </summary>
    private async Task WriteExhaustedHostsErrorAsync(RequestData request, HttpStatusCode statusCode, string errorBody)
    {
        try
        {
            if (request.AsyncTriggered && request.asyncWorker != null)
            {
                _logger.LogInformation("Writing error response to AsyncWorker blob for request {Guid} - Status: {StatusCode}",
                    request.Guid, statusCode);

                var errorHeaders = new WebHeaderCollection
                {
                    ["x-Request-Queue-Duration"] = (request.DequeueTime - request.EnqueueTime).TotalMilliseconds.ToString("F3") + " ms",
                    ["x-Total-Latency"] = (DateTime.UtcNow - request.EnqueueTime).TotalMilliseconds.ToString("F3") + " ms",
                    ["x-ProxyHost"] = _options.HostName,
                    ["x-MID"] = request.MID,
                    ["Attempts"] = request.BackendAttempts.ToString()
                };

                await request.asyncWorker.WriteHeaders(statusCode, errorHeaders);

                var errorBytes = Encoding.UTF8.GetBytes(errorBody);
                if (request.IsBackgroundCheck)
                {
                    var outputStream = await request.asyncWorker.GetOrCreateDataStreamAsync();
                    await outputStream.WriteAsync(errorBytes).ConfigureAwait(false);
                    await outputStream.FlushAsync().ConfigureAwait(false);
                }
                else if (request.OutputStream != null)
                {
                    await request.OutputStream.WriteAsync(errorBytes).ConfigureAwait(false);
                    await request.OutputStream.FlushAsync().ConfigureAwait(false);
                }
            }
            else if (!request.AsyncTriggered && request.Context != null)
            {
                _logger.LogInformation("Response Status Code: {StatusCode} for request {Guid}",
                    statusCode, request.Guid);
                request.Context.Response.StatusCode = (int)statusCode;
                request.Context.Response.KeepAlive = false;

                request.Context.Response.Headers["x-Request-Queue-Duration"] = (request.DequeueTime - request.EnqueueTime).TotalMilliseconds.ToString("F3") + " ms";
                request.Context.Response.Headers["x-Total-Latency"] = (DateTime.UtcNow - request.EnqueueTime).TotalMilliseconds.ToString("F3") + " ms";
                request.Context.Response.Headers["x-ProxyHost"] = _options.HostName;
                request.Context.Response.Headers["x-MID"] = request.MID;
                request.Context.Response.Headers["Attempts"] = request.BackendAttempts.ToString();

                await request.Context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(errorBody)).ConfigureAwait(false);
                await request.Context.Response.OutputStream.FlushAsync().ConfigureAwait(false);
            }
            else
            {
                _logger.LogWarning("Cannot write error response for request {Guid} - Context: {HasContext}, AsyncTriggered: {AsyncTriggered}, AsyncWorker: {HasAsyncWorker}",
                    request.Guid, request.Context != null, request.AsyncTriggered, request.asyncWorker != null);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error writing error response for request {Guid} - AsyncTriggered: {AsyncTriggered}",
                request.Guid, request.AsyncTriggered);
        }
    }

    /// <summary>
    /// Writes an error response (status code + message body) to the client's HTTP connection.
    /// Consolidates the duplicated try/catch pattern used across catch blocks in TaskRunnerAsync.
    /// </summary>
    /// <returns>True if the response was written successfully, false if the write failed.</returns>
    private async Task<bool> WriteErrorToClientAsync(
        HttpListenerContext lcontext,
        HttpStatusCode statusCode,
        string errorMessage,
        ProxyEvent eventData,
        Guid? requestGuid)
    {
        try
        {
            lcontext.Response.StatusCode = (int)statusCode;
            var errorBytes = Encoding.UTF8.GetBytes(errorMessage);
            await lcontext.Response.OutputStream.WriteAsync(errorBytes).ConfigureAwait(false);
            return true;
        }
        catch (Exception writeEx)
        {
            _logger.LogError(writeEx, "Failed to write error response for request {Guid}", requestGuid);
            eventData["InnerErrorDetail"] = "Network Error sending error response";
            eventData["InnerErrorStack"] = writeEx.StackTrace?.ToString() ?? "No Stack Trace";
            eventData.Type = EventType.Exception;
            eventData.Exception = writeEx;
            return false;
        }
    }

    private void PopulateTimeoutError(
        ProxyEvent requestAttempt,
        RequestData request,
        DateTime proxyStartDate,
        bool isCancelled = false)
    {
        requestAttempt.Status = HttpStatusCode.RequestTimeout;
        requestAttempt["Expires-At"] = request.ExpiresAt.ToString("o");
        requestAttempt["MaxTimeout"] = _options.Timeout.ToString();
        requestAttempt["Request-Date"] = proxyStartDate.ToString("o");
        requestAttempt["Request-Timeout"] = $"{request.Timeout} ms";
        requestAttempt["Error"] = isCancelled ? "Request Cancelled" : "Request Timed out";
        requestAttempt["Message"] = isCancelled ? "Operation CANCELLED" : "Operation TIMEOUT";
    }

    private static bool IsInvalidHeaderException(Exception ex)
        => ex.Message.StartsWith("The format of value");

    /// <summary>
    /// Streams the response content from the backend to the client using the appropriate stream processor.
    /// 
    /// RESPONSE ROUTING LOGIC:
    ///   - Synchronous mode: Streams directly to request.OutputStream (client HTTP connection)
    ///   - Async/Background mode: Streams to asyncWorker which writes to blob storage
    /// 
    /// STREAM PROCESSORS:
    ///   - DefaultStream: Pass-through streaming with no processing
    ///   - OpenAI: Detects batch IDs and triggers background processing mode
    ///   - AllUsage: Extracts and logs usage information from OpenAI responses
    ///   - MultiLineAllUsage: Handles multi-line usage data extraction
    /// 
    /// BACKGROUND MODE DETECTION:
    ///   When processor.BackgroundCompleted is set, this indicates a background batch job
    ///   was initiated (e.g., OpenAI batch API). The request will transition to background
    ///   polling mode and status will be tracked separately.
    /// </summary>
    /// <param name="request">The incoming request data</param>
    /// <param name="proxyResponse">The HTTP response from the backend</param>
    /// <param name="processWith">The name of the processor to use for streaming</param>
    private async Task StreamResponseAsync(RequestData request, ProxyData pr)
    {
        ProxyEvent requestSummary = request.EventData;
        string processWith = pr.StreamingProcessor ?? StreamProcessorFactory.DEFAULT_PROCESSOR;
        var proxyResponse = pr.BodyResponseMessage;

        if (proxyResponse == null)
        {
            _logger.LogError("Null Proxy response: Guid: {Guid}, Details: {details}", request.Guid, pr.ToString());
            return;
        }

        IStreamProcessor processor = _streamProcessorFactory.GetStreamProcessor(processWith, out string resolvedProcessor);
        MemoryStream? memoryBuffer = null;
        
        try
        {
            _logger.LogDebug("Resolved processor: {ProcessorName} for request {Guid}", resolvedProcessor, request.Guid);

            // Route response to appropriate destination based on execution mode
            Stream? destination;
            bool needsFlush = false;
            string destinationType;

            if (request.IsBackgroundCheck && request.asyncWorker != null)
            {
                destinationType = "memory buffer";
                memoryBuffer = new MemoryStream();
                destination = memoryBuffer;             // <-- track this in memory for background checks
            }
            else if (request.runAsync && request.asyncWorker != null)
            {
                destinationType = "async blob";
                needsFlush = true;                      // QueuedBlobStream requires FlushAsync to enqueue data
                destination = await request.asyncWorker.GetOrCreateDataStreamAsync().ConfigureAwait(false);
            }
            else if (request.OutputStream != null)
            {
                destinationType = "client";
                destination = request.OutputStream;
            }
            else
            {
                _logger.LogError("OutputStream is null for request {Guid}, cannot stream response", request.Guid);
                return;
            }

            if (proxyResponse.Content != null)
            {
                _logger.LogDebug("Streaming to {Destination} for request {Guid}", destinationType, request.Guid);
                await processor.CopyToAsync(proxyResponse.Content, destination).ConfigureAwait(false);

                if (needsFlush)
                {
                    await destination.FlushAsync().ConfigureAwait(false);
                }
            }
        }
        catch (HttpListenerException ex)
        {
            _logger.LogDebug(ex, "[BLOB-TRACE] StreamResponseAsync | Action: Error-HttpListener | Guid: {Guid} | Error: {ErrorMessage}", 
                request.Guid, ex.Message);
        }
        catch (Exception ex) when (ex is IOException || ex.InnerException is IOException)
        {
            _logger.LogDebug(ex, "[BLOB-TRACE] StreamResponseAsync | Action: Error-IO | Guid: {Guid} | Error: {ErrorMessage}", 
                request.Guid, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BLOB-TRACE] StreamResponseAsync | Action: Error-General | Guid: {Guid} | Error: {ErrorMessage} | Type: {ExType}",
                request.Guid, ex.Message, ex.GetType().FullName);
            // throw new ProxyErrorException(
            //     ProxyErrorException.ErrorType.ClientDisconnected,
            //     HttpStatusCode.InternalServerError,
            //     $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                if (request.IsBackgroundCheck && request.asyncWorker != null && memoryBuffer != null && processor != null)
                {
                    await HandleBackgroundCheckResultAsync(request, proxyResponse, processor, memoryBuffer);
                }

                if (proxyResponse.Headers != null && processor != null)
                {
                    processor.GetStats(request.EventData, proxyResponse.Headers);
                }
                
                await _lifecycleManager.HandleBackgroundRequestLifecycle(request, processor!).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Background lifecycle management failed for request {Guid}", request.Guid);
            }
            finally
            {
                memoryBuffer?.Dispose();
                (processor as IDisposable)?.Dispose();
            }
        }
    }

    private async Task HandleBackgroundCheckResultAsync(
        RequestData request,
        HttpResponseMessage proxyResponse,
        IStreamProcessor processor,
        MemoryStream memoryBuffer)
    {

        if (!processor.BackgroundCompleted && !request.Debug)
        {
            _logger.LogDebug("Background check in progress - discarding {Bytes} bytes for request {Guid}",
                memoryBuffer.Length, request.Guid);
            return;
        }

        _logger.LogDebug("Background check completed or Debug mode - writing headers and {Bytes} bytes to blob for request {Guid}",
            memoryBuffer.Length, request.Guid);

        var pr = new ProxyData();
        ProxyHelperUtils.CopyResponseHeaders(proxyResponse, pr);
        if (pr.Headers != null && request.asyncWorker != null)
        {
            await request.asyncWorker.WriteHeaders(proxyResponse.StatusCode!, pr.Headers);
        }

        if (request.asyncWorker != null)
        {
            var outputStream = await request.asyncWorker.GetOrCreateDataStreamAsync();
            memoryBuffer.Position = 0;
            await memoryBuffer.CopyToAsync(outputStream).ConfigureAwait(false);
            await outputStream.FlushAsync().ConfigureAwait(false);
        }
    }


    // cts is returned to the caller who disposes of it
    private async Task<(CancellationTokenSource, double)> SetupAsyncWorkerAndTimeout(RequestData request)
    {
        double timeout = request.Timeout;
        CancellationTokenSource cts;

        // ✅ Dispose old CTS before creating new one and clear the reference
        _asyncExpelSource?.Dispose();
        _asyncExpelSource = null;
        
        if (request.runAsync)
        {
            timeout = _options.AsyncTimeout;
            if (request.asyncWorker is null)
            {
                var timeLeft = _options.AsyncTriggerTimeout - (int)(DateTime.UtcNow - request.EnqueueTime).TotalMilliseconds;
                timeLeft = Math.Max(1, timeLeft);
                request.asyncWorker = await _asyncWorkerFactory.CreateAsync(request, timeLeft).ConfigureAwait(false);
                _ = request.asyncWorker.StartAsync();
            }

            _asyncExpelSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeout));
            cts = _asyncExpelSource;
        }
        else
        {
            _asyncExpelSource = null;
            cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeout));
        }

        return (cts, timeout);
    }


    // Exclude hop-by-hop and restricted headers that HttpListener manages
    private static readonly HashSet<string> s_excludedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Length", "Transfer-Encoding", "Connection", "Proxy-Connection",
        "Keep-Alive", "Upgrade", "Trailer", "TE", "Date", "Server"
    };

    /// <summary>
    /// Helper method to abstract over shared vs per-request iterators.
    /// For shared iterators: uses TryGetNextHost (circular, thread-safe)
    /// For per-request iterators: uses MoveNext/Current pattern
    /// </summary>
    /// <param name="perRequestIterator">Per-request iterator (null if using shared)</param>
    /// <param name="sharedIterator">Shared iterator (null if using per-request)</param>
    /// <param name="host">Output: the next host, or null if none available</param>
    /// <returns>True if a host was retrieved, false if iteration is complete</returns>
    private static bool TryGetNextHost(
        IHostIterator? perRequestIterator,
        ISharedHostIterator? sharedIterator,
        out BaseHostHealth? host)
    {
        if (sharedIterator != null)
        {
            // Shared iterator - uses atomic TryGetNextHost
            return sharedIterator.TryGetNextHost(out host);
        }
        
        if (perRequestIterator != null && perRequestIterator.MoveNext())
        {
            host = perRequestIterator.Current;
            return true;
        }
        
        host = null;
        return false;
    }
}