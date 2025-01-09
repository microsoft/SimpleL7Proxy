using System.Net;
using System.Text.Json;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Queue;

// This class represents a server that listens for HTTP requests and processes them.
// It uses a priority queue to manage incoming requests and supports telemetry for monitoring.
// If the incoming request has the S7PPriorityKey header, it will be assigned a priority based the S7PPriority header.
public class Server : IServer
{
  private BackendOptions? _options;
  private readonly TelemetryClient? _telemetryClient; // Add this line
  private HttpListener httpListener;

  private IBackendService _backends;
  private CancellationToken _cancellationToken;
  private IBlockingPriorityQueue<RequestData> _requestsQueue;
  private readonly IEventClient? _eventHubClient;

  // Constructor to initialize the server with backend options and telemetry client.
  public Server(
    IBlockingPriorityQueue<RequestData> blockingPriorityQueue,
    IOptions<BackendOptions> backendOptions,
    IEventClient? eventHubClient,
    IBackendService backends,
    TelemetryClient? telemetryClient)
  {
    if (backendOptions == null) throw new ArgumentNullException(nameof(backendOptions));
    if (backendOptions.Value == null) throw new ArgumentNullException(nameof(backendOptions.Value));

    _options = backendOptions.Value;
    _backends = backends;
    _eventHubClient = eventHubClient;
    _telemetryClient = telemetryClient;
    _requestsQueue = blockingPriorityQueue;

    var _listeningUrl = $"http://+:{_options.Port}/";

    httpListener = new HttpListener();
    httpListener.Prefixes.Add(_listeningUrl);

    var timeoutTime = TimeSpan.FromMilliseconds(_options.Timeout).ToString(@"hh\:mm\:ss\.fff");
    WriteOutput($"Server configuration:  Port: {_options.Port} Timeout: {timeoutTime} Workers: {_options.Workers}");
  }

  public IBlockingPriorityQueue<RequestData> Queue()
  {
    return _requestsQueue;
  }

  // Method to start the server and begin processing requests.
  public void Start(CancellationToken cancellationToken)
  {
    try
    {
      _cancellationToken = cancellationToken;
      httpListener.Start();
      WriteOutput($"Listening on {_options?.Port}");
      // Additional setup or async start operations can be performed here

      _requestsQueue.StartSignaler(cancellationToken);
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
    long counter = 0;

    if (_options == null) throw new ArgumentNullException(nameof(_options));

    while (!_cancellationToken.IsCancellationRequested)
    {
      try
      {
        // Use the CancellationToken to asynchronously wait for an HTTP request.
        var getContextTask = httpListener.GetContextAsync();
        using (var delayCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken))
        {
          var delayTask = Task.Delay(Timeout.Infinite, delayCts.Token);

          var completedTask = await Task.WhenAny(getContextTask, delayTask).ConfigureAwait(false);

          //  control to allow other tasks to run .. doesn't make sense here
          // await Task.Yield();

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
            // _requestsQueue.Add(new RequestData(await getContextTask.ConfigureAwait(false)));
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

            var ed = new Dictionary<string, string>();
            // Check circuit breaker status and enqueue the request
            if (_backends.CheckFailedStatus())
            {
              return429 = true;

              ed["Type"] = "S7P-CircuitBreaker";
              ed["Message"] = "Circuit breaker on - 429";
              ed["QueueLength"] = _requestsQueue.Count.ToString();
              ed["ActiveHosts"] = _backends.ActiveHostCount().ToString();
              retrymsg = "Too many failures in last 10 seconds";

              WriteOutput($"Circuit breaker on => 429: Queue Length: {_requestsQueue.Count}, Active Hosts: {_backends.ActiveHostCount()}", ed);
            }
            else if (_requestsQueue.Count >= _options.MaxQueueLength)
            {
              return429 = true;

              ed["Type"] = "S7P-QueueFull";
              ed["Message"] = "Queue is full";
              ed["QueueLength"] = _requestsQueue.Count.ToString();
              ed["ActiveHosts"] = _backends.ActiveHostCount().ToString();
              retrymsg = "Queue is full";

              WriteOutput($"Queue is full  => 429: Queue Length: {_requestsQueue.Count}, Active Hosts: {_backends.ActiveHostCount()}", ed);
            }
            else if (_backends.ActiveHostCount() == 0)
            {
              return429 = true;

              ed["Type"] = "S7P-NoActiveHosts";
              ed["Message"] = "No active hosts";
              ed["QueueLength"] = _requestsQueue.Count.ToString();
              ed["ActiveHosts"] = _backends.ActiveHostCount().ToString();
              retrymsg = "No active hosts";

              WriteOutput($"No active hosts => 429: Queue Length: {_requestsQueue.Count}, Active Hosts: {_backends.ActiveHostCount()}", ed);
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

              WriteOutput($"Failed to enqueue request => 429: Queue Length: {_requestsQueue.Count}, Active Hosts: {_backends.ActiveHostCount()}", ed);
            }

            if (return429)
            {

              if (rd.Context is not null)
              {
                // send a 429 response to client in the number of milliseconds specified in Retry-After header
                rd.Context.Response.StatusCode = 429;
                rd.Context.Response.Headers["Retry-After"] = (_backends.ActiveHostCount() == 0) ? _options.PollInterval.ToString() : "500";

                using (var writer = new System.IO.StreamWriter(rd.Context.Response.OutputStream))
                {
                  await writer.WriteAsync(retrymsg);
                }
                rd.Context.Response.Close();
                WriteOutput($"Pri: {priority} Stat: 429 Path: {rd.Path}");
              }
            }
            else
            {
              ed["Type"] = "S7P-Enqueue";
              ed["Message"] = "Enqueued request";
              ed["QueueLength"] = _requestsQueue.Count.ToString();
              ed["ActiveHosts"] = _backends.ActiveHostCount().ToString();
              ed["Priority"] = priority.ToString();

              WriteOutput($"Enqueued request.  Pri: {priority} Queue Length: {_requestsQueue.Count} Status: {_backends.CheckFailedStatus()} Active Hosts: {_backends.ActiveHostCount()}", ed);
            }
          }
          else
          {
            _cancellationToken.ThrowIfCancellationRequested(); // This will throw if the token is cancelled while waiting for a request.
          }
        }
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
    _requestsQueue.Stop();
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