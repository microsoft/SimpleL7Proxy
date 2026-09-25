using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Backend.Iterators;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Llm;
using SimpleL7Proxy.Queue;
using SimpleL7Proxy.User;
using SimpleL7Proxy.Async.ServiceBus;
using SimpleL7Proxy.StreamProcessor;
using Shared.RequestAPI.Models;
using System.Collections.Frozen;
using SimpleL7Proxy.Tokenomics;

namespace SimpleL7Proxy.Proxy;



// Review DISPOSAL_ARCHITECTURE.MD in the root for details on disposal flow

// The ProxyWorker class has the following main objectives:
// 1. Read incoming requests from the queue, prioritizing the highest priority requests.
// 2. Proxy the request to the backend with the lowest latency.
// 3. Retry against the next backend if the current backend fails.
// 4. Return a 502 Bad Gateway if all backends fail.
// 5. Return a 200 OK with backend server stats if the request is for /health.
// 6. Log telemetry data for each request.
public class ProxyRequestHandler
{
    private readonly WorkerContext _wrkCntxt;
    private readonly int _preferredPriority;
    private readonly string _idStr;
    private readonly RequestLifecycleManager _lifecycleManager;
    private readonly CancellationToken _cancellationToken;
    private readonly IConcurrentPriQueue<RequestData>? s_requestsQueue;
    private readonly IEndpointMonitorService _backends;
    private readonly ProxyConfig _options;
    private readonly ILogger<ProxyRequestHandler> _logger;
    private readonly EventDataBuilder _eventDataBuilder;
    private readonly int _id;
    private readonly ProxyEvent s_finallyBlockErrorEvent = new ProxyEvent(18);
    private ProxyWorker _pw;

    // private readonly StreamFlusher _streamFlusher;
    // // private static bool s_readyToWork;
    // // public static bool IsReadyToWork => s_readyToWork;
    // private CancellationTokenSource? _asyncExpelSource;
    // private bool _isEvictingAsyncRequest;
    // private static List<string> s_backendKeys = [];
    // private static FrozenSet<string> s_stripRequestHeaders = FrozenSet.Create<string>();
    // private static FrozenSet<string> s_stripResponseHeaders = FrozenSet.Create<string>();

    // private bool detectModel = false;

    // //private readonly ProxyStreamWriter _proxyStreamWriter;
    // // private readonly string _timeoutHeaderName;

    // // Static pre-allocated ProxyEvent objects for error scenarios to avoid expensive copy constructor
    // // private static readonly ProxyEvent s_backendRequestAttemptEvent = new ProxyEvent(25);  // Base eventData (~20) + attempt fields (7)

    public ProxyRequestHandler(
        int id,
        int priority,
        WorkerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        _wrkCntxt = context;
        _id = id;
        _idStr = id.ToString();
        _preferredPriority = priority;
        _cancellationToken = cancellationToken;
        _options = context.BackendOptions;


        if (_options.Client == null) throw new ArgumentNullException(nameof(_options.Client));

        s_requestsQueue = context.Queue;
        _backends = context.Backends;
        _logger = context.Logger;
        _lifecycleManager = context.LifecycleManager;
        _eventDataBuilder = context.EventDataBuilder;
        _options = context.BackendOptions;

        _pw = new ProxyWorker(id, context);
    }

    /// <summary>
    /// Delegates eviction of the active asynchronous request to the backend worker.
    /// </summary>
    public void ExpelAsyncRequest() => _pw.ExpelAsyncRequest();

    private async Task HandleProbeRequestAsync(RequestData req, HttpListenerContext lcontext)
    {
        int hostCount = _backends.ActiveHostCount();
        bool hasFailedHosts = _backends.EMSGetBackpressureDelay() > 0;
        _wrkCntxt.HealthCheckService.BuildHealthResponse(req.Path, hostCount, hasFailedHosts, req.Timestamp, out int probeStatus, out string probeMessage);

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

        // Log probe telemetry (moved from Server.Run to ensure single-log per probe)
        var eventData = req.EventData;
        eventData.Type = EventType.Probe;
        eventData.Uri = lcontext.Request.Url!;
        eventData.Status = (HttpStatusCode)probeStatus;
        eventData["ProbeType"] = req.Path switch {
            Constants.Health => "Health",
            Constants.HealthDetail => "HealthDetail",
            _ => "ForceGC"
        };
        eventData["StatusCode"] = probeStatus.ToString();
        eventData.SendEvent();
    }

    /// <summary>
    /// Writes an error response (status code + message body) to the client's HTTP connection.
    /// Consolidates the duplicated try/catch pattern used across catch blocks in RunWorkerLoopAsync.
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
    /// │  │    └─ FinalizeStatus() + asyncWorker?.PersistRequestStateAsync()          │  │
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
    public async Task RunWorkerLoopAsync()
    {
        bool doUserconfig = _options.UseProfiles;
        string workerState = string.Empty;

        if (doUserconfig && _wrkCntxt.UserProfileService == null) throw new ArgumentNullException(nameof(_wrkCntxt.UserProfileService));
        if (s_requestsQueue == null) throw new ArgumentNullException(nameof(s_requestsQueue));

        // Only for use during shutdown after graceseconds have expired
        // CancellationTokenSource cts = new CancellationTokenSource();
        // CancellationToken token = cts.Token;

        // increment the active workers count.   When all workers are active, the startup probe allows traffic. 
        // if (_options.Workers == HealthCheckService.IncrementActiveWorkers(_options.Workers))
        // {
        //     s_readyToWork = true;
        // }

        HealthCheckService.IncrementActiveWorkers(_options.Workers);

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
                // Only exit if the cancellation token has fired AND the queue is empty.
                // If there are still items, re-enter the loop so SignalWorker can dispatch them.
                if (s_requestsQueue.thrdSafeCount == 0 || !_cancellationToken.IsCancellationRequested)
                    break;
                // Cancelled but items remain — continue draining
                continue;
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

                    // check for response check
                    if (incomingRequest.Type == RequestType.StatusCheck)
                    {
                        var statusChecker = _wrkCntxt.AsyncWorkerContext?.RequestStatus;
                        if (statusChecker != null)
                        {
                            _logger.LogInformation("[Worker:{Id}] StatusCheck request {Guid} - delegating to AsyncRequestStatus",
                                _id, incomingRequest.Headers["Guid"]);
                            await statusChecker.CheckStatus(incomingRequest).ConfigureAwait(false);
                        }
                        else
                        {
                            _logger.LogWarning("[Worker:{Id}] StatusCheck requested but AsyncRequestStatus is not configured", _id);
                        }

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
                        pr = await _pw.ProxyToBackEndAsync(incomingRequest).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (!_pw.IsEvictingAsyncRequest)
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
                    await _pw.WriteClientResponseAsync(incomingRequest, pr).ConfigureAwait(false);

                    //                    Task.Yield(); // Yield to the scheduler to allow other tasks to run
                    HealthCheckService.EnterState(_id, WorkerState.Reporting);
                    workerState = "Finalize";

                    var conlen = pr.ContentHeaders?["Content-Length"] ?? "N/A";
                    var proxyLatency = (DateTime.UtcNow - incomingRequest.DequeueTime).TotalMilliseconds.ToString("F3");

                    _logger.LogCritical("[{Guid}] Pri: {Priority}, Stat: {StatusCode}, User: {User}, Type: {RequestType}, Model: {Rodel} Proc: {Processor}, Len: {ContentLength}, Deq: {DequeueTime}, Lat: {ProxyTime} ms, {FullURL}",
                        incomingRequest.Guid,
                        incomingRequest.Priority, statusCodeInt,
                        incomingRequest.UserID ?? "N/A",
                        incomingRequest.Type,
                        _options.DetectModel ? incomingRequest.Model ?? "N/A" : "-",
                        pr.StreamingProcessor,
                        conlen, 
                        incomingRequest.DequeueTime.ToLocalTime().ToString("T"), proxyLatency,
                        pr.FullURL
                        );

                    // Log circuit breaker details when status code is -1
                    if (incomingRequest.Debug && (statusCodeInt == -1 || statusCodeInt == 503))
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
                        if (incomingRequest.asyncWorker != null)
                        {
                            await incomingRequest.asyncWorker.PersistRequestStateAsync().ConfigureAwait(false);
                        }
                    }
                    // Background check requests skip ShouldFinalize but still need
                    // Completed status after blob writes confirm
                    else if (incomingRequest.Type == RequestType.AsyncBackgroundCheck &&
                             incomingRequest.BackgroundRequestCompleted &&
                             incomingRequest.asyncWorker != null)
                    {
                        await incomingRequest.asyncWorker.WaitForBlobWritesAsync().ConfigureAwait(false);
                        _lifecycleManager.FinalizeBackgroundCheckStatus(incomingRequest);
                        await incomingRequest.asyncWorker.PersistRequestStateAsync().ConfigureAwait(false);
                    }

                }
                catch (S7PThrottledException e)
                {
                    _lifecycleManager.TransitionToFailed(incomingRequest, HttpStatusCode.TooManyRequests, e.Message);
                    eventData.Status = HttpStatusCode.TooManyRequests;
                    eventData["Error"] = "Throttled";
                    eventData["ErrorDetails"] = e.InnerException?.Message ?? e.Message;
                    eventData.Type = EventType.Exception;
                    eventData.Exception = e;
                    if (lcontext != null)
                    {
                        await WriteErrorToClientAsync(
                            lcontext,
                            HttpStatusCode.TooManyRequests,
                            e.Message,
                            eventData,
                            incomingRequest.Guid);
                    }

                }
                catch (S7PRequeueException e)
                {
                    // launches a delay task while the current worker goes back to the top of the loop for more work
                    _lifecycleManager.TransitionToRequeued(incomingRequest);
                    _wrkCntxt.RequeueWorker.DelayAsync(incomingRequest, e.RetryAfter);

                }
                catch (S7PClientReadException e)
                {
                    _lifecycleManager.TransitionToFailed(incomingRequest, HttpStatusCode.BadRequest, e.Message);
                    eventData.Status = HttpStatusCode.BadRequest;
                    eventData["Error"] = "Client Read Exception";
                    eventData["ErrorDetails"] = e.InnerException?.Message ?? e.Message;
                    eventData.Type = EventType.Exception;
                    eventData.Exception = e;

                    if (lcontext != null)
                    {
                        await WriteErrorToClientAsync(
                            lcontext,
                            HttpStatusCode.BadRequest,
                            e.Message,
                            eventData,
                            incomingRequest.Guid);
                    }
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
                    if (_pw.IsEvictingAsyncRequest)
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
                        if (_lifecycleManager.ShouldCleanup(incomingRequest, incomingRequest.Requeued, _pw.IsEvictingAsyncRequest))
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
                                _id, incomingRequest.Guid, incomingRequest.Requeued, _pw.IsEvictingAsyncRequest);
                        }
                    }
                    catch (Exception e)
                    {
                        // Reuse the loop's error event
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

        //_logger.LogInformation("[SHUTDOWN] ⏹  Worker {IdStr} stopped", _idStr);

    }
}
