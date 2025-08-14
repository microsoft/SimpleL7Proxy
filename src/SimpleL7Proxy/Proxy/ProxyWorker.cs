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
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Queue;
using SimpleL7Proxy.User;
using SimpleL7Proxy.ServiceBus;
using SimpleL7Proxy.BlobStorage;
using Microsoft.Azure.Amqp.Framing;
using SimpleL7Proxy.StreamProcessor;

namespace SimpleL7Proxy.Proxy;

// The ProxyWorker class has the following main objectives:
// 1. Read incoming requests from the queue, prioritizing the highest priority requests.
// 2. Proxy the request to the backend with the lowest latency.
// 3. Retry against the next backend if the current backend fails.
// 4. Return a 502 Bad Gateway if all backends fail.
// 5. Return a 200 OK with backend server stats if the request is for /health.
// 6. Log telemetry data for each request.
public class ProxyWorker
{
    private int PreferredPriority;
    private readonly CancellationToken _cancellationToken;
    private static bool _debug = false;
    private static IConcurrentPriQueue<RequestData>? _requestsQueue;
    private readonly IBackendService _backends;
    private readonly BackendOptions _options;
    private readonly TelemetryClient? _telemetryClient;
    private readonly IEventClient _eventClient;
    private readonly IAsyncWorkerFactory _asyncWorkerFactory; // Just inject the factory
    private readonly ILogger<ProxyWorker> _logger;
    //private readonly ProxyStreamWriter _proxyStreamWriter;
    private IUserPriorityService _userPriority;
    private IUserProfileService _profiles;
    private readonly string _TimeoutHeaderName;
    private string IDstr = "";
    public static int activeWorkers = 0;
    private static bool readyToWork = false;
    private static Guid? LastHostGuid;
    static string[] backendKeys = new[] { "Backend-Host", "Host-URL", "Status", "Duration", "Error", "Message", "Request-Date" };

    private static int[] states = [0, 0, 0, 0, 0, 0, 0, 0];

    public static string GetState()
    {
        return $"Count: {activeWorkers} States: [ deq-{states[0]} pre-{states[1]} prxy-{states[2]} -[snd-{states[3]} rcv-{states[4]}]-  wr-{states[5]} rpt-{states[6]} cln-{states[7]} ]";
    }
    //public ProxyWorker(CancellationToken cancellationToken, int ID, int priority, ConcurrentPriQueue<RequestData> requestsQueue, BackendOptions backendOptions, IUserPriority? userPriority, IUserProfile? profiles, IBackendService? backends, IEventHubClient? eventHubClient, TelemetryClient? telemetryClient)
    public ProxyWorker(
        int ID,
        int priority,
        IConcurrentPriQueue<RequestData> requestsQueue,
        BackendOptions backendOptions,
        IBackendService? backends,
        IUserProfileService? profiles,
        IUserPriorityService? userPriority,
        IEventClient eventClient,
        TelemetryClient? telemetryClient,
        IAsyncWorkerFactory asyncWorkerFactory,
        ILogger<ProxyWorker> logger,
        ProxyStreamWriter proxyStreamWriter,
        CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        _requestsQueue = requestsQueue ?? throw new ArgumentNullException(nameof(requestsQueue));
        _backends = backends ?? throw new ArgumentNullException(nameof(backends));
        _eventClient = eventClient;
        _asyncWorkerFactory = asyncWorkerFactory;
        _logger = logger;
        //_proxyStreamWriter = proxyStreamWriter;
        //_eventHubClient = eventHubClient;
        _telemetryClient = telemetryClient;
        _userPriority = userPriority ?? throw new ArgumentNullException(nameof(userPriority));
        _options = backendOptions ?? throw new ArgumentNullException(nameof(backendOptions));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _TimeoutHeaderName = _options.TimeoutHeader;
        if (_options.Client == null) throw new ArgumentNullException(nameof(_options.Client));
        IDstr = ID.ToString();
        PreferredPriority = priority;
    }

    public async Task TaskRunner()
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
            Console.WriteLine("All workers ready to work");
        }

        // Stop the worker if the cancellation token is cancelled and there is no work to do
        while (!_cancellationToken.IsCancellationRequested || _requestsQueue.thrdSafeCount > 0)
        {
            RequestData incomingRequest;
            try
            {
                Interlocked.Increment(ref states[0]);
                workerState = "Waiting";

                // This will block until an item is available or the token is cancelled
                incomingRequest = await _requestsQueue.DequeueAsync(PreferredPriority).ConfigureAwait(false);
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

            if (!incomingRequest.Requeued)
            {
                incomingRequest.DequeueTime = DateTime.UtcNow;
            }
            incomingRequest.Requeued = false;  // reset this flag for this round of activity

            await using (incomingRequest)
            {
                var lcontext = incomingRequest.Context;
                bool isExpired = false;

                Interlocked.Increment(ref states[1]);
                workerState = "Processing";

                if (lcontext == null || incomingRequest == null)
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
                    if (Constants.probes.Contains(incomingRequest.Path))
                    {
                        ProbeResponse(incomingRequest.Path, out int probeStatus, out string probeMessage);

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
                    incomingRequest.SBStatus = ServiceBusMessageStatusEnum.Processing;

                    eventData.Type = EventType.ProxyRequest;
                    eventData["EnqueueTime"] = incomingRequest.EnqueueTime.ToString("o");
                    eventData["RequestContentLength"] = incomingRequest.Headers["Content-Length"] ?? "N/A";

                    incomingRequest.Headers["x-Request-Queue-Duration"] = (incomingRequest.DequeueTime! - incomingRequest.EnqueueTime!).TotalMilliseconds.ToString();
                    incomingRequest.Headers["x-Request-Process-Duration"] = (DateTime.UtcNow - incomingRequest.DequeueTime).TotalMilliseconds.ToString();
                    incomingRequest.Headers["x-Request-Worker"] = IDstr;
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
                        pr = await ProxyToBackEndAsync(incomingRequest, eventData).ConfigureAwait(false);
                    }
                    finally
                    {
                        eventData["Url"] = incomingRequest.FullURL;
                        var timeTaken = DateTime.UtcNow - incomingRequest.EnqueueTime;
                        eventData.Duration = timeTaken;
                        eventData["Total-Latency"] = timeTaken.TotalMilliseconds.ToString("F3");
                        eventData["Attempts"] = incomingRequest.Attempts.ToString();
                        if (pr != null)
                        {
                            eventData["Backend-Host"] = !string.IsNullOrEmpty(pr.BackendHostname) ? pr.BackendHostname : "N/A";
                            eventData["Response-Latency"] =
                                pr.ResponseDate != default && incomingRequest.DequeueTime != default
                                    ? (pr.ResponseDate - incomingRequest.DequeueTime).TotalMilliseconds.ToString("F3") : "N/A";
                            eventData["Average-Backend-Probe-Latency"] =
                                pr.CalculatedHostLatency != 0 ? pr.CalculatedHostLatency.ToString("F3") + " ms" : "N/A";
                            if (pr.Headers != null)
                                pr.Headers["x-Atthempts"] = incomingRequest.Attempts.ToString();
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
                        eventData.Type = EventType.ProxyRequestExpired;
                        incomingRequest.SBStatus = ServiceBusMessageStatusEnum.Expired;
                    }
                    else
                    {
                        // Set the appropriate status based on whether the request was async or sync
                        incomingRequest.SBStatus = incomingRequest.AsyncTriggered 
                            ? ServiceBusMessageStatusEnum.AsyncProcessed 
                            : ServiceBusMessageStatusEnum.Processed;
                    }


                    // SYNCHRONOUS MODE
                    //await WriteResponseAsync(lcontext, pr).ConfigureAwait(false);

                    //                    Task.Yield(); // Yield to the scheduler to allow other tasks to run
                    Interlocked.Decrement(ref states[5]);
                    Interlocked.Increment(ref states[6]);
                    workerState = "Finalize";

                    var conlen = pr.ContentHeaders?["Content-Length"] ?? "N/A";
                    var proxyTime = (DateTime.UtcNow - incomingRequest.DequeueTime).TotalMilliseconds.ToString("F3");
                    _logger.LogCritical("Pri: {Priority}, Stat: {StatusCode}, Len: {ContentLength}, {FullURL}, Deq: {DequeueTime}, Lat: {ProxyTime} ms", 
                        incomingRequest.Priority, (int)pr.StatusCode, conlen, pr.FullURL, 
                        incomingRequest.DequeueTime.ToLocalTime().ToString("HH:mm:ss"), proxyTime);

                    eventData["Content-Length"] = lcontext.Response?.ContentLength64.ToString() ?? "N/A";
                    eventData["Content-Type"] = lcontext?.Response?.ContentType ?? "N/A";

                    if (incomingRequest.incompleteRequests != null)
                    {
                        AddIncompleteRequestsToEventData(incomingRequest.incompleteRequests, eventData);
                    }

                    Interlocked.Decrement(ref states[6]);
                    Interlocked.Increment(ref states[7]);
                    workerState = "Cleanup";
                }
                catch (S7PRequeueException e)
                {

                    // Create a  new task that will requeue after the retry-after value
                    var requeueTask = Task.Run(async () =>
                    {
                        // we shouldn't need to create a copy of the incomingRequest because we are skipping the dispose.
                        // Requeue the request after the retry-after value
                        incomingRequest.SBStatus = ServiceBusMessageStatusEnum.RetryAfterDelay;

                        Interlocked.Decrement(ref states[7]);
                        await Task.Delay(e.RetryAfter).ConfigureAwait(false);
                        Interlocked.Increment(ref states[7]);


                        incomingRequest.SBStatus = ServiceBusMessageStatusEnum.ReQueued;
                        _requestsQueue.Requeue(incomingRequest, incomingRequest.Priority, incomingRequest.Priority2, incomingRequest.EnqueueTime);
                    });

                    // Event details were  already captured in ReadProxyAsync

                    // Requeue the request 
                    //_requestsQueue.Requeue(incomingRequest, incomingRequest.Priority, incomingRequest.Priority2, incomingRequest.EnqueueTime);
                    _logger.LogCritical("Requeued request, Pri: {Priority}, Expires-At: {ExpiresAt} Retry-after-ms: {RetryAfter}, Q-Len: {QueueLength}, CB: {CircuitBreakerStatus}, Hosts: {ActiveHosts}", 
                        incomingRequest.Priority, incomingRequest.ExpiresAtString, e.RetryAfter, _requestsQueue.thrdSafeCount, _backends.CheckFailedStatus(), _backends.ActiveHostCount());
                    incomingRequest.Requeued = true;
                    incomingRequest.SkipDispose = true;

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

                    try
                    {
                        lcontext.Response.StatusCode = (int)e.StatusCode;
                        await lcontext.Response.OutputStream.WriteAsync(
                            errorMessage,
                            0,
                            errorMessage.Length).ConfigureAwait(false);

                        _logger.LogWarning("Proxy error: {Message}", e.Message);
                    }
                    catch (Exception writeEx)
                    {
                        _logger.LogError($"Failed to write error message: {writeEx.Message}");

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

                        try
                        {
                            lcontext.Response.StatusCode = (int)eventData.Status;
                            var errorBytes = Encoding.UTF8.GetBytes(errorMessage);
                            await lcontext.Response.OutputStream.WriteAsync(
                                errorBytes,
                                0,
                                errorBytes.Length).ConfigureAwait(false);
                            _logger.LogError($"An IO exception occurred: {ioEx.Message}");
                        }
                        catch (Exception writeEx)
                        {
                            _logger.LogError($"Failed to write error message: {writeEx.Message}");
                            eventData["InnerErrorDetail"] = "Network Error";
                            if (writeEx.StackTrace != null)
                            {
                                eventData["InnerErrorStack"] = writeEx.StackTrace.ToString();
                            }
                        }
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
                        _logger.LogError("GOT AN ERROR: {Message}", ex.Message);
                        eventData.Status = HttpStatusCode.InternalServerError; // 500 Internal Server Error
                        eventData.Type = EventType.Exception;
                        eventData.Exception = ex;
                        eventData["WorkerState"] = workerState;

                        if (ex.Message == "Cannot access a disposed object." || ex.Message.StartsWith("Unable to write data") || ex.Message.Contains("Broken Pipe")) // The client likely closed the connection
                        {
                            _logger.LogInformation("Client closed connection: {FullURL}", incomingRequest.FullURL);
                            eventData["InnerErrorDetail"] = "Client Disconnected";
                        }
                        else
                        {
                            requestException = true;

                            // Set an appropriate status code for the error
                            var errorMessage = "Exception: " + ex.Message;
                            eventData["ErrorDetails"] = errorMessage;

                            try
                            {
                                // _telemetryClient?.TrackException(ex, eventData);
                                lcontext.Response.StatusCode = 500;
                                var errorBytes = Encoding.UTF8.GetBytes(errorMessage);
                                await lcontext.Response.OutputStream.WriteAsync(
                                    errorBytes,
                                    0,
                                    errorBytes.Length).ConfigureAwait(false);
                            }
                            catch (Exception writeEx)
                            {
                                eventData["InnerErrorDetail"] = "Network Error";
                                if (writeEx.StackTrace != null)
                                {
                                    eventData["InnerErrorStack"] = writeEx.StackTrace.ToString();
                                }
                            }
                        }
                    }
                }
                finally
                {
                    try
                    {
                        // Don't track the request yet if it was retried.
                        if (!incomingRequest.Requeued)
                        {
                            if (workerState != "Cleanup")
                                eventData["WorkerState"] = workerState;

                            eventData.SendEvent(); // Ensure the event at the completion of the request
                            if (doUserconfig)
                                _userPriority.removeRequest(incomingRequest.UserID, incomingRequest.Guid);

                            // Track the status of the request for circuit breaker
                            _backends.TrackStatus((int)lcontext.Response.StatusCode, requestException);

                            try
                            {
                                incomingRequest.Dispose(); // Dispose of the request data
                            }
                            catch (Exception disposeEx)
                            {
                                _logger.LogError($"Failed to dispose of request data: {disposeEx.Message}");
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
                        _logger.LogError($"Error in finally: {e.Message}");
                    }

                    Interlocked.Decrement(ref states[7]);
                    workerState = "Exit - Finally";
                }
            }   // lifespan of incomingRequest
        }       // while running loop

        Interlocked.Decrement(ref activeWorkers);

        _logger.LogDebug("Worker {IDstr} stopped.", IDstr);
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

    // Returns probe responses
    //
    // /startup - 200  if all workers started, there is at least 1 active host ... runs at fastest priority
    // /readiness - same as /startup
    // /liveness - 503 if no active hosts  or  there are many recent errors ... runs on high priority
    // /health - details on all active hosts ... runs on high priority

    private void ProbeResponse(string path, out int probeStatus, out string probeMessage)
    {
        probeStatus = 200;
        probeMessage = "OK\n";

        // Cache these to avoid repeatedly calling the same methods
        int hostCount = _backends.ActiveHostCount();
        bool hasFailedHosts = _backends.CheckFailedStatus();

        switch (path)
        {
            case Constants.Health:
                if (hostCount == 0 || hasFailedHosts)
                {
                    probeStatus = 503;
                    probeMessage = $"Not Healthy.  Active Hosts: {hostCount} Failed Hosts: {hasFailedHosts}\n";
                }
                else
                {
                    var hosts = _backends.GetHosts();
                    probeMessage = $"Replica: {_options.HostName} {"".PadRight(30)} SimpleL7Proxy: {Constants.VERSION}\nBackend Hosts:\n  Active Hosts: {hostCount}  -  {(hasFailedHosts ? "FAILED HOSTS" : "All Hosts Operational")}\n";
                    if (hosts.Count > 0)
                    {
                        foreach (var host in hosts)
                        {
                            probeMessage += $" Name: {host.Host}  Status: {host.GetStatus(out int calls, out int errorCalls, out double average)}\n";
                        }
                    }
                    else
                    {
                        probeMessage += "No Hosts\n";
                    }
                }

                var stats = $"Worker Statistics:\n {GetState()}\n";
                var priority = $"User Priority Queue: {_userPriority?.GetState() ?? "N/A"}\n";
                var requestQueue = $"Request Queue: {_requestsQueue?.thrdSafeCount.ToString() ?? "N/A"}\n";
                var events = $"Event Hub: {(_eventClient != null ? $"Enabled  -  {_eventClient.Count} Items" : "Disabled")}\n";
                probeMessage += stats + priority + requestQueue + events;
                break;

            case Constants.Readiness:
            case Constants.Startup:
                if (!readyToWork || hostCount == 0)
                {
                    probeStatus = 503;
                    probeMessage = "Not Ready .. hostCount = " + hostCount + " readyToWork = " + readyToWork;
                }
                break;

            case Constants.Liveness:
                if (hostCount == 0)
                {
                    probeStatus = 503;
                    probeMessage = $"Not Lively.  Active Hosts: {hostCount} Failed Hosts: {hasFailedHosts}";
                }
                break;

            case Constants.Shutdown:
                // Shutdown is a signal to unwedge workers and shut down gracefully
                break;
        }
    }
    // // Method to replace or add a header
    // void ReplaceOrAddHeader(WebHeaderCollection headers, string headerName, string headerValue)
    // {
    //     if (headers[headerName] != null)
    //     {
    //         headers.Remove(headerName);
    //     }
    //     headers.Add(headerName, headerValue);
    // }

    // private async Task WriteDataToStreamAsync(HttpListenerContext context,
    //                                           ProxyData pr,
    //                                           CancellationToken token)
    // {
    //     HttpListenerResponseWrapper listener = new(context.Response);
    //     await _proxyStreamWriter.WriteDataToStreamAsync(listener, pr, token);

    // }

    //DateTime requestDate, string method, string path, WebHeaderCollection headers, Stream body)//HttpListenerResponse downStreamResponse)
    public async Task<ProxyData> ProxyToBackEndAsync(RequestData request, ProxyEvent eventData)
    {
        if (request == null) throw new ArgumentNullException(nameof(request), "Request cannot be null.");
        if (request.Body == null) throw new ArgumentNullException(nameof(request.Body), "Request body cannot be null.");
        if (request.Headers == null) throw new ArgumentNullException(nameof(request.Headers), "Request headers cannot be null.");
        if (request.Method == null) throw new ArgumentNullException(nameof(request.Method), "Request method cannot be null.");

        // Use the current active hosts
        var activeHosts = _backends.GetActiveHosts();
        List<Dictionary<string, string>> incompleteRequests = request.incompleteRequests;

        request.Debug = _debug || (request.Headers["S7PDEBUG"] != null && string.Equals(request.Headers["S7PDEBUG"], "true", StringComparison.OrdinalIgnoreCase));
        HttpStatusCode lastStatusCode = HttpStatusCode.ServiceUnavailable;
        var requestSummary = request.EventData;

        // Read the body stream once and reuse it
        //byte[] bodyBytes = await request.CachBodyAsync().ConfigureAwait(false);
        List<S7PRequeueException> retryAfter = new();

        if (_options.UseOAuth)
        {
            // Get a token
            var OAToken = _backends.OAuth2Token();
            if (request.Debug)
            {
                _logger.LogDebug("Token: " + OAToken);
            }
            // Set the token in the headers
            request.Headers.Set("Authorization", $"Bearer {OAToken}");
        }

        // Round-robin logic: if the last used host is known, start after it
        if (_options.LoadBalanceMode == Constants.RoundRobin)
        {
            // Round-robin mode: start after the last used host
            if (LastHostGuid != null)
            {

                // Console.WriteLine($"Last Round Robin Host: {LastHostGuid}");
                int lastIndex = activeHosts.FindIndex(h => h.guid == LastHostGuid);
                if (lastIndex >= 0)
                {
                    // Rotate the list to start after the last used host
                    activeHosts = activeHosts.Skip(lastIndex + 1).Concat(activeHosts.Take(lastIndex + 1)).ToList();
                }
            }
        }


        foreach (var host in activeHosts)
        {
            DateTime ProxyStartDate = DateTime.UtcNow;
            LastHostGuid = host.guid;
            // track the number of attempts
            request.Attempts++;
            bool successfulRequest = false;
            string requestState = "Init";

            ProxyEvent requestAttempt = new(request.EventData)
            {
                Type = EventType.BackendRequest,
                ParentId = request.ParentId,
                MID = $"{request.MID}-{request.Attempts}",
                Method = request.Method,
                Uri = request.Context!.Request.Url!,
                ["Request-Date"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffK"),
                ["Backend-Host"] = host.Host,
                ["Host-URL"] = host.Url,
                ["Attempt"] = request.Attempts.ToString()
            };

            // Try the request on each active host, stop if it worked
            try
            {
                requestState = "Calc ExpiresAt";

                // Check ExpiresAt against current time .. keep in mind, client may have disconnected already
                if (request.ExpiresAt < DateTimeOffset.UtcNow)
                {
                    string errorMessage = $"Request has expired: Time: {DateTime.Now}  Reason: {request.ExpireReason}";
                    requestSummary.Type = EventType.ProxyRequestExpired;
                    request.SkipDispose = false;
                    throw new ProxyErrorException(ProxyErrorException.ErrorType.TTLExpired,
                                                HttpStatusCode.PreconditionFailed,
                                                errorMessage);
                }


                var minDate = DateTime.Compare(request.ExpiresAt, DateTime.UtcNow.AddMilliseconds(request.defaultTimeout)) < 0
                    ? request.ExpiresAt
                    : DateTime.UtcNow.AddMilliseconds(request.defaultTimeout);
                request.Timeout = (int)(minDate - DateTime.UtcNow).TotalMilliseconds;

                request.Headers.Set("Host", host.Host);
                var urlWithPath = new UriBuilder(host.Url) { Path = request.Path }.Uri.AbsoluteUri;
                request.FullURL = System.Net.WebUtility.UrlDecode(urlWithPath);

                requestState = "Cache Body";
                // Read the body stream once and reuse it
                byte[] bodyBytes = await request.CacheBodyAsync().ConfigureAwait(false);

                requestState = "Create Backend Request";
                using (ByteArrayContent bodyContent = new(bodyBytes))
                using (HttpRequestMessage proxyRequest = new(new(request.Method), request.FullURL))
                {
                    proxyRequest.Content = bodyContent;
                    CopyHeaders(request.Headers, proxyRequest, true);

                    if (bodyBytes.Length > 0)
                    {

                        proxyRequest.Content.Headers.ContentLength = bodyBytes.Length;

                        // Preserve the content type if it was provided
                        var contentType = request.Context?.Request.ContentType ?? "application/octet-stream";
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
                            mediaTypeHeaderValue = new MediaTypeHeaderValue("application/octet-stream") { CharSet = "utf-8" };
                            _logger.LogInformation("Invalid charset provided, defaulting to utf-8: {Message}", e.Message);
                        }

                        proxyRequest.Content.Headers.ContentType = mediaTypeHeaderValue;
                    }

                    proxyRequest.Headers.ConnectionClose = true;

                    // Log request headers if debugging is enabled
                    if (request.Debug)
                    {
                        _logger.LogDebug($"> {request.Method} {request.FullURL} {bodyBytes.Length} bytes");
                        LogHeaders(proxyRequest.Headers, ">");
                        LogHeaders(proxyRequest.Content.Headers, "  >");
                        //string bodyString = System.Text.Encoding.UTF8.GetString(bodyBytes);
                        //Console.WriteLine($"Body Content: {bodyString}");
                    }

                    // Send the request and get the response
                    ProxyStartDate = DateTime.UtcNow;
                    Interlocked.Increment(ref states[3]);
                    try
                    {
                        requestState = "Make Backend Request";

                        // ASYNC: Calculate the timeout, start async worker
                        double rTimeout = request.Timeout;

                        // determine if request will run async or sync
                        if (request.runAsync && request.asyncWorker is null)
                        {
                            rTimeout = _options.AsyncTimeout;
                            request.asyncWorker = _asyncWorkerFactory.CreateAsync(request, _options.AsyncTriggerTimeout);
                            _ = request.asyncWorker.StartAsync();   // don't await this, let it run in parallel
                        }

                        // SEND THE REQUEST TO THE BACKEND USING THE APROPRIATE TIMEOUT
                        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(rTimeout));
                        using var proxyResponse = await _options.Client!.SendAsync(
                            proxyRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

                        // ASYNC: We got a response back, Synchronize with the asyncWorker 
                        if ((int)proxyResponse.StatusCode == 200 && request.runAsync)
                        {
                            if (request.asyncWorker == null)
                            {
                                _logger.LogError("AsyncWorker is null, but runAsync is true");
                            }
                            else if (!await request.asyncWorker.Synchronize()) // Wait for the worker to finish setting up the blob's, etc...
                            {
                                _logger.LogError($"AsyncWorker failed to setup: {request.asyncWorker.ErrorMessage}");
                            }
                        }

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


                        // Check if the status code of the response is in the set of allowed status codes, else try the next host
                        var intCode = (int)proxyResponse.StatusCode;
                        if ((intCode > 300 && intCode < 400) || intCode == 412 || intCode >= 500)
                        {
                            requestState = "Call unsuccessful";

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
                                    _logger.LogError("Error reading from backend host: {Message}", e.Message);
                                }

                                _logger.LogDebug("Trying next host: Response: {StatusCode}", proxyResponse.StatusCode);
                            }

                            // The request did not succeed, try the next host
                            continue;
                        }

                        host.AddPxLatency((responseDate - ProxyStartDate).TotalMilliseconds);
                        bodyBytes = [];

                        Interlocked.Increment(ref states[4]);

                        // Read the response
                        //await GetProxyResponseAsync(proxyResponse, request, pr).ConfigureAwait(false);

                        pr.Headers["x-BackendHost"] = requestSummary["Backend-Host"] = pr.BackendHostname;
                        pr.Headers["x-Request-Queue-Duration"] = requestSummary["Request-Queue-Duration"] = request.Headers["x-Request-Queue-Duration"] ?? "N/A";
                        pr.Headers["x-Request-Process-Duration"] = requestSummary["Request-Process-Duration"] = request.Headers["x-Request-Process-Duration"] ?? "N/A";
                        pr.Headers["x-Total-Latency"] = requestSummary["Total-Latency"] = (DateTime.UtcNow - request.EnqueueTime).TotalMilliseconds.ToString("F3");

                        Interlocked.Decrement(ref states[4]);

                        if ((int)proxyResponse.StatusCode == 429 && proxyResponse.Headers.TryGetValues("S7PREQUEUE", out var values))
                        {
                            // Requeue the request if the response is a 429 and the S7PREQUEUE header is set
                            // It's possible that the next host processes this request successfully, in which case these will get ignored
                            var s7PrequeueValue = values.FirstOrDefault();

                            if (s7PrequeueValue != null && string.Equals(s7PrequeueValue, "true", StringComparison.OrdinalIgnoreCase))
                            {
                                // we're keep track of the retry after values for later.
                                proxyResponse.Headers.TryGetValues("retry-after-ms", out var retryAfterValues);
                                if (retryAfterValues != null && int.TryParse(retryAfterValues.FirstOrDefault(), out var retryAfterValue))
                                {
                                    throw new S7PRequeueException("Requeue request", pr, retryAfterValue);
                                }

                                throw new S7PRequeueException("Requeue request", pr, 1000);
                            }
                        }
                        else
                        {
                            // request was successful, so we can disable the skip
                            request.SkipDispose = false;
                        }

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

                        var processWith = "Inline";
                        if ((int)proxyResponse.StatusCode == 200 && proxyResponse.Headers.TryGetValues("TOKENPROCESSOR", out var processorValues))
                        {
                            var tokenProcessor = processorValues.FirstOrDefault();
                            if (!string.IsNullOrEmpty(tokenProcessor))
                            {
                                // Use the token processor for further processing
                                processWith = tokenProcessor;
                            }
                        }

                        requestState = "Stream Proxy Response : " + processWith;

                        // Stream response from the backend to the client / blob depending on async timer
                        IStreamProcessor processor = new NullStreamProcessor();
                        try
                        {
                            if (processWith == "Inline")
                            {
                                await proxyResponse.Content.CopyToAsync(request.OutputStream).ConfigureAwait(false);
                            }
                            else
                            {
                                var processorType = Type.GetType($"SimpleL7Proxy.StreamProcessor.{processWith}Processor");
                                if (processorType != null)
                                {
                                    processor = Activator.CreateInstance(processorType) as IStreamProcessor ?? new NullStreamProcessor();
                                }

                                await processor.CopyToAsync(proxyResponse.Content, request.OutputStream, null).ConfigureAwait(false);

                            }
                            // This will write to either the client or the blob depending on the async timer
                        }
                        catch (IOException e)
                        {
                            _logger.LogError("IO Error streaming response: {Message}", e.Message);

                        }
                        catch (Exception e)
                        {
                            if (e.InnerException is IOException ioex)
                            {
                                // This is likely a client disconnect, we can ignore it.
                                // We've already returned a status and streamed the response.  Best to continue.
                                _logger.LogDebug("Client disconnected while streaming response: {Message}", ioex.Message);
                            }
                            else
                            {
                                _logger.LogError("Error streaming response: {InnerException}", e.InnerException);
                                throw new ProxyErrorException(ProxyErrorException.ErrorType.ClientDisconnected,
                                                              HttpStatusCode.InternalServerError, e.Message);
                            }
                        }
                        finally
                        {
                            processor.GetStats(eventData, proxyResponse.Headers);
                        }

                        try
                        {
                            _logger.LogDebug("Flushing output stream for {FullURL}", request.FullURL);
                            await request.OutputStream.FlushAsync().ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            _logger.LogDebug("Unable to flush output stream: {Exception}", e);
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

                continue;
            }
            catch (TaskCanceledException)
            {
                // 408 Request Timeout

                requestAttempt.Status = HttpStatusCode.RequestTimeout;
                requestAttempt["Expires-At"] = request.ExpiresAt.ToString("o");
                requestAttempt["MaxTimeout"] = _options.Timeout.ToString();
                requestAttempt["Request-Date"] = ProxyStartDate.ToString("o");
                requestAttempt["Request-Timeout"] = request.Timeout.ToString() + " ms";
                requestAttempt["Error"] = "Request Timed out";
                requestAttempt["Message"] = "Operation TIMEOUT";

                continue;
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
                _logger.LogError($"Error: {e.StackTrace}");
                _logger.LogError($"Error: {e.Message}");
                //lastStatusCode = HandleProxyRequestError(host, e, request.Timestamp, request.FullURL, HttpStatusCode.InternalServerError);

                requestAttempt.Status = HttpStatusCode.InternalServerError;
                requestAttempt["Error"] = "Internal Error: " + e.Message;
            }
            finally
            {
                // Add the request attempt to the summary
                requestAttempt.Duration = DateTime.UtcNow - ProxyStartDate;
                requestAttempt.SendEvent();  // Log the dependent request attempt

                if (!successfulRequest)
                {
                    var miniDict = requestAttempt.ToDictionary(backendKeys);
                    miniDict["State"] = requestState;
                    incompleteRequests.Add(miniDict);

                    var str = JsonSerializer.Serialize(miniDict);
                    _logger.LogDebug(str);
                }
            }
        }



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
        requestSummary.Type = EventType.ProxyError;

        // STREAM SERVER ERROR RESPONSE.  Must respond because the request was not successful
        try
        {
            request.Context!.Response.StatusCode = (int)lastStatusCode;
            request.Context.Response.KeepAlive = false;
            request.Context.Response.Headers["x-Request-Queue-Duration"] = (request.DequeueTime - request.EnqueueTime).TotalMilliseconds.ToString("F3") + " ms";
            request.Context.Response.Headers["x-Total-Latency"] = (DateTime.UtcNow - request.EnqueueTime).TotalMilliseconds.ToString("F3") + " ms";
            request.Context.Response.Headers["x-ProxyHost"] = _options.HostName;
            request.Context.Response.Headers["x-MID"] = request.MID;
            request.Context.Response.Headers["Attempts"] = request.Attempts.ToString();

            await request.OutputStream.WriteAsync(
                Encoding.UTF8.GetBytes(sb.ToString()),
                0,
                sb.Length).ConfigureAwait(false);
            await request.OutputStream.FlushAsync().ConfigureAwait(false);

        }
        catch (Exception e)
        {
            // If we can't write the response, we can only log it
            _logger.LogError("Error writing response: {Message}", e.Message);
        }


        return new ProxyData
        {
            FullURL = request.FullURL,

            CalculatedHostLatency = (DateTime.UtcNow - request.EnqueueTime).TotalMilliseconds,
            BackendHostname = "No Active Hosts Available",
            ResponseDate = DateTime.UtcNow,
            StatusCode = HandleProxyRequestError(null, requestSummary, lastStatusCode, "No active hosts were able to handle the request", incompleteRequests),
            Body = Encoding.UTF8.GetBytes(sb.ToString())
        };
    }

    private static void GenerateErrorMessage(List<Dictionary<string, string>> incompleteRequests, out StringBuilder sb, out bool statusMatches, out int currentStatusCode)
    {
        sb = new StringBuilder();
        sb.AppendLine("Error processing request.  No active hosts were able to handle the request.");
        sb.AppendLine("Request Summary:");
        statusMatches = true;
        currentStatusCode = -1;

        var statusCodes = new List<int>();

        foreach (var requestAttempt in incompleteRequests)
        {
            if (requestAttempt.TryGetValue("Status", out var statusStr) && int.TryParse(statusStr, out var status))
            {
                statusCodes.Add(status);
            }

            var sb2 = new StringBuilder();
            foreach (var key in requestAttempt.Keys)
            {
                sb2.Append($"{key}: {requestAttempt[key]} ");
            }
            sb.AppendLine(sb2.ToString());
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

    private void LogHeaders(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers, string prefix)
    {
        foreach (var header in headers)
        {
            _logger.LogDebug("{Prefix} {HeaderKey} : {HeaderValues}", prefix, header.Key, string.Join(", ", header.Value));
        }
    }
    private HttpStatusCode HandleProxyRequestError(
        BackendHostHealth? host,
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

        host?.AddError();
        return statusCode;
    }

}