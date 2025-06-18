using System.Net;
using System.Text.Json;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using System.Threading;
using SimpleL7Proxy.Backend;
using SimpleL7Proxy.User;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Queue;
using SimpleL7Proxy.Proxy;
using SimpleL7Proxy.ServiceBus;
using System.Text;


namespace SimpleL7Proxy;
// This class represents a server that listens for HTTP requests and processes them.
// It uses a priority queue to manage incoming requests and supports telemetry for monitoring.
// If the incoming request has the S7PPriorityKey header, it will be assigned a priority based the S7PPriority header.
public class Server : BackgroundService
{
    //    private readonly IBackendOptions? _options;
    private readonly BackendOptions _options;
    private readonly TelemetryClient? _telemetryClient; // Add this line
    private readonly HttpListener _httpListener;

    private readonly IBackendService _backends;

    private readonly IUserPriorityService _userPriority;
    private readonly IUserProfileService _userProfile;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly IConcurrentPriQueue<RequestData> _requestsQueue;// = new ConcurrentPriQueue<RequestData>();
    //private readonly IServiceBusRequestService _serviceBusRequestService;
    private readonly ILogger<Server> _logger;
    private static bool _isShuttingDown = false;
    private readonly string _priorityHeaderName;

    private readonly IEventClient? _eventHubClient;

    // public void enqueueShutdownRequest() {
    //     var shutdownRequest = new RequestData(Constants.Shutdown);
    //     _requestsQueue.Enqueue(shutdownRequest, 3, 0, DateTime.UtcNow, true);
    // }

    // Constructor to initialize the server with backend options and telemetry client.
    public Server(
        IConcurrentPriQueue<RequestData> requestsQueue,
        IOptions<BackendOptions> backendOptions,
        IHostApplicationLifetime appLifetime,
        IUserPriorityService userPriority,
        IUserProfileService userProfile,
        //IServiceBusRequestService serviceBusRequestService,
        IEventClient? eventHubClient,
        IBackendService backends,
        TelemetryClient? telemetryClient,
        ILogger<Server> logger)
    {
        ArgumentNullException.ThrowIfNull(backendOptions, nameof(backendOptions));
        ArgumentNullException.ThrowIfNull(backends, nameof(backends));
        ArgumentNullException.ThrowIfNull(userPriority, nameof(userPriority));
        ArgumentNullException.ThrowIfNull(userProfile, nameof(userProfile));
        ArgumentNullException.ThrowIfNull(logger, nameof(logger));
        ArgumentNullException.ThrowIfNull(appLifetime, nameof(appLifetime));
        //ArgumentNullException.ThrowIfNull(telemetryClient, nameof(telemetryClient));
        ArgumentNullException.ThrowIfNull(requestsQueue, nameof(requestsQueue));
        //ArgumentNullException.ThrowIfNull(serviceBusRequestService, nameof(serviceBusRequestService));



        _options = backendOptions.Value;
        _backends = backends;
        _eventHubClient = eventHubClient;
        _telemetryClient = telemetryClient;
        _userPriority = userPriority;
        _userProfile = userProfile;
        _logger = logger;
        _requestsQueue = requestsQueue;
        _priorityHeaderName = _options.PriorityKeyHeader;
        //_serviceBusRequestService = serviceBusRequestService;

        //appLifetime.ApplicationStopping.Register(OnApplicationStopping);

        var _listeningUrl = $"http://+:{_options.Port}/";

        _httpListener = new HttpListener();
        _httpListener.Prefixes.Add(_listeningUrl);

        var timeoutTime = TimeSpan.FromMilliseconds(_options.Timeout).ToString(@"hh\:mm\:ss\.fff");
        _logger.LogInformation($"Server configuration:  Port: {_options.Port} Timeout: {timeoutTime} Workers: {_options.Workers}");
    }

    public void BeginShutdown()
    {
        _isShuttingDown = true;
    }

    public Task StopListening(CancellationToken cancellationToken)
    {
        _cancellationTokenSource?.Cancel();
        _logger.LogInformation("Server stopping.");
        return Task.CompletedTask;
    }

    // public ConcurrentPriQueue<RequestData> Queue() {
    //     return _requestsQueue;
    // }

    // Method to start the server and begin processing requests.
    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        Task backendStartTask;
        try
        {
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            _backends.Start();
            backendStartTask = _backends.WaitForStartup(20);

            _httpListener.Start();
            _logger.LogInformation($"Listening on {_options?.Port}");
            // Additional setup or async start operations can be performed here

            _requestsQueue.StartSignaler(cancellationToken);
        }
        catch (HttpListenerException ex)
        {
            // Handle specific errors, e.g., port already in use
            _logger.LogError($"Failed to start HttpListener: {ex.Message}");
            // Consider rethrowing, logging the error, or handling it as needed
            throw new Exception("Failed to start the server due to an HttpListener exception.", ex);
        }
        catch (Exception ex)
        {
            // Handle other potential errors
            _logger.LogError($"An error occurred: {ex.Message}");
            throw new Exception("An error occurred while starting the server.", ex);
        }

        return backendStartTask.ContinueWith((x) => Run(cancellationToken), cancellationToken);

    }

    static long counter = 0;

    // Continuously listens for incoming HTTP requests and processes them.
    // Requests are enqueued with a priority based on specific headers.
    // The method runs until a cancellation is requested.
    // Each request is enqueued with a priority into BlockingPriorityQueue.
    public async Task Run(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(_httpListener, nameof(_httpListener));
        //ArgumentNullException.ThrowIfNull(_cancellationTokenSource, nameof(_cancellationTokenSource));
        ArgumentNullException.ThrowIfNull(_options, nameof(_options));

        //var cancellationToken = _cancellationTokenSource.Token;

        int livenessPriority = _options.PriorityValues.Min();
        bool doUserProfile = _options.UseProfiles;
        bool doAsync = _options.AsyncModeEnabled;

        while (!cancellationToken.IsCancellationRequested)
        {
            ProxyEvent ed = null!;

            try
            {
                // Use the CancellationToken to asynchronously wait for an HTTP request.
                var getContextTask = _httpListener.GetContextAsync();

                // call GetContextAsync in a way that it can be cancelled
                var completedTask = await Task.WhenAny(getContextTask, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);

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
                    var rd = new RequestData(await getContextTask.ConfigureAwait(false), requestId);

                    ed = rd.EventData;
                    ed["Date"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                    ed["S7P-Host-ID"] = _options.IDStr;
                    ed["Revision"] = _options.Revision;
                    ed["ContainerApp"] = _options.ContainerApp;
                    ed["Path"] = rd.Path ?? "N/A";
                    ed["Method"] = rd.Method ?? "N/A";
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
                                    var headers = _userProfile.GetUserProfile(requestUser);

                                    if (headers != null)
                                    {
                                        foreach (var header in headers)
                                        {
                                            rd.Headers.Set(header.Key, header.Value);
                                            if (rd.Debug)
                                                Console.WriteLine($"Add Header: {header.Key} = {header.Value}");
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

                            // determine if the request is allowed async operation
                            if (doAsync && rd.Headers["AsyncEnabled"] != null && bool.TryParse(rd.Headers["AsyncEnabled"], out var allowed))
                            {
                                var clientInfo = _userProfile.GetAsyncParams(rd.UserID);
                                rd.runAsync = false;

                                if (clientInfo != null)
                                {
                                    rd.AsyncBlobAccessTimeoutSecs = clientInfo.AsyncBlobAccessTimeoutSecs;
                                                                        
                                    // Set blob storage and Service Bus information for async processing
                                    ed["AsyncBlobContainer"] = rd.BlobContainerName = clientInfo.ContainerName;
                                    ed["AsyncSBTopic"] = rd.SBTopicName = clientInfo.SBTopicName;
                                    ed["BlobAccessTimeout"] = clientInfo.AsyncBlobAccessTimeoutSecs.ToString();
                                    rd.runAsync = true;
                                }

                                if (rd.Debug)
                                {
                                    Console.WriteLine($"AsyncEnabled: {rd.runAsync}");
                                }
                            }

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

                            if (!notEnqued && doAsync)
                            {
                                rd.SBStatus = ServiceBusMessageStatusEnum.InQueue;
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

                    ed["ActiveHosts"] = _backends.ActiveHostCount().ToString();
                    ed["QueueLength"] = _requestsQueue.thrdSafeCount.ToString();
                    ed["MID"] = rd.MID;
                    ed["ExpiresAt"] = rd.ExpiresAtString;
                    ed["Priority"] = priority.ToString();
                    ed["Priority2"] = userPriorityBoost.ToString();

                    if (notEnqued)
                    {

                        if (rd.Context is not null)
                        {
                            ed["Type"] = "S7P-EnqueueFailed";
                            _logger.LogInformation($"{logmsg}: Queue Length: {_requestsQueue.thrdSafeCount}, Active Hosts: {_backends.ActiveHostCount()}", ed);

                            try
                            {
                                rd.Context.Response.StatusCode = notEnquedCode;
                                rd.Context.Response.Headers["Retry-After"] = (_backends.ActiveHostCount() == 0) ? _options.PollInterval.ToString() : "500";
                                using (var writer = new System.IO.StreamWriter(rd.Context.Response.OutputStream))
                                {
                                    await writer.WriteAsync(retrymsg).ConfigureAwait(false);
                                }
                                rd.Context.Response.Close();
                            }
                            catch (Exception ex)
                            {
                                _logger.LogInformation($"Request was not enqueue'd and got an error writing on network: {ex.Message}", ed);
                            }
                            _logger.LogInformation($"Pri: {priority} Stat: 429 Path: {rd.Path}");
                        }

                        _userPriority.removeRequest(rd.UserID, rd.Guid);
                    }
                    else
                    {
                        ProxyEvent temp_ed = new(ed);
                        temp_ed["Type"] = "S7P-Enqueue";
                        temp_ed["Message"] = "Enqueued request";

                        _logger.LogInformation($"Enque Pri: {priority}, User: {rd.UserID}, Q-Len: {_requestsQueue.thrdSafeCount}, CB: {_backends.CheckFailedStatus()}, Hosts: {_backends.ActiveHostCount()} ");
                        _eventHubClient?.SendData(temp_ed);
                    }
                }
                else
                {
                    cancellationToken.ThrowIfCancellationRequested(); // This will throw if the token is cancelled while waiting for a request.
                }
                //}
            }
            catch (IOException ioEx)
            {
                _logger.LogError($"An IO exception occurred: {ioEx.Message}", ed);
            }
            catch (OperationCanceledException)
            {
                // Handle the cancellation request (e.g., break the loop, log the cancellation, etc.)
                WriteOutput("HTTP server shutdown initiated.", ed);
                break; // Exit the loop
            }
            catch (Exception e)
            {
                _telemetryClient?.TrackException(e);
                _logger.LogError($"Error: {e.Message}\n{e.StackTrace}", ed);
            }
        }

        _logger.LogInformation("HTTP server stopped.");
    }

    private void WriteOutput(string data = "", ProxyEvent eventData = null)
    {

        try
        {
            var ldata = eventData ?? new();

            // Log the data to the console
            if (!string.IsNullOrEmpty(data))
            {
                Console.WriteLine(data);
                ldata["Message"] = data;
            }

            if (!ldata.TryGetValue("Type", out var typeValue))
            {
                ldata["Type"] = "S7P-Console";
            }

            _eventHubClient?.SendData(ldata);
        }
        catch (Exception ex)
        {
            // Handle any exceptions that occur during logging
            Console.WriteLine($"Error writing output: {ex.Message}");
        }
    }
}