using System.Net;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Options;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Queue;
using Microsoft.Extensions.Logging;
namespace SimpleL7Proxy;

// This class represents a server that listens for HTTP requests and processes them.
// It uses a priority queue to manage incoming requests and supports telemetry for monitoring.
// If the incoming request has the S7PPriorityKey header, it will be assigned a priority based the S7PPriority header.
public class Server : IServer
{
  private readonly BackendOptions? _options;
  private readonly TelemetryClient? _telemetryClient; // Add this line
  private readonly HttpListener _httpListener;

  private readonly Backends _backends;
  private CancellationToken _cancellationToken;
  private readonly IBlockingPriorityQueue<RequestData> _requestsQueue;
  private readonly IEventClient? _eventHubClient;
  private readonly ILogger<Server> _logger;

  // Constructor to initialize the server with backend options and telemetry client.
  public Server(
    IBlockingPriorityQueue<RequestData> blockingPriorityQueue,
    IOptions<BackendOptions> backendOptions,
    IEventClient? eventHubClient,
    Backends backends,
    TelemetryClient? telemetryClient,
    ILogger<Server> logger)
  {
    if (backendOptions == null) throw new ArgumentNullException(nameof(backendOptions));
    if (backendOptions.Value == null) throw new ArgumentNullException(nameof(backendOptions.Value));

    _options = backendOptions.Value;
    _backends = backends;
    _eventHubClient = eventHubClient;
    _telemetryClient = telemetryClient;
    _requestsQueue = blockingPriorityQueue;
    _logger = logger;

    var _listeningUrl = $"http://+:{_options.Port}/";

    _httpListener = new HttpListener();
    _httpListener.Prefixes.Add(_listeningUrl);

    var timeoutTime = TimeSpan.FromMilliseconds(_options.Timeout).ToString(@"hh\:mm\:ss\.fff");
    _logger.LogInformation($"Server configuration:  Port: {_options.Port} Timeout: {timeoutTime} Workers: {_options.Workers}");
  }

    public IBlockingPriorityQueue<RequestData> Queue() => _requestsQueue;

    // Method to start the server and begin processing requests.
    public void Start(CancellationToken cancellationToken)
  {
    try
    {
      _cancellationToken = cancellationToken;
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
  }

  // Continuously listens for incoming HTTP requests and processes them.
  // Requests are enqueued with a priority based on specific headers.
  // The method runs until a cancellation is requested.
  // Each request is enqueued with a priority into BlockingPriorityQueue.
  public async Task Run()
  {
    long counter = 0;

    if (_options == null) throw new ArgumentNullException(nameof(_options));

    while (!_cancellationToken.IsCancellationRequested)
    {
      try
      {
        // Use the CancellationToken to asynchronously wait for an HTTP request.
        var getContextTask = _httpListener.GetContextAsync();
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken);
        var delayTask = Task.Delay(Timeout.Infinite, delayCts.Token);
        var completedTask = await Task.WhenAny(getContextTask, delayTask).ConfigureAwait(false);

        // Cancel the delay task immedietly if the getContextTask completes first
        if (completedTask == getContextTask)
        {
            var mid = "";
            try
            {
                mid = _options.IDStr + counter++.ToString();
            }
            catch (OverflowException)
            {
                mid = _options.IDStr + "0";
                counter = 1;
            }

            delayCts.Cancel();
            var rd = new RequestData(await getContextTask.ConfigureAwait(false), mid);
            int priority = _options.DefaultPriority;
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
            rd.EnqueueTime = DateTime.UtcNow;

            var return429 = false;
            var retrymsg = "";

            Dictionary<string, string> ed = [];
            // Check circuit breaker status and enqueue the request
            if (_backends.CheckFailedStatus())
            {
                return429 = true;

                ed["Type"] = "S7P-CircuitBreaker";
                ed["Message"] = "Circuit breaker on - 429";
                ed["QueueLength"] = _requestsQueue.Count.ToString();
                ed["ActiveHosts"] = _backends.ActiveHostCount().ToString();
                retrymsg = "Too many failures in last 10 seconds";

                _logger.LogError($"Circuit breaker on => 429: Queue Length: {_requestsQueue.Count}, Active Hosts: {_backends.ActiveHostCount()}");
            }
            else if (_requestsQueue.Count >= _options.MaxQueueLength)
            {
                return429 = true;

                ed["Type"] = "S7P-QueueFull";
                ed["Message"] = "Queue is full";
                ed["QueueLength"] = _requestsQueue.Count.ToString();
                ed["ActiveHosts"] = _backends.ActiveHostCount().ToString();
                retrymsg = "Queue is full";

                _logger.LogError($"Queue is full => 429: Queue Length: {_requestsQueue.Count}, Active Hosts: {_backends.ActiveHostCount()}");
            }
            else if (_backends.ActiveHostCount() == 0)
            {
                return429 = true;

                ed["Type"] = "S7P-NoActiveHosts";
                ed["Message"] = "No active hosts";
                ed["QueueLength"] = _requestsQueue.Count.ToString();
                ed["ActiveHosts"] = _backends.ActiveHostCount().ToString();
                retrymsg = "No active hosts";

                _logger.LogError($"No active hosts => 429: Queue Length: {_requestsQueue.Count}, Active Hosts: {_backends.ActiveHostCount()}", ed);
            }
            // Enqueue the request
            else if (!_requestsQueue.Enqueue(rd, priority, rd.EnqueueTime))
            {
                return429 = true;

                ed["Type"] = "S7P-EnqueueFailed";
                ed["Message"] = "Failed to enqueue request";
                ed["QueueLength"] = _requestsQueue.Count.ToString();
                ed["ActiveHosts"] = _backends.ActiveHostCount().ToString();
                retrymsg = "Failed to enqueue request";

                _logger.LogError($"Failed to enqueue request => 429: Queue Length: {_requestsQueue.Count}, Active Hosts: {_backends.ActiveHostCount()}", ed);
            }

            if (return429)
            {
                if (rd.Context is not null)
                {
                    // send a 429 response to client in the number of milliseconds specified in Retry-After header
                    rd.Context.Response.StatusCode = 429;
                    rd.Context.Response.Headers["Retry-After"] = (_backends.ActiveHostCount() == 0) ? _options.PollInterval.ToString() : "500";

                    using StreamWriter writer = new(rd.Context.Response.OutputStream);
                    await writer.WriteAsync(retrymsg);
                    rd.Context.Response.Close();
                    _logger.LogError($"Pri: {priority} Stat: 429 Path: {rd.Path}");
                }
            }
            else
            {
                ed["Type"] = "S7P-Enqueue";
                ed["Message"] = "Enqueued request";
                ed["QueueLength"] = _requestsQueue.Count.ToString();
                ed["ActiveHosts"] = _backends.ActiveHostCount().ToString();
                ed["Priority"] = priority.ToString();

                _logger.LogInformation($"Enqueued request.  Pri: {priority} Queue Length: {_requestsQueue.Count} Status: {_backends.CheckFailedStatus()} Active Hosts: {_backends.ActiveHostCount()}", ed);
            }
        }
        else
        {
            _cancellationToken.ThrowIfCancellationRequested(); // This will throw if the token is cancelled while waiting for a request.
        }
      }
      catch (IOException ioEx)
      {
        _logger.LogError($"An IO exception occurred: {ioEx.Message}");
      }
      catch (OperationCanceledException)
      {
        // Handle the cancellation request (e.g., break the loop, log the cancellation, etc.)
        _logger.LogInformation("Operation was canceled. Stopping the listener.");
        break; // Exit the loop
      }
      catch (Exception e)
      {
        _telemetryClient?.TrackException(e);
        _logger.LogError($"Error: {e.Message}\n{e.StackTrace}");
      }
    }
    _requestsQueue.Stop();
    _logger.LogInformation("Listener task stopped.");
  }
}
