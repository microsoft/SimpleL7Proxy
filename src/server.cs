using System.Net;
using System.Text.Json;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

// This class represents a server that listens for HTTP requests and processes them.
// It uses a priority queue to manage incoming requests and supports telemetry for monitoring.
// If the incoming request has the S7PPriorityKey header, it will be assigned a priority based the S7PPriority header.
public class Server : IServer
{
    private IBackendOptions? _options;
    private readonly TelemetryClient? _telemetryClient; // Add this line
    private HttpListener httpListener;

    private IBackendService _backends;

    private IUserPriority _userPriority;
    private IUserProfile _userProfile;
    private CancellationToken _cancellationToken;
    private ConcurrentPriQueue<RequestData> _requestsQueue = new ConcurrentPriQueue<RequestData>();
    private readonly IEventHubClient? _eventHubClient;
    private static bool _isShuttingDown = false;
    private readonly string _priorityHeaderName;
    private static ProxyEvent _staticEvent = new ProxyEvent();
    private readonly ProbeServer _probeServer;
    private readonly Task _probeServerTask;

    // public void enqueueShutdownRequest() {
    //     var shutdownRequest = new RequestData(Constants.Shutdown);
    //     _requestsQueue.Enqueue(shutdownRequest, 3, 0, DateTime.UtcNow, true);
    // }

    // Constructor to initialize the server with backend options and telemetry client.
    public Server(IOptions<BackendOptions> backendOptions, IUserPriority userPriority, IUserProfile userProfile, IEventHubClient? eventHubClient, IBackendService backends, TelemetryClient? telemetryClient)
    {
        if (backendOptions == null) throw new ArgumentNullException(nameof(backendOptions));
        if (backendOptions.Value == null) throw new ArgumentNullException(nameof(backendOptions.Value));

        _options = backendOptions.Value;
        _backends = backends;
        _eventHubClient = eventHubClient;
        _telemetryClient = telemetryClient;
        _requestsQueue.MaxQueueLength = _options.MaxQueueLength;
        _userPriority = userPriority;
        _userProfile = userProfile;
        _priorityHeaderName = _options.PriorityKeyHeader;

        var _listeningUrl = $"http://+:{_options.Port}/";

        httpListener = new HttpListener();
        httpListener.Prefixes.Add(_listeningUrl);

        var timeoutTime = TimeSpan.FromMilliseconds(_options.Timeout).ToString(@"hh\:mm\:ss\.fff");
        _staticEvent.WriteOutput($"Server configuration:  Port: {_options.Port} Timeout: {timeoutTime} Workers: {_options.Workers}");
        _probeServer = new ProbeServer(ProxyWorker.GetStatus );

        _probeServerTask = _probeServer.StartAsync(_cancellationToken);
    }

    public ConcurrentPriQueue<RequestData> Queue()
    {
        return _requestsQueue;
    }

    // Method to start the server and begin processing requests.
    public ConcurrentPriQueue<RequestData> Start(CancellationToken cancellationToken)
    {
        try
        {
            _cancellationToken = cancellationToken;
            httpListener.Start();
            _staticEvent.WriteOutput($"Listening on {_options?.Port}");
            // Additional setup or async start operations can be performed here

            return _requestsQueue;
        }
        catch (HttpListenerException ex)
        {
            // Handle specific errors, e.g., port already in use
            _staticEvent.WriteOutput($"Failed to start HttpListener: {ex.Message}");
            // Consider rethrowing, logging the error, or handling it as needed
            throw new Exception("Failed to start the server due to an HttpListener exception.", ex);
        }
        catch (Exception ex)
        {
            // Handle other potential errors
            _staticEvent.WriteErrorOutput($"An error occurred: {ex.Message}");
            throw new Exception("An error occurred while starting the server.", ex);
        }
    }

    // Continuously listens for incoming HTTP requests and processes them.
    // Requests are enqueued with a priority based on specific headers.
    // The method runs until a cancellation is requested.
    // Each request is enqueued with a priority into BlockingPriorityQueue.
    public async Task Run()
    {
        if (_options == null) throw new ArgumentNullException(nameof(_options));

        long counter = 0;
        int livenessPriority = _options.PriorityValues.Min();
        bool doUserProfile = _options.UseProfiles;

        while (!_cancellationToken.IsCancellationRequested)
        {
            ProxyEvent ed = null!;

            using var operation = _telemetryClient.StartOperation<RequestTelemetry>("IncomingRequest");
            try
            {
                // Use the CancellationToken to asynchronously wait for an HTTP request.
                var getContextTask = httpListener.GetContextAsync();

                // call GetContextAsync in a way that it can be cancelled
                var completedTask = await Task.WhenAny(getContextTask, Task.Delay(Timeout.Infinite, _cancellationToken)).ConfigureAwait(false);

                //  control to allow other tasks to run .. doesn't make sense here
                // await Task.Yield();

                // Cancel the delay task immedietly if the getContextTask completes first
                if (completedTask == getContextTask)
                {
                    int priority = _options.DefaultPriority;
                    int userPriorityBoost = 0;
                    var notEnqued = false;
                    int notEnquedCode = 0;
                    var retrymsg = "";
                    var logmsg = "";

                    Interlocked.Increment(ref counter);
                    var requestId = _options.IDStr + counter.ToString();

                    //delayCts.Cancel();
                    var lc = await getContextTask.ConfigureAwait(false);
                    if ( lc == null || lc.Request == null )
                    {
                        continue;
                    }

                    // if it's a probe, then bypass all the below checks and enqueue the request 
                    if (Constants.probes.Contains(lc.Request.Url?.PathAndQuery))
                    {
                        // Get ProbeData from pool using modulo rotation
                        var probePath = lc!.Request.Url!.PathAndQuery;

                        var fallthrough = false;

                        // Fast-path for probes to avoid queue and worker latency
                        switch (probePath)
                        {
                            case Constants.Liveness:
                                await _probeServer.LivenessResponseAsync(lc);
                                break;
                            case Constants.Readiness:
                                await _probeServer.ReadinessResponseAsync(lc);
                                break;
                            case Constants.Startup:
                                await _probeServer.StartupResponseAsync(lc);
                                break;
                            default:
                                fallthrough = true;
                                break;

                        }

                        if (!fallthrough)
                            continue;
                    }

                    var rd = new RequestData(lc, requestId);
                    ed = rd.EventData;
                    ed["Date"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                    ed.Uri = rd.Context!.Request.Url!;
                    ed.Method = rd.Method ?? "N/A";

                    ed["Path"] = rd.Path ?? "N/A";
                    ed["RequestHost"] = rd.Headers["Host"] ?? "N/A";
                    ed["RequestUserAgent"] = rd.Headers["User-Agent"] ?? "N/A";
                    // readiness probes:

                    // if it's a probe, then bypass all the below checks and enqueue the request 
                    if (Constants.probes.Contains(rd.Path))
                    {

                        // /startup runs a priority of 0,   otherwise run at highest priority ( lower is more urgent )
                        priority = 0;//(rd.Path == Constants.Liveness || rd.Path == Constants.Health) ? livenessPriority : 0;

                        // bypass all the below checks and enqueue the request
                        _requestsQueue.Enqueue(rd, priority, userPriorityBoost, rd.EnqueueTime, true);
                        continue;
                    }

                    if (!_isShuttingDown)
                    {
                        try
                        {
                            rd.Debug = rd.Headers["S7PDEBUG"] != null && string.Equals(rd.Headers["S7PDEBUG"], "true", StringComparison.OrdinalIgnoreCase);


                            if (_options.ValidateAuthAppID)
                            {
                                string? authAppID = rd.Headers[_options.ValidateAuthAppIDHeader];
                                if (!string.IsNullOrEmpty(authAppID) && _userProfile.IsAuthAppIDValid(authAppID))
                                {
                                    if (rd.Debug)
                                        Console.WriteLine($"AuthAppID {rd.Headers[_options.ValidateAuthAppIDHeader]} is valid.");
                                }
                                else
                                {
                                    if (rd.Debug)
                                        Console.WriteLine($"AuthAppID {rd.Headers[_options.ValidateAuthAppIDHeader]} is invalid.");

                                    throw new ProxyErrorException(
                                        ProxyErrorException.ErrorType.DisallowedAppID,
                                        HttpStatusCode.Forbidden,
                                        "Invalid AuthAppID: " + rd.Headers[_options.ValidateAuthAppIDHeader] + "\n"
                                    );
                                }
                            }

                            // Remove any disallowed headers
                            foreach (var header in _options.DisallowedHeaders)
                            {
                                if (rd.Debug && !String.IsNullOrEmpty(rd.Headers.Get(header)))
                                    Console.WriteLine($"Disallowed header {header} removed from request.");
                                rd.Headers.Remove(header);
                            }

                            rd.UserID = "";

                            // Lookup the user profile and add the headers to the request
                            if (doUserProfile)
                            {
                                var requestUser = rd.Headers[_options.UserProfileHeader];
                                if (!string.IsNullOrEmpty(requestUser))
                                {
                                    (var headers, var isSoftDeleted) = _userProfile.GetUserProfile(requestUser);

                                    if (headers != null && headers.Count > 0)
                                    {
                                        foreach (var header in headers)
                                        {
                                            if (!header.Key.StartsWith("internal-"))
                                            {
                                                rd.Headers.Set(header.Key, header.Value);
                                                if (rd.Debug)
                                                    Console.WriteLine($"Add Header: {header.Key} = {header.Value}");
                                            }
                                        }
                                    }
                                    else
                                    {
                                        if (rd.Debug)
                                            Console.WriteLine($"User profile for {requestUser} not found.");
                                        throw new ProxyErrorException(
                                            ProxyErrorException.ErrorType.UnknownProfile,
                                            HttpStatusCode.Forbidden,
                                            "User profile not found: " + requestUser + "\n"
                                        );
                                    }
                                    
                                    if (isSoftDeleted)
                                    {
                                        var type=ed.Type;
                                        var message=ed["Message"];
                                        ed.Type = EventType.ProfileError;
                                        ed["Message"] = "Soft deleted profile access attempt";
                                        ed.SendEvent();
                                        ed.Type = type;
                                        ed["Message"] = message;
                                    }
                                }
                            }

                            // Check for any required headers
                            if (_options.RequiredHeaders.Count > 0)
                            {
                                var missing = _options.RequiredHeaders.FirstOrDefault(x => string.IsNullOrEmpty(rd.Headers[x]));
                                if (!string.IsNullOrEmpty(missing))
                                {
                                    if (rd.Debug)
                                        Console.WriteLine($"Required header {missing} is missing from request.");

                                    throw new ProxyErrorException(
                                        ProxyErrorException.ErrorType.IncompleteHeaders,
                                        HttpStatusCode.ExpectationFailed,
                                        "Required header is missing: " + missing + "\n"
                                    );
                                }
                            }

                            // Check for any validate headers  ( both fields have been checked for existance )
                            if (_options.ValidateHeaders.Count > 0)
                            {
                                foreach (var header in _options.ValidateHeaders)
                                {
                                    // Check that the header exists in the destination header
                                    var lookup = rd.Headers[header.Key]!.Trim();
                                    List<string> values = [.. rd.Headers[header.Value]!.Split(',')];
                                    if (!values.Contains(lookup))
                                    {
                                        if (rd.Debug)
                                            Console.WriteLine($"Validation check failed for header: {header.Key} = {lookup}");
                                        throw new ProxyErrorException(
                                            ProxyErrorException.ErrorType.InvalidHeader,
                                            HttpStatusCode.ExpectationFailed,
                                            "Validation check failed for header: " + header.Key + "\n"
                                        );
                                    }
                                }
                                if (rd.Debug)
                                    Console.WriteLine($"Validation check passed for all headers.");
                            }

                            // Determine priority boost based on the UserID 
                            if (_options.UniqueUserHeaders.Count > 0)
                            {
                                foreach (var header in _options.UniqueUserHeaders)
                                {
                                    rd.UserID += rd.Headers[header] ?? "";
                                }
                            }

                            if (String.IsNullOrEmpty(rd.UserID))
                            {
                                rd.UserID = "defaultUser";
                            }

                            ed["UserID"] = rd.UserID;
                            ed["S7P-ID"] = rd.MID;

                            if (rd.Debug)
                                Console.WriteLine($"UserID: {rd.UserID}");

                            // Determine priority boost based on the UserID
                            rd.Guid = _userPriority.addRequest(rd.UserID);
                            bool shouldBoost = _userPriority.boostIndicator(rd.UserID, out float boostValue);
                            userPriorityBoost = shouldBoost ? 1 : 0;

                            ed["GUID"] = rd.Guid.ToString();

                            var priorityKey = rd.Headers[_priorityHeaderName];
                            if (!string.IsNullOrEmpty(priorityKey) && _options.PriorityKeys.Contains(priorityKey)) //lookup the priority
                            {
                                var index = _options.PriorityKeys.IndexOf(priorityKey);
                                if (index >= 0)
                                {
                                    priority = _options.PriorityValues[index];
                                }
                            }
                            rd.Priority = priority;
                            rd.Priority2 = userPriorityBoost;
                            rd.EnqueueTime = DateTime.UtcNow;

                            ed["S7P-Priority"] = priority.ToString();
                            ed["S7P-Priority2"] = userPriorityBoost.ToString();

                            // Save the timeout header value if it exists
                            if (rd.Headers[_options.TimeoutHeader] != null && int.TryParse(rd.Headers[_options.TimeoutHeader], out var timeout))
                            {
                                rd.defaultTimeout = timeout;
                            }
                            else
                            {
                                rd.defaultTimeout = _options.Timeout;
                            }

                            // Calculate expiresAt time based on the timeout header or default TTL
                            rd.CalculateExpiration(_options.DefaultTTLSecs, _options.TTLHeader);
                            ed["DefaultTimeout"] = rd.defaultTimeout.ToString();

                            // Check circuit breaker status and enqueue the request
                            if (_backends.CheckFailedStatus())
                            {
                                notEnqued = true;
                                notEnquedCode = 429;

                                ed["Message"] = "Circuit breaker on - 429";
                                retrymsg = $"Too many failures in last {_options.CircuitBreakerTimeslice} seconds";
                                logmsg = "Circuit breaker on  => 429:";
                            }
                            else if (_requestsQueue.thrdSafeCount >= _options.MaxQueueLength)
                            {
                                notEnqued = true;
                                notEnquedCode = 429;

                                retrymsg = ed["Message"] = "Queue is full";
                                logmsg = "Queue is full  => 429:";
                            }
                            else if (_backends.ActiveHostCount() == 0)
                            {
                                notEnqued = true;
                                notEnquedCode = 429;

                                retrymsg = ed["Message"] = "No active hosts";
                                logmsg = "No active hosts  => 429:";
                            }


                            // Enqueue the request

                            else if (!_requestsQueue.Enqueue(rd, priority, userPriorityBoost, rd.EnqueueTime))
                            {
                                notEnqued = true;
                                notEnquedCode = 429;

                                retrymsg = ed["Message"] = "Failed to enqueue request";
                                logmsg = "Failed to enqueue request  => 429:";
                            }

                        }
                        catch (ProxyErrorException e)
                        {
                            notEnqued = true;
                            notEnquedCode = (int)e.StatusCode;

                            logmsg = retrymsg = ed["Message"] = e.Message;
                        }
                    }
                    else
                    {
                        if (rd.Context is not null)
                        {
                            notEnqued = true;
                            notEnquedCode = 503;

                            retrymsg = ed["Message"] = "Server is shutting down.";
                            logmsg = "Connection rejected, Server is shutting down";
                            rd.Context.Response.Headers["Retry-After"] = "120"; // Retry after 120 seconds (adjust as needed)

                        }
                    }

                    // Distributed tracking:
                    // If incoming request already has a ParentId, use it otherwise use the current request's MID as ParentId
                    if (rd.Context?.Request.Headers["ParentId"] is string parentId && !string.IsNullOrEmpty(parentId))
                    {
                         rd.ParentId = parentId;
                    }
                    else
                    {
                        rd.ParentId = rd.MID;
                    }

                    ed.MID = rd.MID;
                    ed.ParentId = rd.ParentId;
                    ed["ActiveHosts"] = _backends.ActiveHostCount().ToString();
                    ed["QueueLength"] = _requestsQueue.thrdSafeCount.ToString();
                    ed["ExpiresAt"] = rd.ExpiresAtString;
                    ed["Priority"] = priority.ToString();
                    ed["Priority2"] = userPriorityBoost.ToString();

                    if (notEnqued)
                    {

                        if (rd.Context is not null)
                        {
                            ed.Type = EventType.ServerError;
                            ed["ErrorDetail"] = "EnqueueFailed";
                            ed.Status = (HttpStatusCode)notEnquedCode;
                            ed["QueueLength"] = _requestsQueue.thrdSafeCount.ToString();
                            ed["ActiveHosts"] = _backends.ActiveHostCount().ToString();

                            Console.Error.WriteLine($"{logmsg}: Queue Length: {_requestsQueue.thrdSafeCount}, Active Hosts: {_backends.ActiveHostCount()}");

                            try
                            {
                                rd.Context.Response.StatusCode = notEnquedCode;
                                ed["Retry-After"] = rd.Context.Response.Headers["Retry-After"] = (_backends.ActiveHostCount() == 0) ? _options.PollInterval.ToString() : "500";

                                using (var writer = new System.IO.StreamWriter(rd.Context.Response.OutputStream))
                                {
                                    await writer.WriteAsync(retrymsg).ConfigureAwait(false);
                                }
                                rd.Context.Response.Close();
                            }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine($"Request was not enqueue'd and got an error writing on network: {ex.Message}");
                                ed["ErrorWritingResponse"] = ex.Message;
                            }
                            _staticEvent.WriteOutput($"Pri: {priority} Stat: 429 Path: {rd.Path}");
                        }

                        ed.SendEvent();
                        _userPriority.removeRequest(rd.UserID, rd.Guid);
                    }
                    else
                    {
                        ProxyEvent temp_ed = new(ed);
                        temp_ed.Type = EventType.ProxyRequestEnqueued;
                        temp_ed["Message"] = "Enqueued request";

                        temp_ed.SendEvent();
                        _staticEvent.WriteOutput($"Enque Pri: {priority}, User: {rd.UserID}, Q-Len: {_requestsQueue.thrdSafeCount}, CB: {_backends.CheckFailedStatus()}, Hosts: {_backends.ActiveHostCount()} ");
                    }
                }
                else
                {
                    _cancellationToken.ThrowIfCancellationRequested(); // This will throw if the token is cancelled while waiting for a request.
                }
                //}
            }
            catch (IOException ioEx)
            {
                ed.WriteOutput($"An IO exception occurred: {ioEx.Message}");
            }
            catch (OperationCanceledException)
            {
                // Handle the cancellation request (e.g., break the loop, log the cancellation, etc.)
                _staticEvent.WriteOutput("HTTP server shutdown initiated.");
                break; // Exit the loop
            }
            catch (Exception e)
            {
                _telemetryClient?.TrackException(e);
                _staticEvent.WriteOutput($"Error: {e.Message}\n{e.StackTrace}");
            }
        }

        _staticEvent.WriteOutput("HTTP server stopped.");
    }
}