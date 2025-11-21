using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Linq.Expressions;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Logging;
using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Backend.Iterators;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Queue;
using SimpleL7Proxy.User;
using SimpleL7Proxy.ServiceBus;
using SimpleL7Proxy.BlobStorage;
using SimpleL7Proxy.StreamProcessor;
using Shared.RequestAPI.Models;
using System.Runtime.InteropServices;

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
    private int _preferredPriority;
    private readonly CancellationToken _cancellationToken;
    private static bool _debug = false;
    private static IConcurrentPriQueue<RequestData>? _requestsQueue;
    private static IRequeueWorker? _requeueDelayWorker; // Initialized in constructor, only one instance
    private readonly IBackendService _backends;
    private readonly BackendOptions _options;
    private readonly IEventClient _eventClient;
    private readonly IAsyncWorkerFactory _asyncWorkerFactory; // Just inject the factory
    private readonly ILogger<ProxyWorker> _logger;
    private readonly StreamProcessorFactory _streamProcessorFactory;
    //private readonly ProxyStreamWriter _proxyStreamWriter;
    private IUserPriorityService _userPriority;
    private IUserProfileService _profiles;
    private readonly string _timeoutHeaderName;
    private string _idStr = "";
    public static int activeWorkers = 0;
    private static bool readyToWork = false;
    public static bool IsReadyToWork => readyToWork;
    private CancellationTokenSource? _asyncExpelSource;
    private bool _asyncExpelInProgress = false;
    private static HealthCheckService? _healthCheckService;

    static string[] backendKeys = Array.Empty<string>();

    private static int[] states = [0, 0, 0, 0, 0, 0, 0, 0];

    public static string GetState()
    {
        return $"Count: {activeWorkers} States: [ deq-{states[0]} pre-{states[1]} prxy-{states[2]} -[snd-{states[3]} rcv-{states[4]}]-  wr-{states[5]} rpt-{states[6]} cln-{states[7]} ]";
    }

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
        //ProxyStreamWriter proxyStreamWriter,
        CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        _requestsQueue = requestsQueue ?? throw new ArgumentNullException(nameof(requestsQueue));
        _backends = backends ?? throw new ArgumentNullException(nameof(backends));
        _eventClient = eventClient;
        _asyncWorkerFactory = asyncWorkerFactory;
        _requeueDelayWorker = requeueDelayWorker;
        _logger = logger;
        _streamProcessorFactory = streamProcessorFactory ?? throw new ArgumentNullException(nameof(streamProcessorFactory));
        //_proxyStreamWriter = proxyStreamWriter;
        //_eventHubClient = eventHubClient;
        _userPriority = userPriority ?? throw new ArgumentNullException(nameof(userPriority));
        _options = backendOptions ?? throw new ArgumentNullException(nameof(backendOptions));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _timeoutHeaderName = _options.TimeoutHeader;
        if (_options.Client == null) throw new ArgumentNullException(nameof(_options.Client));
        backendKeys = _options.DependancyHeaders;
        _idStr = id.ToString();
        _preferredPriority = priority;

        // Initialize health check service once for all workers (thread-safe)
        if (_healthCheckService == null)
        {
            _healthCheckService = new HealthCheckService(
                _backends,
                _options,
                _requestsQueue,
                _userPriority,
                eventClient,
                GetState);
        }
    }

    public async Task TaskRunnerAsync()
    {
        bool doUserconfig = _options.UseProfiles;
        string workerState = "";

        if (doUserconfig && _profiles == null) throw new ArgumentNullException(nameof(_profiles));
        if (_requestsQueue == null) throw new ArgumentNullException(nameof(_requestsQueue));

        // Only for use during shutdown after graceseconds have expired
        CancellationTokenSource cts = new CancellationTokenSource();
        CancellationToken token = cts.Token;

        // increment the active workers count
        Interlocked.Increment(ref activeWorkers);
        if (_options.Workers == activeWorkers)
        {
            readyToWork = true;
            // Always display
            _logger.LogInformation("[READY] ✓ All workers ready to work");
        }

        // Run until cancellation is requested. (Queue emptiness is handled by the blocking DequeueAsync call.)
        while (!_cancellationToken.IsCancellationRequested || _requestsQueue.thrdSafeCount > 0)
        {
            RequestData incomingRequest;
            try
            {
                Interlocked.Increment(ref states[0]);
                workerState = "Waiting";

                // This will block until an item is available or the token is cancelled
                incomingRequest = await _requestsQueue.DequeueAsync(_preferredPriority).ConfigureAwait(false);
                if (incomingRequest == null)
                {
                    continue;
                }
            }
            catch (OperationCanceledException)
            {
                //_logger.LogInformation("Operation was cancelled. Stopping the worker.");
                break; // Exit the loop if the operation is cancelled
            }
            finally
            {
                Interlocked.Decrement(ref states[0]);
                workerState = "Exit - Get Work";
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

                Interlocked.Increment(ref states[1]);
                workerState = "Processing";

                var lcontext = incomingRequest.Context;

                if (!incomingRequest.AsyncHydrated && (lcontext == null || incomingRequest == null))
                {
                    Interlocked.Decrement(ref states[1]);
                    workerState = "Exit - No Context";
                    Interlocked.Increment(ref states[7]);
                    continue;
                }

                var eventData = incomingRequest.EventData;
                bool requestException = false;
                try
                {
                    if (Constants.probes.Contains(incomingRequest.Path) && lcontext != null)
                    {
                        _healthCheckService!.GetProbeResponse(incomingRequest.Path, out int probeStatus, out string probeMessage);

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

                        Interlocked.Decrement(ref states[1]);
                        workerState = "Exit - Probe";
                        Interlocked.Increment(ref states[7]);


                        // probe details, will be logged in the finally clause [ or not if logProbes==false ]
                        eventData["Probe"] = incomingRequest.Path;
                        eventData["ProbeStatus"] = probeStatus.ToString();
                        eventData["ProbeMessage"] = probeMessage;
                        eventData.Type = EventType.Probe;
                        //Console.WriteLine($"Probe: {incomingRequest.Path} Status: {probeStatus} Message: {probeMessage}");

                        continue;
                    }

                    // ASYNC: Set the status to Processing
                    if (incomingRequest.IsBackground)
                    {
                        incomingRequest.SBStatus = ServiceBusMessageStatusEnum.CheckingBackgroundRequestStatus;
                    }
                    else
                    {
                        incomingRequest.SBStatus = ServiceBusMessageStatusEnum.Processing;
                    }

                    eventData.Type = EventType.ProxyRequest;
                    eventData["EnqueueTime"] = incomingRequest.EnqueueTime.ToString("o");
                    eventData["RequestContentLength"] = incomingRequest.Headers["Content-Length"] ?? "N/A";

                    incomingRequest.Headers["x-Request-Queue-Duration"] = (incomingRequest.DequeueTime! - incomingRequest.EnqueueTime!).TotalMilliseconds.ToString();
                    incomingRequest.Headers["x-Request-Process-Duration"] = (DateTime.UtcNow - incomingRequest.DequeueTime).TotalMilliseconds.ToString();
                    incomingRequest.Headers["x-Request-Worker"] = _idStr;
                    incomingRequest.Headers["x-S7P-ID"] = incomingRequest.MID ?? "N/A";
                    incomingRequest.Headers["x-S7PPriority"] = incomingRequest.Priority.ToString();
                    incomingRequest.Headers["x-S7PPriority2"] = incomingRequest.Priority2.ToString();

                    eventData["Request-Queue-Duration"] = incomingRequest.Headers["x-Request-Queue-Duration"] ?? "N/A";
                    eventData["Request-Process-Duration"] = incomingRequest.Headers["x-Request-Process-Duration"] ?? "N/A";

                    Interlocked.Decrement(ref states[1]);
                    Interlocked.Increment(ref states[2]);
                    workerState = "Read Proxy";

                    //  Do THE WORK:  FIND A BACKEND AND SEND THE REQUEST
                    ProxyData pr = null!;

                    try
                    {
                        pr = await ProxyToBackEndAsync(incomingRequest).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (!_asyncExpelInProgress)
                        {
                            eventData["Url"] = incomingRequest.FullURL;
                            var timeTaken = DateTime.UtcNow - incomingRequest.EnqueueTime;
                            eventData.Duration = timeTaken;
                            eventData["Total-Latency"] = timeTaken.TotalMilliseconds.ToString("F3");
                            eventData["Attempts"] = incomingRequest.BackendAttempts.ToString();
                            if (pr != null)
                            {
                                eventData["Backend-Host"] = !string.IsNullOrEmpty(pr.BackendHostname) ? pr.BackendHostname : "N/A";
                                eventData["Response-Latency"] =
                                    pr.ResponseDate != default && incomingRequest.DequeueTime != default
                                        ? (pr.ResponseDate - incomingRequest.DequeueTime).TotalMilliseconds.ToString("F3") : "N/A";
                                eventData["Average-Backend-Probe-Latency"] =
                                    pr.CalculatedHostLatency != 0 ? pr.CalculatedHostLatency.ToString("F3") + " ms" : "N/A";
                                if (pr.Headers != null)
                                    pr.Headers["x-Attempts"] = incomingRequest.BackendAttempts.ToString();
                            }
                        }
                        Interlocked.Decrement(ref states[2]);
                    }

                    // POST PROCESSING ... logging
                    Interlocked.Increment(ref states[5]);
                    workerState = "Write Response";

                    //                    Task.Yield(); // Yield to the scheduler to allow other tasks to run

                    eventData.Status = pr.StatusCode;
                    if (_options.LogAllRequestHeaders)
                    {
                        foreach (var header in incomingRequest.Headers.AllKeys)
                        {
                            if (_options.LogAllRequestHeadersExcept == null || !_options.LogAllRequestHeadersExcept.Contains(header))
                            {
                                eventData["Request-" + header] = incomingRequest.Headers[header] ?? "N/A";

                            }
                        }
                    }

                    if (_options.LogAllResponseHeaders)
                    {
                        foreach (var header in pr.Headers.AllKeys)
                        {
                            if (_options.LogAllResponseHeadersExcept == null || !_options.LogAllResponseHeadersExcept.Contains(header))
                            {
                                eventData["Response-" + header] = pr.Headers[header] ?? "N/A";
                            }
                        }
                    }
                    else if (_options.LogHeaders?.Count > 0)
                    {
                        foreach (var header in _options.LogHeaders)
                        {
                            eventData["Response-" + header] = pr.Headers[header] ?? "N/A";
                        }
                    }

                    if (pr.StatusCode == HttpStatusCode.PreconditionFailed || pr.StatusCode == HttpStatusCode.RequestTimeout) // 412 or 408
                    {
                        // Request has expired
                        isExpired = true;
                        // eventData.Type = EventType.ProxyRequestExpired;
                        incomingRequest.SBStatus = ServiceBusMessageStatusEnum.Expired;
                    }
                    else if (pr.StatusCode == HttpStatusCode.OK)
                    {
                        // scenarios:
                        // 1: the Background request initiated => BackgroundRequestSubmitted
                        // 2: the non-background async request completed => AsyncProcessed
                        // 3: the background check request completed and the background task completed => AsyncProcessed
                        // 4: the background check request completed and the background task is still running => no updates
                        // all others are treated as synchronous completions => Processed

                        if (!incomingRequest.runAsync)
                        {
                            // Synchronous requests were processed and response sent directly to client
                            incomingRequest.SBStatus = ServiceBusMessageStatusEnum.Processed;
                        }
                        else
                        {
                            if (incomingRequest.IsBackgroundCheck)
                            {
                                // Background check scenario
                                if (incomingRequest.BackgroundRequestCompleted)
                                {
_logger.LogInformation("updating status to Completed");
                                    // Scenario 3: Background check where the background task has completed
                                    // (RequestAPIStatus set to Completed in StreamResponseAsync when processor.BackgroundCompleted == true)
                                    incomingRequest.SBStatus = ServiceBusMessageStatusEnum.AsyncProcessed;
                                    incomingRequest.RequestAPIStatus = RequestAPIStatusEnum.Completed;
                                }
                                else
                                {
_logger.LogInformation("updating status to BackgroundProcessing");
                                    // Scenario 4: Background check where the background task is still running
                                    // Keep existing status (no update needed)
                                    incomingRequest.SBStatus = ServiceBusMessageStatusEnum.CheckingBackgroundRequestStatus;
                                    incomingRequest.RequestAPIStatus = RequestAPIStatusEnum.BackgroundProcessing;
                                }
                            }
                            else
                            {
_logger.LogInformation("updating status to BackgroundRequestSubmitted");
                                // Scenario 1: New background request initiated - submit for later polling
                                incomingRequest.SBStatus = ServiceBusMessageStatusEnum.BackgroundRequestSubmitted;
                                incomingRequest.RequestAPIStatus = RequestAPIStatusEnum.BackgroundProcessing;
                            }
                        }
                    }

                    // SYNCHRONOUS MODE
                    //await WriteResponseAsync(lcontext, pr).ConfigureAwait(false);

                    //                    Task.Yield(); // Yield to the scheduler to allow other tasks to run
                    Interlocked.Decrement(ref states[5]);
                    Interlocked.Increment(ref states[6]);
                    workerState = "Finalize";

                    var conlen = pr.ContentHeaders?["Content-Length"] ?? "N/A";
                    var proxyTime = (DateTime.UtcNow - incomingRequest.DequeueTime).TotalMilliseconds.ToString("F3");
                    _logger.LogCritical("Pri: {Priority}, Stat: {StatusCode}, User: {user} Guid: {Guid} Async: {mode} Processor: {Processor}, Len: {ContentLength}, {FullURL}, Deq: {DequeueTime}, Lat: {ProxyTime} ms",
                        incomingRequest.Priority, (int)pr.StatusCode,
                        incomingRequest.UserID ?? "N/A",
                        incomingRequest.Guid,
                        incomingRequest.AsyncTriggered ? "Async" : "Sync",
                        pr.StreamingProcessor,
                        conlen, pr.FullURL, incomingRequest.DequeueTime.ToLocalTime().ToString("T"), proxyTime);

                    eventData["Content-Length"] = lcontext?.Response?.ContentLength64.ToString() ?? "N/A";
                    eventData["Content-Type"] = lcontext?.Response?.ContentType ?? "N/A";

                    if (incomingRequest.incompleteRequests != null)
                    {
                        AddIncompleteRequestsToEventData(incomingRequest.incompleteRequests, eventData);
                    }

                    Interlocked.Decrement(ref states[6]);
                    Interlocked.Increment(ref states[7]);
                    workerState = "Cleanup";

                    if (isExpired)
                    {
                        incomingRequest.RequestAPIStatus = RequestAPIStatusEnum.Failed;
                    }
                    else if (!incomingRequest.IsBackground)
                    {
                        var isSuccessfulResponse = ((int)pr.StatusCode == 200 ||
                                                    (int)pr.StatusCode == 206 || // Partial Content
                                                    (int)pr.StatusCode == 201 || // Created
                                                    (int)pr.StatusCode == 202);  // Accepted
                        incomingRequest.RequestAPIStatus = isSuccessfulResponse
                            ? RequestAPIStatusEnum.Completed
                            : RequestAPIStatusEnum.Failed;
                        incomingRequest.asyncWorker?.UpdateBackup();
                    }

                }
                catch (S7PRequeueException e)
                {
                    // launches a delay task while the current worker goes back to the top of the loop for more work
                    _requeueDelayWorker!.DelayAsync(incomingRequest, e.RetryAfter);

                }
                catch (ProxyErrorException e)
                {
                    incomingRequest.SBStatus = ServiceBusMessageStatusEnum.Failed;

                    // Handle proxy error
                    eventData.Status = e.StatusCode;
                    eventData["Error"] = "Proxy Exception";
                    eventData.Type = EventType.Exception;
                    eventData.Exception = e;

                    var errorMessage = Encoding.UTF8.GetBytes(e.Message);

                    if (lcontext == null)
                    {
                        _logger.LogError("Context is null in ProxyErrorException");
                        continue;
                    }

                    try
                    {
                        lcontext.Response.StatusCode = (int)e.StatusCode;
                        await lcontext.Response.OutputStream.WriteAsync(errorMessage).ConfigureAwait(false);
                        _logger.LogWarning("Proxy error: {Message}", e.Message);
                    }
                    catch (Exception writeEx)
                    {
                        _logger.LogError(writeEx, "Failed to write error message for request {Guid}", incomingRequest?.Guid);

                        eventData["ErrorDetail"] = "Network Error sending error response";
                        eventData.Type = EventType.Exception;
                        eventData.Exception = writeEx;
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
                        eventData.Status = HttpStatusCode.RequestTimeout; // 408 Request Timeout
                        eventData.Type = EventType.Exception;
                        eventData.Exception = ioEx;
                        var errorMessage = "IO Exception: " + ioEx.Message;
                        eventData["ErrorDetails"] = errorMessage;

                        if (lcontext == null)
                        {
                            _logger.LogError("Context is null in IOException");
                            continue;
                        }
                        try
                        {
                            lcontext.Response.StatusCode = (int)eventData.Status;
                            var errorBytes = Encoding.UTF8.GetBytes(errorMessage);
                            await lcontext.Response.OutputStream.WriteAsync(errorBytes).ConfigureAwait(false);
                            _logger.LogError(ioEx, "An IO exception occurred for request {Guid}", incomingRequest?.Guid);
                        }
                        catch (Exception writeEx)
                        {
                            _logger.LogError(writeEx, "Failed to write error message for request {Guid}", incomingRequest?.Guid);
                            eventData["InnerErrorDetail"] = "Network Error";
                            eventData["InnerErrorStack"] = writeEx.StackTrace?.ToString() ?? "No Stack Trace";
                        }
                    }
                }
                catch (TaskCanceledException)
                {
                    if (_asyncExpelInProgress)
                    {
                        abortTask = true;
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
                        eventData.Status = HttpStatusCode.InternalServerError; // 500 Internal Server Error
                        eventData.Type = EventType.Exception;
                        eventData.Exception = ex;
                        eventData["WorkerState"] = workerState;

                        if (ex.Message == "Cannot access a disposed object." || ex.Message.StartsWith("Unable to write data") || ex.Message.Contains("Broken Pipe")) // The client likely closed the connection
                        {
                            _logger.LogInformation("Client closed connection: {FullURL}", incomingRequest?.FullURL ?? "Unknown");
                            eventData["InnerErrorDetail"] = "Client Disconnected";
                        }
                        else
                        {
                            requestException = true;

                            // Set an appropriate status code for the error
                            var errorMessage = "Exception: " + ex.Message;
                            eventData["ErrorDetails"] = errorMessage;

                            if (lcontext == null)
                            {
                                _logger.LogError("Context is null in General Exception");
                                continue;
                            }

                            try
                            {
                                lcontext.Response.StatusCode = 500;
                                var errorBytes = Encoding.UTF8.GetBytes(errorMessage);
                                await lcontext.Response.OutputStream.WriteAsync(errorBytes).ConfigureAwait(false);
                            }
                            catch (Exception writeEx)
                            {
                                eventData["InnerErrorDetail"] = "Network Error";
                                eventData["InnerErrorStack"] = writeEx.StackTrace?.ToString() ?? "No Stack Trace";
                            }
                        }
                    }
                }
                finally
                {
                    try
                    {
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

                        // Request is still valid, clean up
                        if (!incomingRequest.Requeued &&      // Request is not being requeued for retry
                            !_asyncExpelInProgress &&          // reserved for async operations which will resume after reboot
                            !incomingRequest.IsBackground)    // Not a background request - covers the background skip case
                        {

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
                    }
                    catch (Exception e)
                    {
                        ProxyEvent errorEvent = new(eventData)
                        {
                            Type = EventType.Exception,
                            Exception = e,
                            Status = HttpStatusCode.InternalServerError,
                            ["WorkerState"] = workerState,
                            ["Message"] = e.Message,
                            ["StackTrace"] = e.StackTrace ?? "No Stack Trace"
                        };
                        errorEvent.SendEvent();
                        _logger.LogError(e, "Error in finally block");
                    }

                    Interlocked.Decrement(ref states[7]);
                    workerState = "Exit - Finally";
                }
            }   // lifespan of incomingRequest
        }       // while running loop

        Interlocked.Decrement(ref activeWorkers);

        _logger.LogDebug("[SHUTDOWN] ✓ Worker {IdStr} stopped", _idStr);
    }

    private void AddIncompleteRequestsToEventData(List<Dictionary<string, string>> incompleteRequests, ConcurrentDictionary<string, string> eventData)
    {
        int i = 0;
        foreach (var summary in incompleteRequests)
        {
            i++;
            foreach (var key in summary.Keys)
            {
                eventData[$"Attempt-{i}-{key}"] = summary[key];
            }
        }
    }

    public void ExpelAsyncRequest()
    {
        if (_asyncExpelSource != null)
        {
            _asyncExpelInProgress = true;
            _logger.LogDebug("Expelling async request in progress, cancelling the token.");
            _asyncExpelSource.Cancel();
        }

    }

    /*
     * REQUEST EXECUTION MODES EXPLAINED:
     * 
     * 1. SYNCHRONOUS (default):
     *    - Client waits for response
     *    - Response streamed directly to client connection
     *    - No blob storage involved
     *    - request.runAsync = false
     * 
     * 2. ASYNC (triggered when request processing time > AsyncTriggerTimeout):
     *    - AsyncWorker created and started
     *    - Client receives 202 Accepted immediately
     *    - Response written to blob storage
     *    - Status tracked via Service Bus
     *    - Client polls later to retrieve result
     *    - request.runAsync = true, request.AsyncTriggered = true after worker starts
     * 
     * 3. BACKGROUND (OpenAI Batch API):
     *    - Backend returns batch ID instead of final result
     *    - S7P stores batch ID and polls backend periodically
     *    - When complete, final result written to blob
     *    - Client retrieves completed result
     *    - request.IsBackground = true
     * 
     * 4. BACKGROUND CHECK (Polling for batch completion):
     *    - Periodic status check requests for background jobs
     *    - Checks if batch processing is complete
     *    - request.IsBackgroundCheck = true
     * 
     * KEY TRANSITIONS:
     *    Synchronous -> Async (when timeout approaching, AsyncWorker.StartAsync called)
     *    Async -> Background (when stream processor detects batch ID in response)
     *    Background -> BackgroundCheck (when polling for batch status)
     * 
     * RESPONSE ROUTING:
     *    - Synchronous: response -> client HTTP connection
     *    - Async/Background: response -> blob storage (via asyncWorker.WriteAsync)
     *    - BackgroundCheck: status response -> blob storage if complete, or re-queue if pending
     */
    /// <summary>
    /// Processes a proxy request through the backend with support for synchronous, asynchronous, and background execution modes.
    /// </summary>
    /// <param name="request">The incoming request to proxy to the backend</param>
    /// <returns>ProxyData containing response headers, status, and metadata</returns>
    /// <exception cref="ArgumentNullException">Thrown when request or required fields are null</exception>
    /// <exception cref="ProxyErrorException">Thrown when all backend hosts fail or request expires</exception>
    /// <exception cref="S7PRequeueException">Thrown when backend requests requeue with retry-after</exception>
    public async Task<ProxyData> ProxyToBackEndAsync(RequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Body, nameof(request.Body));
        ArgumentNullException.ThrowIfNull(request.Headers, nameof(request.Headers));
        ArgumentNullException.ThrowIfNull(request.Method, nameof(request.Method));

        List<Dictionary<string, string>> incompleteRequests = request.incompleteRequests;

        request.Debug = _debug || (request.Headers["S7PDEBUG"] != null && string.Equals(request.Headers["S7PDEBUG"], "true", StringComparison.OrdinalIgnoreCase));
        HttpStatusCode lastStatusCode = HttpStatusCode.ServiceUnavailable;
        var requestSummary = request.EventData;
        int intCode = 0;

        // Read the body stream once and reuse it
        //byte[] bodyBytes = await request.CachBodyAsync().ConfigureAwait(false);
        List<S7PRequeueException> retryAfter = new();

        string modifiedPath = "";
        // Get an iterator for the active hosts based on the load balancing mode and iteration strategy
        using var hostIterator = _options.IterationMode switch
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
        request.Path = modifiedPath;

        if ( request.Debug) {
            // count the number of hosts
            int hostCount = 0;
            while (hostIterator.MoveNext())
            {
                hostCount++;
                _logger.LogDebug("Host {HostNumber}: {PartialPath} ({Guid})", 
                    hostCount, hostIterator.Current.Config.PartialPath, hostIterator.Current.Config.Guid);
            }
            // Reset the iterator to the beginning
            hostIterator.Reset();
            _logger.LogDebug("Matched Hosts: {HostCount} for URL: {RequestPath}", hostCount, request.Path);
        }

        // Try the request on each active host, stop if it worked
        while (hostIterator.MoveNext())
        {
            var host = hostIterator.Current;
            DateTime proxyStartDate = DateTime.UtcNow;

            if (host.Config.CheckFailedStatus())
            {
                continue;
            }
                    
            // track the number of attempts
            request.BackendAttempts++;
            bool successfulRequest = false;
            string requestState = "Init";

            ProxyEvent requestAttempt = new(request.EventData)
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

                // Check ExpiresAt against current time .. keep in mind, client may have disconnected already
                if (request.ExpiresAt < DateTimeOffset.UtcNow)
                {
                    string errorMessage = $"Request has expired: Time: {DateTime.UtcNow:o}  Reason: {request.ExpireReason}";
                    // requestSummary.Type = EventType.ProxyRequestExpired;
                    requestSummary["Disposition"] = "Expired";
                    request.SkipDispose = false;
                    throw new ProxyErrorException(ProxyErrorException.ErrorType.TTLExpired,
                                                HttpStatusCode.PreconditionFailed,
                                                errorMessage);
                }


                var minDate = request.ExpiresAt < DateTime.UtcNow.AddMilliseconds(request.defaultTimeout)
                    ? request.ExpiresAt
                    : DateTime.UtcNow.AddMilliseconds(request.defaultTimeout);
                request.Timeout = (int)(minDate - DateTime.UtcNow).TotalMilliseconds;

                request.Headers.Set("Host", host.Host);
                request.FullURL = host.Config.BuildDestinationUrl(request.Path);

                requestState = "Cache Body";
                // Read the body stream once and reuse it
                byte[] bodyBytes = await request.CacheBodyAsync().ConfigureAwait(false);

                requestState = "Create Backend Request";
                using (ByteArrayContent bodyContent = new(bodyBytes))
                using (HttpRequestMessage proxyRequest = new(new(request.Method), request.FullURL))
                {
                    proxyRequest.Content = bodyContent;
                   // request.Headers["Content-Type"] = "application/json";
                    
                    CopyHeaders(request.Headers, proxyRequest, true);
                    proxyRequest.Headers.Add("x-PolicyCycleCounter", request.TotalDownstreamAttempts.ToString());

                    if (bodyBytes.Length > 0)
                    {

                        proxyRequest.Content.Headers.ContentLength = bodyBytes.Length;

                        // Preserve the content type if it was provided, otherwise default to application/json
                        var contentType = request.Context?.Request.ContentType ?? "application/json";
                        MediaTypeHeaderValue mediaTypeHeaderValue;

                        try
                        {
                            mediaTypeHeaderValue = MediaTypeHeaderValue.Parse(contentType);

                            if (string.IsNullOrWhiteSpace(mediaTypeHeaderValue.CharSet))
                            {
                                mediaTypeHeaderValue.CharSet = "utf-8";
                            }

                        }
                        catch (Exception e)
                        {
                            // Default to application/json if parsing fails
                            mediaTypeHeaderValue = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                            _logger.LogInformation("Invalid content type provided, defaulting to application/json: {Message}", e.Message);
                        }

                        proxyRequest.Content.Headers.ContentType = mediaTypeHeaderValue;
                    }
                    else
                    {
                        // Even if there's no body, set the content type to application/json
                        proxyRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                    }
                    proxyRequest.Headers.ConnectionClose = true;

                    // Log request headers if debugging is enabled
                    if (request.Debug)
                    {
                        _logger.LogDebug("> {Method} {FullURL} {BodyLength} bytes", 
                            request.Method, request.FullURL, bodyBytes.Length);
                        LogHeaders(proxyRequest.Headers, ">");
                        LogHeaders(proxyRequest.Content.Headers, "  >");
                        //string bodyString = System.Text.Encoding.UTF8.GetString(bodyBytes);
                        //Console.WriteLine($"Body Content: {bodyString}");
                    }

                    // Send the request and get the response
                    proxyStartDate = DateTime.UtcNow;
                    Interlocked.Increment(ref states[3]);
                    try
                    {
                        // ASYNC: Calculate the timeout, start async worker
                        _asyncExpelInProgress = false;
                        requestState = "Make Backend Request";


                        // Create ASYNC Worker if needed, and setup the timeout
                        var (workerCts, rTimeout) = SetupAsyncWorkerAndTimeout(request);
                        CancellationTokenSource cts = workerCts;


                        // SEND THE REQUEST TO THE BACKEND USING THE APROPRIATE TIMEOUT.  CTS is only used here.
                        using var proxyResponse = await _options.Client!.SendAsync(
                            proxyRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

                        var responseDate = DateTime.UtcNow;
                        lastStatusCode = proxyResponse.StatusCode;
                        requestAttempt.Status = proxyResponse.StatusCode;

                        requestState = "Process Backend Response";

                        // Capture the response
                        ProxyData pr = new()
                        {
                            ResponseDate = responseDate,
                            StatusCode = proxyResponse.StatusCode,
                            FullURL = request.FullURL,
                            CalculatedHostLatency = host.CalculatedAverageLatency,
                            BackendHostname = host.Host
                        };

                        // ASYNC: We got a response back, Synchronize with the asyncWorker 
                        //if ((int)proxyResponse.StatusCode == 200 && request.runAsync)
                        if (request.runAsync)
                        {
                            if (request.asyncWorker == null)
                            {
                                _logger.LogError("AsyncWorker is null but runAsync is true for request {Guid}", request.Guid);
                            }
                            else if (!await request.asyncWorker.Synchronize()) // Wait for the worker to finish setting up the blob's, etc...
                            {
                                pr.Headers["x-Async-Error"] = request.asyncWorker.ErrorMessage;
                                request.SBStatus = ServiceBusMessageStatusEnum.AsyncProcessingError;

                                //_logger.LogError($"AsyncWorker failed to setup: {request.asyncWorker.ErrorMessage}");
                            }
                        }

                        if (proxyResponse.Headers.TryGetValues("x-PolicyCycleCounter", out var policyAttempts))
                        {
                            if (int.TryParse(policyAttempts.FirstOrDefault(), out var pAttempts))
                            {
                                request.TotalDownstreamAttempts = pAttempts;
                            }
                        }

                        // Check if the status code of the response is in the set of allowed status codes, else try the next host
                        intCode = (int)proxyResponse.StatusCode;
                        if ((intCode > 300 && intCode < 400) || intCode == 404 || intCode == 412 || intCode >= 500)
                        {
                            requestState = "Call unsuccessful";

                            foreach (var header in proxyResponse.Headers)
                            {
                                if (excluded.Contains(header.Key)) continue;
                                requestAttempt[header.Key] = string.Join(", ", header.Value);
                                //Console.WriteLine($"  {header.Key}: {requestAttempt[header.Key]}");
                            }

                            if (request.Debug)
                            {
                                try
                                {
                                    // Read the response body so that we can get the byte length ( DEBUG ONLY )  
                                    //bodyBytes = [];
                                    //await GetProxyResponseAsync(proxyResponse, request, pr).ConfigureAwait(false);
                                    _logger.LogDebug("Got: {StatusCode} {FullURL} {ContentLength} Body: {BodyLength} bytes",
                                        pr.StatusCode, pr.FullURL, pr.ContentHeaders["Content-Length"], pr?.Body?.Length);
                                    _logger.LogDebug("< {Body}", pr?.Body);
                                }
                                catch (Exception e)
                                {
                                    _logger.LogError(e, "Error reading from backend host {BackendHost}", host.Host);
                                }

                                _logger.LogDebug("Trying next host: Response: {StatusCode}", proxyResponse.StatusCode);
                            }

                            // The request did not succeed, try the next host
                            continue;
                        }
                        else if (intCode == 429 && proxyResponse.Headers.TryGetValues("S7PREQUEUE", out var values))
                        {
                            requestState = "Process 429";

                            foreach (var header in proxyResponse.Headers)
                            {
                                if (excluded.Contains(header.Key)) continue;
                                requestAttempt[header.Key] = string.Join(", ", header.Value);
                                // Console.WriteLine($"  {header.Key}: {requestAttempt[header.Key]}");
                            }

                            // Requeue the request if the response is a 429 and the S7PREQUEUE header is set
                            // It's possible that the next host processes this request successfully, in which case these will get ignored
                            var s7PrequeueValue = values.FirstOrDefault();

                            if (s7PrequeueValue != null && string.Equals(s7PrequeueValue, "true", StringComparison.OrdinalIgnoreCase))
                            {
                                // we're keep track of the retry after values for later.
                                proxyResponse.Headers.TryGetValues("retry-after-ms", out var retryAfterValuesMS);
                                if (retryAfterValuesMS != null && int.TryParse(retryAfterValuesMS.FirstOrDefault(), out var retryAfterValueMS))
                                {
                                    throw new S7PRequeueException("Requeue request", pr, retryAfterValueMS);
                                }

                                proxyResponse.Headers.TryGetValues("retry-after", out var retryAfterValues);
                                if (retryAfterValues != null && int.TryParse(retryAfterValues.FirstOrDefault(), out var retryAfterValue))
                                {
                                    throw new S7PRequeueException("Requeue request", pr, retryAfterValue * 1000);
                                }


                                throw new S7PRequeueException("Requeue request", pr, 1000);
                            }
                        }
                        else
                        {
                            // request was successful, so we can disable the skip
                            request.SkipDispose = false;
                            requestAttempt["RequestSuccess"] = "true"; // Track success in event data
                            bodyBytes = [];
                        }

                        host.AddPxLatency((responseDate - proxyStartDate).TotalMilliseconds);
                        // copy headers from the response to the ProxyData object
                        CopyResponseHeaders(proxyResponse, pr);

                        pr.Headers["x-BackendHost"] = requestSummary["Backend-Host"] = pr.BackendHostname;
                        pr.Headers["x-Request-Queue-Duration"] = requestSummary["Request-Queue-Duration"] = request.Headers["x-Request-Queue-Duration"] ?? "N/A";
                        pr.Headers["x-Request-Process-Duration"] = requestSummary["Request-Process-Duration"] = request.Headers["x-Request-Process-Duration"] ?? "N/A";
                        pr.Headers["x-Total-Latency"] = requestSummary["Total-Latency"] = (DateTime.UtcNow - request.EnqueueTime).TotalMilliseconds.ToString("F3");

                        // Strip headers from pr.Headers that are not allowed in the response
                        foreach (var header in _options.StripHeaders)
                        {
                            if (pr.Headers.Get(header) != null)
                            {
                                pr.Headers.Remove(header);
                            }
                        }

                        // ASYNC: If the request was triggered asynchronously, we need to write the response to the async worker blob
                        // TODO: Move to caller to handle writing errors?
                        // Store the response stream in proxyData and return to parent caller
                        // WAS ASYNC SYNCHRONIZED?
                        if (request.AsyncTriggered)
                        {
                            // Write the headers to the async worker blob [ the client connection is closed ]
                            if (!await request.asyncWorker!.WriteHeaders(proxyResponse.StatusCode, pr.Headers))
                            {
                                throw new ProxyErrorException(ProxyErrorException.ErrorType.AsyncWorkerError,
                                                              HttpStatusCode.InternalServerError, "Failed to write headers to async worker");
                            }
                        }
                        else
                        {
                            request.Context!.Response.StatusCode = (int)proxyResponse.StatusCode;
                            request.Context.Response.Headers = pr.Headers;
                        }

                        string mediaType = proxyResponse.Content?.Headers?.ContentType?.MediaType ?? string.Empty;

                        // pull out the processor from response headers if it exists
                        if (host.Config.DirectMode)
                        {
                            pr.StreamingProcessor = host.Config.Processor;
                            requestState = "Direct Mode Processor: " + pr.StreamingProcessor;
                        } else {
                            pr.StreamingProcessor = StreamProcessorFactory.DetermineStreamProcessor(proxyResponse, mediaType);
                            requestState = "Stream Proxy Response : " + pr.StreamingProcessor;
                        }

                        // Stream response from backend to client/blob
                        try
                        {
                            await StreamResponseAsync(request, proxyResponse, pr.StreamingProcessor, mediaType).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            _logger.LogError(e, "Error streaming response for request {Guid} to {FullURL}", 
                                request.Guid, request.FullURL);
                            throw;
                        }

                        try
                        {
                            _logger.LogDebug("Flushing output stream for {FullURL}", request.FullURL);
                            if (request.OutputStream != null)
                            {
                                await request.OutputStream.FlushAsync().ConfigureAwait(false);

                                if (request.OutputStream is BufferedStream bufferedStream)
                                {
                                    await bufferedStream.FlushAsync().ConfigureAwait(false);
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            _logger.LogDebug(e, "Unable to flush output stream for {FullURL}", request.FullURL);
                        }

                        // Log the response if debugging is enabled
                        if (request.Debug)
                        {
                            _logger.LogDebug("Got: {StatusCode} {FullURL} {ContentLength} Body: {BodyLength} bytes",
                                pr.StatusCode, pr.FullURL, pr.ContentHeaders["Content-Length"], pr?.Body?.Length);
                        }

                        successfulRequest = true;
                        return pr ?? throw new ArgumentNullException(nameof(pr));
                    }
                    finally
                    {
                        Interlocked.Decrement(ref states[3]);
                    }
                }
            }
            catch (S7PRequeueException e)
            {
                requestAttempt.Status = HttpStatusCode.TooManyRequests; // 429 Too Many Requests
                requestAttempt["Message"] = "Will retry if no other hosts are available";
                requestAttempt["Error"] = "Requeue request: Retry-After = " + e.RetryAfter;

                // Try all the hosts before sleeping
                retryAfter.Add(e);
                continue;
            }
            catch (ProxyErrorException e)
            {
                requestAttempt.Status = e.StatusCode;
                requestAttempt["Error"] = e.Message;

                if (e.Type == ProxyErrorException.ErrorType.TTLExpired)
                    break;

                continue;
            }
            catch (TaskCanceledException)
            {

                if (_asyncExpelInProgress)
                {
                    if (request.asyncWorker != null)
                    {
                        request.asyncWorker.ShouldReprocess = true;
                    }

                    requestAttempt.Status = HttpStatusCode.ServiceUnavailable;
                    requestAttempt["Error"] = "Request being expelled";
                    requestAttempt["Message"] = "Request will rehydrate on startup";

                    throw;
                }
                else
                {
                    // 408 Request Timeout

                    requestAttempt.Status = HttpStatusCode.RequestTimeout;
                    requestAttempt["Expires-At"] = request.ExpiresAt.ToString("o");
                    requestAttempt["MaxTimeout"] = _options.Timeout.ToString();
                    requestAttempt["Request-Date"] = proxyStartDate.ToString("o");
                    requestAttempt["Request-Timeout"] = request.Timeout.ToString() + " ms";
                    requestAttempt["Error"] = "Request Timed out";
                    requestAttempt["Message"] = "Operation TIMEOUT";

                    continue;
                }
            }
            catch (OperationCanceledException)
            {

                // 408 Request Timeout
                requestAttempt.Status = HttpStatusCode.RequestTimeout;
                requestAttempt["Error"] = "Request Cancelled";
                requestAttempt["Message"] = "Operation CANCELLED";

                continue;

            }
            catch (HttpRequestException e)
            {
                // 400 Bad Request
                requestAttempt.Status = HttpStatusCode.BadRequest;
                requestAttempt["Error"] = "Bad Request: " + e.Message;
                requestAttempt["Message"] = "Operation Exception: HttpRequest";

                continue;
            }
            catch (Exception e)
            {
                if (e.Message.StartsWith("The format of value"))
                {
                    throw new ProxyErrorException(ProxyErrorException.ErrorType.InvalidHeader, HttpStatusCode.BadRequest, "Bad header: " + e.Message);
                }
                // 500 Internal Server Error
                _logger.LogError(e, "Internal server error processing request {Guid} to {FullURL}", 
                    request.Guid, request.FullURL);

                requestAttempt.Status = HttpStatusCode.InternalServerError;
                requestAttempt["Error"] = "Internal Error: " + e.Message;
            }
            finally
            {                    
                // Add the request attempt to the summary
                requestAttempt.Duration = DateTime.UtcNow - proxyStartDate;
                requestAttempt.SendEvent();  // Log the dependent request attempt

                hostIterator.RecordResult(host, successfulRequest);
                
                // Track host status for circuit breaker
                host.Config.TrackStatus(intCode, successfulRequest);

                if (!successfulRequest)
                {

                    var miniDict = requestAttempt.ToDictionary(backendKeys);
                    miniDict["State"] = requestState;
                    incompleteRequests.Add(miniDict);

                    var str = JsonSerializer.Serialize(miniDict);
                    _logger.LogDebug(str);
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
        GenerateErrorMessage(incompleteRequests, out sb, out statusMatches, out currentStatusCode);

        // 502 Bad Gateway  or   call status code form all attempts ( if they are the same )
        lastStatusCode = (statusMatches) ? (HttpStatusCode)currentStatusCode : HttpStatusCode.BadGateway;
        // requestSummary.Type = EventType.ProxyError;

        // STREAM SERVER ERROR RESPONSE.  Must respond because the request was not successful
        try
        {
            if (!request.AsyncTriggered)
            {
                _logger.LogInformation("Response Status Code: {StatusCode} for request {Guid}", 
                    lastStatusCode, request.Guid);
                request.Context!.Response.StatusCode = (int)lastStatusCode;
                request.Context.Response.KeepAlive = false;
            }

            if (request.Context != null)
            {
                request.Context.Response.Headers["x-Request-Queue-Duration"] = (request.DequeueTime - request.EnqueueTime).TotalMilliseconds.ToString("F3") + " ms";
                request.Context.Response.Headers["x-Total-Latency"] = (DateTime.UtcNow - request.EnqueueTime).TotalMilliseconds.ToString("F3") + " ms";
                request.Context.Response.Headers["x-ProxyHost"] = _options.HostName;
                request.Context.Response.Headers["x-MID"] = request.MID;
                request.Context.Response.Headers["Attempts"] = request.BackendAttempts.ToString();
            }

            if (request.OutputStream != null)
            {
                await request.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString())).ConfigureAwait(false);
                await request.OutputStream.FlushAsync().ConfigureAwait(false);
            }

        }
        catch (Exception e)
        {
            // If we can't write the response, we can only log it
            _logger.LogError(e, "Error writing response for request {Guid}", request.Guid);
        }


        return new ProxyData
        {
            FullURL = request.FullURL,

            CalculatedHostLatency = (DateTime.UtcNow - request.EnqueueTime).TotalMilliseconds,
            BackendHostname = "No Active Hosts Available",
            ResponseDate = DateTime.UtcNow,
            StatusCode = RecordIncompleteRequests(requestSummary, lastStatusCode, "No active hosts were able to handle the request", incompleteRequests),
            Body = Encoding.UTF8.GetBytes(sb.ToString())
        };
    }

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
    /// <param name="mediaType">The media type of the response content</param>
    private async Task StreamResponseAsync(RequestData request, HttpResponseMessage proxyResponse, string processWith, string mediaType)
    {
        ProxyEvent requestSummary = request.EventData;        

        _logger.LogInformation("Streaming response with processor. Requested: {ProcessorRequested}, ContentType: {ContentType}, Guid: {Guid}", 
            processWith, mediaType, request.Guid);

        IStreamProcessor processor = _streamProcessorFactory.GetStreamProcessor(processWith, out string resolvedProcessor);
        try
        {
            _logger.LogDebug("Resolved processor: {ProcessorName} for request {Guid}", resolvedProcessor, request.Guid);

            if (request.OutputStream != null)
            {
                await processor.CopyToAsync(proxyResponse.Content, request.OutputStream).ConfigureAwait(false);
            }
            else
            {
                _logger.LogError("OutputStream is null for request {Guid}, cannot stream response", request.Guid);
            }
        }
        catch (IOException e)
        {
            _logger.LogError(e, "IO Error streaming response for request {Guid}", request.Guid);

        }
        catch (Exception ex) when (ex.InnerException is IOException ioEx)
        {
            // Most likely a client disconnect, log at debug level
            _logger.LogDebug(ioEx, "Client disconnected while streaming response for request {Guid}", request.Guid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error streaming response for request {Guid}", request.Guid);
            throw new ProxyErrorException(
                ProxyErrorException.ErrorType.ClientDisconnected,
                HttpStatusCode.InternalServerError,
                ex.Message);
        }
        finally
        {
            try
            {
                // BACKGROUND REQUEST LIFECYCLE MANAGEMENT
                // When a processor detects a background request ID during streaming (e.g., OpenAI batch API),
                // we track it for periodic status polling via the background checker component.

                if (!string.IsNullOrEmpty(processor.BackgroundRequestId) && request.runAsync)
                {
                    _logger.LogInformation("Found background request for GUID: {Guid}, BackgroundRequestId: {BackgroundRequestId}", 
                        request.Guid, processor.BackgroundRequestId);
                    request.IsBackground = true;
                    request.BackgroundRequestId = processor.BackgroundRequestId;
                    
                    if (request.asyncWorker == null)
                    {
                        _logger.LogError("AsyncWorker is null but runAsync is true for request {Guid}", request.Guid);
                    }
                    else
                    {
                        // Handle three distinct scenarios based on request type and completion status:
                        
                        if (!request.IsBackgroundCheck)
                        {
                            // Scenario 1: Initial background request submission
                            // Mark as BackgroundProcessing to trigger periodic polling
                            _logger.LogInformation("Updating async worker for background GUID: {Guid}, BackgroundRequestId: {BackgroundRequestId}", 
                                request.Guid, processor.BackgroundRequestId);
                            await request.asyncWorker.UpdateBackup().ConfigureAwait(false);
                            request.BackgroundRequestCompleted = false;
// REMOVE                            request.RequestAPIStatus = RequestAPIStatusEnum.BackgroundProcessing;
                        }
                        else if (processor.BackgroundCompleted)
                        {
                            // Scenario 2: Background check found completed task
                            // Update status to Completed so caller can retrieve final results
                            _logger.LogInformation("Background processing completed for GUID: {Guid}, BackgroundRequestId: {BackgroundRequestId}", 
                                request.Guid, processor.BackgroundRequestId);
                            request.BackgroundRequestCompleted = true;
// REMOVE                            request.RequestAPIStatus = RequestAPIStatusEnum.Completed;
                        }
                        else
                        {
                            // Scenario 3: Background check found task still running
                            // retrigger the API to continue polling
                            _logger.LogInformation("Background processing still in progress for GUID: {Guid}, BackgroundRequestId: {BackgroundRequestId}", 
                                request.Guid, processor.BackgroundRequestId);
                            request.BackgroundRequestCompleted = false;
                        }
                    }
                } else
                {
                    _logger.LogDebug("No background request ID found in processor for request {Guid}", request.Guid);
                }
            }
            catch (Exception statsEx)
            {
                _logger.LogDebug(statsEx, "Processor stats collection failed for request {Guid}", request.Guid);
            }
            finally
            {
                if (processor is IDisposable disposableProcessor)
                {
                    try { disposableProcessor.Dispose(); }
                    catch (Exception processorDisposeEx)
                    {
                        _logger.LogDebug(processorDisposeEx, "Processor disposal failed for request {Guid}", request.Guid);
                    }
                }
            }
        }
    }


    private (CancellationTokenSource, double) SetupAsyncWorkerAndTimeout(RequestData request)
    {
        double timeout = request.Timeout;
        CancellationTokenSource cts;

        if (request.runAsync)
        {
            timeout = _options.AsyncTimeout;
            if (request.asyncWorker is null)
            {
                var timeLeft = _options.AsyncTriggerTimeout - (int)(DateTime.UtcNow - request.EnqueueTime).TotalMilliseconds;
                timeLeft = Math.Max(1, timeLeft);
                request.asyncWorker = _asyncWorkerFactory.CreateAsync(request, timeLeft);
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

    private static void GenerateErrorMessage(List<Dictionary<string, string>> incompleteRequests, out StringBuilder sb, out bool statusMatches, out int currentStatusCode)
    {
        sb = new StringBuilder();
        sb.AppendLine("Error processing request.  No active hosts were able to handle the request.");
        sb.AppendLine("Request Summary:");
        statusMatches = true;
        currentStatusCode = -1;

        var statusCodes = new List<int>();

        int iter = 0;
        Dictionary<string, object> requestSummary = [];
        foreach (var requestAttempt in incompleteRequests)
        {
            if (requestAttempt.TryGetValue("Status", out var statusStr) && int.TryParse(statusStr, out var status))
            {
                statusCodes.Add(status);
            }

            iter++;
            //requestSummary["Attempt-" + iter] = JsonSerializer.Serialize(requestAttempt);
            requestSummary["Attempt-" + iter] = requestAttempt;
        }

        if (statusCodes.Count > 0)
        {

            // Console.WriteLine($"Status Codes: {string.Join(", ", statusCodes)}");

            // If all status codes are 408 or 412, use the latest (last) one
            if (statusCodes.All(s => s == 408 || s == 412 || s == 429))
            {
                currentStatusCode = statusCodes.Last();
                statusMatches = true;
            }
            // If all status codes are the same, return that one
            else if (statusCodes.Distinct().Count() == 1)
            {
                currentStatusCode = statusCodes.First();
                statusMatches = true;
            }
            else
            {
                statusMatches = false;
                currentStatusCode = statusCodes.Last();
            }
        }
        sb.AppendLine(JsonSerializer.Serialize(requestSummary, new JsonSerializerOptions { WriteIndented = true }));
        sb.AppendLine();
    }

    private static void CopyHeaders(
        NameValueCollection sourceHeaders,
        HttpRequestMessage? targetMessage,
        bool ignoreHeaders = false)
    {
        foreach (var key in sourceHeaders.AllKeys)
        {
            if (key == null) continue;
            if (!ignoreHeaders || (!key.StartsWith("S7P") && !key.StartsWith("X-MS-CLIENT", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("content-length", StringComparison.OrdinalIgnoreCase)))
            {
                targetMessage?.Headers.TryAddWithoutValidation(key, sourceHeaders[key]);
            }
        }
    }

    // Exclude hop-by-hop and restricted headers that HttpListener manages
    static HashSet<string> excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Length","Transfer-Encoding","Connection","Proxy-Connection","Keep-Alive","Upgrade","Trailer","TE","Date","Server"
    };
    private static void CopyResponseHeaders(HttpResponseMessage response, ProxyData pr)
    {

        // Response headers
        foreach (var header in response.Headers)
        {
            if (excluded.Contains(header.Key)) continue;
            pr.Headers[header.Key] = string.Join(", ", header.Value);
        }

        // Content headers
        foreach (var header in response.Content.Headers)
        {
            if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                pr.ContentHeaders[header.Key] = string.Join(", ", header.Value);
                continue;
            }

            if (!excluded.Contains(header.Key))
            {
                pr.Headers[header.Key] = string.Join(", ", header.Value);
            }
            pr.ContentHeaders[header.Key] = string.Join(", ", header.Value);
        }
    }

    private void LogHeaders(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers, string prefix)
    {
        foreach (var header in headers)
        {
            _logger.LogDebug("{Prefix} {HeaderKey} : {HeaderValues}", prefix, header.Key, string.Join(", ", header.Value));
        }
    }
    private HttpStatusCode RecordIncompleteRequests (
        ConcurrentDictionary<string, string> data,
        HttpStatusCode statusCode,
        string message,
        List<Dictionary<string, string>>? incompleteRequests = null,
        Exception? e = null)
    {
        data["Status"] = statusCode.ToString();
        data["Message"] = message;

        if (incompleteRequests != null)
        {
            AddIncompleteRequestsToEventData(incompleteRequests, data);
        }

        return statusCode;
    }

}