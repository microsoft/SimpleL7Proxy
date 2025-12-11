using System.Net;
using System.Net.Http.Headers;
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

    private static string[] s_backendKeys = Array.Empty<string>();

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
    }

    public async Task TaskRunnerAsync()
    {
        bool doUserconfig = _options.UseProfiles;
        string workerState = string.Empty;

        if (doUserconfig && _profiles == null) throw new ArgumentNullException(nameof(_profiles));
        if (s_requestsQueue == null) throw new ArgumentNullException(nameof(s_requestsQueue));

        // Only for use during shutdown after graceseconds have expired
        // CancellationTokenSource cts = new CancellationTokenSource();
        // CancellationToken token = cts.Token;

        // increment the active workers count
        HealthCheckService.IncrementActiveWorkers(_options.Workers);
        if (_options.Workers == HealthCheckService.ActiveWorkers)
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
                try
                {
                    if (Constants.probes.Contains(incomingRequest.Path) && lcontext != null)
                    {
                        await _healthCheckService.HandleProbeRequestAsync(incomingRequest.Path, lcontext, eventData).ConfigureAwait(false);

                        HealthCheckService.EnterState(_id, WorkerState.Cleanup);

                        //Console.WriteLine($"Probe: {incomingRequest.Path} Status: {probeStatus} Message: {probeMessage}");

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
                    ProxyData pr = null!;

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

                    eventData.Status = pr.StatusCode;
                    _eventDataBuilder.PopulateHeaderEventData(incomingRequest, pr.Headers);

                    // Determine final status based on response
                    if (pr.StatusCode == HttpStatusCode.PreconditionFailed || pr.StatusCode == HttpStatusCode.RequestTimeout) // 412 or 408
                    {
                        isExpired = true;
                        _lifecycleManager.TransitionToExpired(incomingRequest);
                    }
                    else if (pr.StatusCode == HttpStatusCode.OK)
                    {
                        _lifecycleManager.TransitionToSuccess(incomingRequest, pr.StatusCode);
                    }
                    else  // Non-200, non-expired response - handle failures
                    {
                        _lifecycleManager.TransitionToFailed(incomingRequest, pr.StatusCode);
                    }

                    // SYNCHRONOUS MODE
                    //await WriteResponseAsync(lcontext, pr).ConfigureAwait(false);

                    //                    Task.Yield(); // Yield to the scheduler to allow other tasks to run
                    HealthCheckService.EnterState(_id, WorkerState.Reporting);
                    workerState = "Finalize";

                    var conlen = pr.ContentHeaders?["Content-Length"] ?? "N/A";
                    var proxyTime = (DateTime.UtcNow - incomingRequest.DequeueTime).TotalMilliseconds.ToString("F3");
                    var statusCodeInt = (int)pr.StatusCode;
                    _logger.LogCritical("Pri: {Priority}, Stat: {StatusCode}, User: {user} Guid: {Guid} Type: {RequestType}, Processor: {Processor}, Len: {ContentLength}, {FullURL}, Deq: {DequeueTime}, Lat: {ProxyTime} ms",
                        incomingRequest.Priority, statusCodeInt,
                        incomingRequest.UserID ?? "N/A",
                        incomingRequest.Guid,
                        incomingRequest.Type,
                        pr.StreamingProcessor,
                        conlen, pr.FullURL, incomingRequest.DequeueTime.ToLocalTime().ToString("T"), proxyTime);

                    // Log circuit breaker details when status code is -1
                    if (statusCodeInt == -1 || statusCodeInt ==  503)
                    {
                        _logger.LogCritical("[CircuitBreaker] Status {StatusCode} detected for request {Guid}. Backend host: {Host}",
                            statusCodeInt, incomingRequest.Guid, pr.BackendHostname);
                        
                        // Log circuit breaker status for all hosts
                        var activeHosts = _backends.GetActiveHosts();
                        foreach (var host in activeHosts)
                        {
                            var cbStatus = host.Config.GetCircuitBreakerStatus();
                            _logger.LogCritical("[CircuitBreaker] Guid: {guid} Host {HostName} - FailureCount: {FailureCount}/{Threshold}, IsBlocked: {IsBlocked}, SecondsUntilUnblock: {SecondsUntilExpiry}, OldestFailure: {OldestFailure}, NewestFailure: {NewestFailure}",
                                incomingRequest.Guid,
                                host.Host,
                                cbStatus["FailureCount"],
                                cbStatus["FailureThreshold"],
                                cbStatus["IsBlocked"],
                                cbStatus["SecondsUntilOldestExpires"],
                                cbStatus["OldestFailure"],
                                cbStatus["NewestFailure"]);
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
                        _lifecycleManager.FinalizeStatus(incomingRequest, isSuccessfulResponse);
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

                        if (ex.Message == "Cannot access a disposed object." || ex.Message.StartsWith("Unable to write data") || ex.Message.Contains("Broken Pipe")) // The client likely closed the connection
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
                        _logger.LogError(e, "[Worker:{Id}] CRITICAL: Unhandled error in finally block for request {Guid}", _id, incomingRequest!.Guid);
                    }
                }
            }   // lifespan of incomingRequest
        }       // while running loop

        HealthCheckService.DecrementActiveWorkers(_id);

        _logger.LogDebug("[SHUTDOWN] ✓ Worker {IdStr} stopped", _idStr);
    }

    public void ExpelAsyncRequest()
    {
        if (_asyncExpelSource != null)
        {
            _isEvictingAsyncRequest = true;
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

        var activeHosts = _backends.GetActiveHosts().Where(h => h.Config.PartialPath == request.Path || h.Config.PartialPath == "/").ToList();
        _logger.LogDebug("[ProxyToBackEnd:{Guid}] Found {HostCount} backend hosts for path {Path}", 
            request.Guid, activeHosts.Count, request.Path);

        if (activeHosts.Count == 0)
        {
            _logger.LogWarning("[ProxyToBackEnd:{Guid}] ⚠ NO BACKEND HOSTS matched path {Path} - Request will fail", 
                request.Guid, request.Path);
            
            // Log all available hosts and their paths for debugging
            var allHosts = _backends.GetActiveHosts();
            _logger.LogCritical("[ProxyToBackEnd:{Guid}] Available hosts and their paths:", request.Guid);
            foreach (var h in allHosts)
            {
                var cbStatus = h.Config.GetCircuitBreakerStatus();
                _logger.LogCritical("[ProxyToBackEnd:{Guid}]   - Host: {Host}, Path: {PartialPath}, CB-Failures: {FailureCount}/{Threshold}, CB-Blocked: {IsBlocked}", 
                    request.Guid, h.Host, h.Config.PartialPath, cbStatus["FailureCount"], cbStatus["FailureThreshold"], cbStatus["IsBlocked"]);
            }
        }

        if (request.Debug)
        {
            // count the number of hosts
            int debugHostCount = 0;
            while (hostIterator.MoveNext())
            {
                debugHostCount++;
                _logger.LogCritical("Host {HostNumber}: {PartialPath} ({Guid})",
                    debugHostCount, hostIterator.Current.Config.PartialPath, hostIterator.Current.Config.Guid);
            }
            // Reset the iterator to the beginning
            hostIterator.Reset();
            _logger.LogDebug("Matched Hosts: {HostCount} for URL: {RequestPath}", debugHostCount, request.Path);
        }

        // Try the request on each active host, stop if it worked
        while (hostIterator.MoveNext())
        {
            var host = hostIterator.Current;
            DateTime proxyStartDate = DateTime.UtcNow;

            if (host.Config.CheckFailedStatus())
            {
                var cbStatus = host.Config.GetCircuitBreakerStatus();
                _logger.LogCritical("[ProxyToBackEnd:{Guid}] ⚠ Circuit breaker BLOCKING host: {Host} - FailureCount: {FailureCount}/{Threshold}, Path: {PartialPath}, SecondsUntilUnblock: {SecondsUntilExpiry}", 
                    request.Guid, host.Host, cbStatus["FailureCount"], cbStatus["FailureThreshold"], host.Config.PartialPath, cbStatus["SecondsUntilOldestExpires"]);
                continue;
            }

            // track the number of attempts
            request.BackendAttempts++;
            _logger.LogDebug("[ProxyToBackEnd:{Guid}] Attempting backend host: {Host} (Attempt #{Attempt})", 
                request.Guid, host.Host, request.BackendAttempts);
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

                // Validate request hasn't expired
                _lifecycleManager.ValidateRequestNotExpired(request);

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
                        requestState = "Make Backend Request";


                        // Create ASYNC Worker if needed, and setup the timeout
                        // SEND THE REQUEST TO THE BACKEND USING THE APROPRIATE TIMEOUT.
                        // TO DO:   reuse the cts instead of creating a new one each time.
                        var (requestCts, rTimeout) = SetupAsyncWorkerAndTimeout(request);
                        DateTime responseDate;
                        using (requestCts)
                        {
                            HealthCheckService.EnterState(_id, WorkerState.Receiving);
                            using var proxyResponse = await _options.Client!.SendAsync(
                                proxyRequest, HttpCompletionOption.ResponseHeadersRead, requestCts.Token).ConfigureAwait(false);
                            responseDate = DateTime.UtcNow;
                            lastStatusCode = proxyResponse.StatusCode;
                            requestAttempt.Status = proxyResponse.StatusCode;

                            _logger.LogDebug("[ProxyToBackEnd:{Guid}] Received response from {Host} - Status: {StatusCode}, Duration: {Duration}ms", 
                                request.Guid, host.Host, lastStatusCode, (responseDate - proxyStartDate).TotalMilliseconds);

                            requestState = "Process Backend Response";

                            // Capture the response
                            ProxyData pr = new()
                            {
                                ResponseDate = responseDate,
                                StatusCode = lastStatusCode,
                                FullURL = request.FullURL,
                                CalculatedHostLatency = host.CalculatedAverageLatency,
                                BackendHostname = host.Host
                            };

                            // ASYNC: We got a response back, Synchronize with the asyncWorker 
                            //if ((int)proxyResponse.StatusCode == 200 && request.runAsync)
                            if (request.runAsync)
                            {
                                _logger.LogDebug("[ProxyToBackEnd:{Guid}] Synchronizing with AsyncWorker", request.Guid);
                                if (request.asyncWorker == null)
                                {
                                    _logger.LogError("AsyncWorker is null but runAsync is true for request {Guid}", request.Guid);
                                }
                                else if (!await request.asyncWorker.Synchronize()) // Wait for the worker to finish setting up the blob's, etc...
                                {
                                    _logger.LogWarning("[ProxyToBackEnd:{Guid}] AsyncWorker synchronization failed - Error: {Error}", 
                                        request.Guid, request.asyncWorker.ErrorMessage);
                                    pr.Headers["x-Async-Error"] = request.asyncWorker.ErrorMessage;
                                    request.SBStatus = ServiceBusMessageStatusEnum.AsyncProcessingError;

                                    //_logger.LogError($"AsyncWorker failed to setup: {request.asyncWorker.ErrorMessage}");
                                }
                                else
                                {
                                    _logger.LogDebug("[ProxyToBackEnd:{Guid}] AsyncWorker synchronized successfully", request.Guid);
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

                                foreach (var header in proxyResponse.Headers.ToList())
                                {
                                    if (s_excludedHeaders.Contains(header.Key)) continue;
                                    requestAttempt[header.Key] = string.Join(", ", header.Value);
                                    //Console.WriteLine("requestAttempt[{0}] = {1}", header.Key, header.Value);
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

                                foreach (var header in proxyResponse.Headers.ToList())
                                {
                                    if (s_excludedHeaders.Contains(header.Key)) continue;
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
                            ProxyHelperUtils.CopyResponseHeaders(proxyResponse, pr);

                            pr.Headers["x-BackendHost"] = requestSummary["Backend-Host"] = pr.BackendHostname;
                            pr.Headers["x-Request-Queue-Duration"] = requestSummary["Request-Queue-Duration"] = request.Headers["x-Request-Queue-Duration"] ?? "N/A";
                            pr.Headers["x-Request-Process-Duration"] = requestSummary["Request-Process-Duration"] = request.Headers["x-Request-Process-Duration"] ?? "N/A";
                            pr.Headers["x-Total-Latency"] = requestSummary["Total-Latency"] = (DateTime.UtcNow - request.EnqueueTime).TotalMilliseconds.ToString("F3");

                            // Strip headers from pr.Headers that are not allowed in the response
                            foreach (var header in _options.StripResponseHeaders)
                            {
                                if (pr.Headers.Get(header) != null)
                                {
                                    pr.Headers.Remove(header);
                                }
                            }

                            // ASYNC: If the request was triggered asynchronously, we need to write the response to the async worker blob
                            // For background checks, skip header writing here - will be written in StreamResponseAsync if completed
                            // TODO: Move to caller to handle writing errors?
                            // Store the response stream in proxyData and return to parent caller
                            // WAS ASYNC SYNCHRONIZED?
                            if (request.AsyncTriggered)
                            {
                                // Skip header write for background checks - will be handled in StreamResponseAsync
                                if (!request.IsBackgroundCheck)
                                {
                                    _logger.LogDebug("[ProxyToBackEnd:{Guid}] Writing headers to AsyncWorker blob", request.Guid);
                                    // Write the headers to the async worker blob [ the client connection is closed ]
                                    if (!await request.asyncWorker!.WriteHeaders(proxyResponse.StatusCode, pr.Headers))
                                    {
                                        throw new ProxyErrorException(ProxyErrorException.ErrorType.AsyncWorkerError,
                                                                    HttpStatusCode.InternalServerError, "Failed to write headers to async worker");
                                    }
                                    _logger.LogDebug("[ProxyToBackEnd:{Guid}] Headers written successfully to AsyncWorker", request.Guid);
                                }
                                else
                                {
                                    _logger.LogDebug("[ProxyToBackEnd:{Guid}] Deferring header write for background check until completion status known", request.Guid);
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
                                requestState = $"Direct Mode Processor: {pr.StreamingProcessor}";
                            }
                            else
                            {
                                pr.StreamingProcessor = StreamProcessorFactory.DetermineStreamProcessor(proxyResponse, mediaType);
                                requestState = $"Stream Proxy Response : {pr.StreamingProcessor}";
                            }

                            _logger.LogDebug("[ProxyToBackEnd:{Guid}] Starting response streaming - Processor: {Processor}, MediaType: {MediaType}", 
                                request.Guid, pr.StreamingProcessor, mediaType);

                            // Stream response from backend to client/blob
                            try
                            {
                                await StreamResponseAsync(request, proxyResponse, pr.StreamingProcessor, mediaType).ConfigureAwait(false);
                                _logger.LogDebug("[ProxyToBackEnd:{Guid}] Response streaming completed", request.Guid);
                            }
                            catch (Exception e)
                            {
                                _logger.LogError(e, "[ProxyToBackEnd:{Guid}] Error streaming response to {FullURL}",
                                    request.Guid, request.FullURL);
                                throw;
                            }

                            try
                            {
                                _logger.LogDebug("[ProxyToBackEnd:{Guid}] Flushing output stream", request.Guid);
                                if (request.OutputStream != null)
                                {
                                    await request.OutputStream.FlushAsync().ConfigureAwait(false);

                                    if (request.OutputStream is BufferedStream bufferedStream)
                                    {
                                        await bufferedStream.FlushAsync().ConfigureAwait(false);
                                    }
                                }
                                _logger.LogDebug("[ProxyToBackEnd:{Guid}] Output stream flushed successfully", request.Guid);
                            }
                            catch (Exception e)
                            {
                                _logger.LogDebug(e, "[ProxyToBackEnd:{Guid}] Unable to flush output stream", request.Guid);
                            }

                            // Log the response if debugging is enabled
                            if (request.Debug)
                            {
                                _logger.LogDebug("Got: {StatusCode} {FullURL} {ContentLength} Body: {BodyLength} bytes",
                                    pr.StatusCode, pr.FullURL, pr.ContentHeaders["Content-Length"], pr?.Body?.Length);
                            }

                            successfulRequest = true;
                            _logger.LogDebug("[ProxyToBackEnd:{Guid}] Request completed successfully, returning ProxyData", request.Guid);
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

                if (e.Type == ProxyErrorException.ErrorType.TTLExpired)
                    break;

                continue;
            }
            catch (TaskCanceledException) when (_isEvictingAsyncRequest)
            {
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
                // 408 Request Timeout - consolidates both TaskCanceledException and OperationCanceledException
                PopulateTimeoutError(requestAttempt, request, proxyStartDate, ex is OperationCanceledException);
                continue;
            }
            catch (HttpRequestException e)
            {
                PopulateRequestAttemptError(requestAttempt, HttpStatusCode.BadRequest, 
                    $"Bad Request: {e.Message}", 
                    "Operation Exception: HttpRequest");
                continue;
            }
            catch (Exception e)
            {
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
            }
            finally
            {
                // Add the request attempt to the summary
                requestAttempt.Duration = DateTime.UtcNow - proxyStartDate;
                requestAttempt.SendEvent();  // Log the dependent request attempt

                hostIterator.RecordResult(host, successfulRequest);

                // Track host status for circuit breaker
                host.Config.TrackStatus(intCode, !successfulRequest);

                if (!successfulRequest)
                {
                    var miniDict = requestAttempt.ToDictionary(s_backendKeys);
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
        _logger.LogError("[ProxyToBackEnd:{Guid}] ⚠ ALL HOSTS EXHAUSTED - Attempts: {Attempts}, LastStatus: {StatusCode}", 
            request.Guid, request.BackendAttempts, lastStatusCode);

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

        // STREAM SERVER ERROR RESPONSE.  Must respond because the request was not successful
        try
        {
            // For async requests that triggered, write error to blob via AsyncWorker
            if (request.AsyncTriggered && request.asyncWorker != null)
            {
                _logger.LogInformation("Writing error response to AsyncWorker blob for request {Guid} - Status: {StatusCode}",
                    request.Guid, lastStatusCode);
                
                // Write error headers to blob
                var errorHeaders = new WebHeaderCollection
                {
                    ["x-Request-Queue-Duration"] = (request.DequeueTime - request.EnqueueTime).TotalMilliseconds.ToString("F3") + " ms",
                    ["x-Total-Latency"] = (DateTime.UtcNow - request.EnqueueTime).TotalMilliseconds.ToString("F3") + " ms",
                    ["x-ProxyHost"] = _options.HostName,
                    ["x-MID"] = request.MID,
                    ["Attempts"] = request.BackendAttempts.ToString()
                };
                
                await request.asyncWorker.WriteHeaders(lastStatusCode, errorHeaders);
                
                // Write error body to blob - use lazy stream creation for background checks
                if (request.IsBackgroundCheck)
                {
                    var outputStream = await request.asyncWorker.GetOrCreateDataStreamAsync();
                    await outputStream.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString())).ConfigureAwait(false);
                    await outputStream.FlushAsync().ConfigureAwait(false);
                }
                else if (request.OutputStream != null)
                {
                    await request.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString())).ConfigureAwait(false);
                    await request.OutputStream.FlushAsync().ConfigureAwait(false);
                }
            }
            // For synchronous requests or async that hasn't triggered, write to HTTP context
            else if (!request.AsyncTriggered && request.Context != null)
            {
                _logger.LogInformation("Response Status Code: {StatusCode} for request {Guid}",
                    lastStatusCode, request.Guid);
                request.Context.Response.StatusCode = (int)lastStatusCode;
                request.Context.Response.KeepAlive = false;
                
                request.Context.Response.Headers["x-Request-Queue-Duration"] = (request.DequeueTime - request.EnqueueTime).TotalMilliseconds.ToString("F3") + " ms";
                request.Context.Response.Headers["x-Total-Latency"] = (DateTime.UtcNow - request.EnqueueTime).TotalMilliseconds.ToString("F3") + " ms";
                request.Context.Response.Headers["x-ProxyHost"] = _options.HostName;
                request.Context.Response.Headers["x-MID"] = request.MID;
                request.Context.Response.Headers["Attempts"] = request.BackendAttempts.ToString();

                await request.Context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString())).ConfigureAwait(false);
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
            // If we can't write the response, we can only log it
            _logger.LogError(e, "Error writing error response for request {Guid} - AsyncTriggered: {AsyncTriggered}", 
                request.Guid, request.AsyncTriggered);
        }


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
    /// <param name="mediaType">The media type of the response content</param>
    private async Task StreamResponseAsync(RequestData request, HttpResponseMessage proxyResponse, string processWith, string mediaType)
    {
        ProxyEvent requestSummary = request.EventData;

        _logger.LogDebug("Streaming response with processor. Requested: {ProcessorRequested}, ContentType: {ContentType}, Guid: {Guid}",
            processWith, mediaType, request.Guid);

        IStreamProcessor processor = _streamProcessorFactory.GetStreamProcessor(processWith, out string resolvedProcessor);
        MemoryStream? memoryBuffer = null;
        
        try
        {
            _logger.LogDebug("Resolved processor: {ProcessorName} for request {Guid}", resolvedProcessor, request.Guid);

            // For background checks, buffer response in memory first, then conditionally write to blob
            if (request.IsBackgroundCheck && request.asyncWorker != null)
            {
                _logger.LogDebug("Buffering background check response to memory for request {Guid}", request.Guid);
                memoryBuffer = new MemoryStream();
                await processor.CopyToAsync(proxyResponse.Content, memoryBuffer).ConfigureAwait(false);
            }
            else if (request.runAsync && request.asyncWorker != null)
            {
                // For async requests, use lazy stream creation to handle both normal and rehydrated requests
                var outputStream = await request.asyncWorker.GetOrCreateDataStreamAsync().ConfigureAwait(false);
                _logger.LogDebug("Streaming to async blob for request {Guid}", request.Guid);
                await processor.CopyToAsync(proxyResponse.Content, outputStream).ConfigureAwait(false);
            }
            else if (request.OutputStream != null)
            {
                _logger.LogDebug("Streaming to client for request {Guid}", request.Guid);
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
                processor.GetStats(request.EventData, proxyResponse.Headers);
                
                // For background checks, only write to blob if completed or Debug mode
                if (request.IsBackgroundCheck && request.asyncWorker != null && memoryBuffer != null)
                {
                    if (processor.BackgroundCompleted || request.Debug)
                    {
                        _logger.LogDebug("Background check completed or Debug mode - writing headers and {Bytes} bytes to blob for request {Guid}", 
                            memoryBuffer.Length, request.Guid);
                        
                        // Copy headers from proxyResponse
                        var pr = new ProxyData();
                        ProxyHelperUtils.CopyResponseHeaders(proxyResponse, pr);
                        
                        // Write headers to blob
                        await request.asyncWorker.WriteHeaders(proxyResponse.StatusCode, pr.Headers);
                        
                        // Write buffered data to blob
                        var outputStream = await request.asyncWorker.GetOrCreateDataStreamAsync();
                        memoryBuffer.Position = 0;
                        await memoryBuffer.CopyToAsync(outputStream).ConfigureAwait(false);
                        await outputStream.FlushAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        _logger.LogDebug("Background check in progress - discarding {Bytes} bytes for request {Guid}", 
                            memoryBuffer.Length, request.Guid);
                    }
                }
                
                await _lifecycleManager.HandleBackgroundRequestLifecycle(request, processor).ConfigureAwait(false);
            }
            catch (Exception statsEx)
            {
                _logger.LogDebug(statsEx, "Background lifecycle management failed for request {Guid}", request.Guid);
            }
            finally
            {
                memoryBuffer?.Dispose();
                
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


    // cts is returned to the caller who disposes of it
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

            // ✅ Dispose old CTS before creating new one
            _asyncExpelSource?.Dispose();
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


}

