using System.Net;
using System.Text.Json;
using Microsoft.ApplicationInsights;
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

        var _listeningUrl = $"http://+:{_options.Port}/";

        httpListener = new HttpListener();
        httpListener.Prefixes.Add(_listeningUrl);

        var timeoutTime = TimeSpan.FromMilliseconds(_options.Timeout).ToString(@"hh\:mm\:ss\.fff");
        WriteOutput($"Server configuration:  Port: {_options.Port} Timeout: {timeoutTime} Workers: {_options.Workers}");
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
            WriteOutput($"Listening on {_options?.Port}");
            // Additional setup or async start operations can be performed here

            return _requestsQueue;
        }
        catch (HttpListenerException ex)
        {
            // Handle specific errors, e.g., port already in use
            WriteOutput($"Failed to start HttpListener: {ex.Message}");
            // Consider rethrowing, logging the error, or handling it as needed
            throw new Exception("Failed to start the server due to an HttpListener exception.", ex);
        }
        catch (Exception ex)
        {
            // Handle other potential errors
            WriteOutput($"An error occurred: {ex.Message}");
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
            try
            {
                // Use the CancellationToken to asynchronously wait for an HTTP request.
                var getContextTask = httpListener.GetContextAsync();
                //using (var delayCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken))
                //{
                //var delayTask = Task.Delay(Timeout.Infinite, delayCts.Token);

                //var completedTask = await Task.WhenAny(getContextTask, delayTask).ConfigureAwait(false);
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
                    Dictionary<string, string> ed = [];

                    Interlocked.Increment(ref counter);
                    var requestId = _options.IDStr + counter.ToString();

                    //delayCts.Cancel();
                    var rd = new RequestData(await getContextTask.ConfigureAwait(false), requestId);

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
                            // Remove any disallowed headers
                            foreach (var header in _options.DisallowedHeaders)
                            {
                                rd.Headers.Remove(header);   
                            }
                            
                            rd.UserID = "";

                            // Lookup the user profile and add the headers to the request
                            if (doUserProfile)
                            {
                                var requestUser = rd.Headers.Get(_options.UserProfileHeader);
                                if (!string.IsNullOrEmpty(requestUser))
                                {
                                    var headers = _userProfile.GetUserProfile(requestUser);

                                    if (headers != null)
                                    {
                                        foreach (var header in headers)
                                        {
                                            rd.Headers.Set(header.Key, header.Value);
                                            Console.WriteLine($"User profile header {header.Key} = {header.Value} added to request.");
                                        }
                                    }
                                }
                            }

                            // Check for any required headers
                            if (_options.RequiredHeaders.Count > 0)
                            {
                                var missing = _options.RequiredHeaders.FirstOrDefault(x => string.IsNullOrEmpty(rd.Headers[x]));
                                if (!string.IsNullOrEmpty(missing)) {
                                    throw new ProxyErrorException(
                                        ProxyErrorException.ErrorType.IncompleteHeaders,
                                        HttpStatusCode.ExpectationFailed,
                                        "Required header is missing: " + missing
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
                                        throw new ProxyErrorException(
                                            ProxyErrorException.ErrorType.InvalidHeader,
                                            HttpStatusCode.ExpectationFailed,
                                            "Validation check failed for header: " + header.Key
                                        );
                                    }
                                }
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

                            // Determine priority boost based on the UserID
                            rd.Guid = _userPriority.addRequest(rd.UserID);
                            bool shouldBoost = _userPriority.boostIndicator(rd.UserID, out float boostValue);
                            userPriorityBoost = shouldBoost ? 1 : 0;


                            var priorityKey = rd.Headers.Get("S7PPriorityKey");
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

                            ed["S7P-Hostname"] = _options.IDStr;
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

                    if (notEnqued)
                    {

                        if (rd.Context is not null)
                        {
                            ed["Type"] = "S7P-EnqueueFailed";
                            ed["QueueLength"] = _requestsQueue.thrdSafeCount.ToString();
                            ed["ActiveHosts"] = _backends.ActiveHostCount().ToString();
                            WriteOutput($"{logmsg}: Queue Length: {_requestsQueue.thrdSafeCount}, Active Hosts: {_backends.ActiveHostCount()}", ed);

                            rd.Context.Response.StatusCode = notEnquedCode;
                            rd.Context.Response.Headers["Retry-After"] = (_backends.ActiveHostCount() == 0) ? _options.PollInterval.ToString() : "500";
                            using (var writer = new System.IO.StreamWriter(rd.Context.Response.OutputStream))
                            {
                                await writer.WriteAsync(retrymsg).ConfigureAwait(false);
                            }
                            rd.Context.Response.Close();
                            WriteOutput($"Pri: {priority} Stat: {notEnquedCode} Path: {rd.Path}");
                        }

                        _userPriority.removeRequest(rd.UserID, rd.Guid);
                    }
                    else
                    {
                        ed["Type"] = "S7P-Enqueue";
                        ed["Message"] = "Enqueued request";
                        ed["QueueLength"] = _requestsQueue.thrdSafeCount.ToString();
                        ed["ActiveHosts"] = _backends.ActiveHostCount().ToString();
                        ed["Priority"] = priority.ToString();

                        WriteOutput($"Enque Pri: {priority}, User: {rd.UserID}, Queue: {_requestsQueue.thrdSafeCount}, CB: {_backends.CheckFailedStatus()}, Hosts: {_backends.ActiveHostCount()} ", ed);
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
                WriteOutput($"An IO exception occurred: {ioEx.Message}");
            }
            catch (OperationCanceledException)
            {
                // Handle the cancellation request (e.g., break the loop, log the cancellation, etc.)
                WriteOutput("Operation was canceled. Stopping the listener.");
                break; // Exit the loop
            }
            catch (Exception e)
            {
                _telemetryClient?.TrackException(e);
                WriteOutput($"Error: {e.Message}\n{e.StackTrace}");
            }
        }

        WriteOutput("Listener task stopped.");
    }

    private void WriteOutput(string data = "", Dictionary<string, string>? eventData = null)
    {

        // Log the data to the console
        if (!string.IsNullOrEmpty(data))
        {
            Console.WriteLine(data);

            // if eventData is null, create a new dictionary and add the message to it
            if (eventData == null)
            {
                eventData = new Dictionary<string, string>();
                eventData.Add("Message", data);
            }
        }

        if (eventData == null)
            eventData = new Dictionary<string, string>();

        if (!eventData.TryGetValue("Type", out var typeValue))
        {
            eventData["Type"] = "S7P-Console";
        }

        string jsonData = JsonSerializer.Serialize(eventData);
        _eventHubClient?.SendData(jsonData);
    }
}