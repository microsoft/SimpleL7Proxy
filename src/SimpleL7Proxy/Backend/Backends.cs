using System.Diagnostics;
using Microsoft.Extensions.Options;
using System.Text;
using Azure.Identity;
using Azure.Core;
using SimpleL7Proxy.Events;
using System.Text.Json;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Collections.Concurrent;


namespace SimpleL7Proxy.Backend;

// This code has 3 objectives:  
// * Check the status of each backend host and measure its latency
// * Filter the active hosts based on the success rate
// * Fetch the OAuth2 token and refresh it 100ms minutes before it expires
public class Backends : IBackendService
{
  public List<BackendHostHealth> _backendHosts { get; set; }
  private List<BackendHostHealth> _activeHosts;

  private readonly BackendOptions _options;
  private static readonly bool _debug = false;

  private static double _successRate;
  private static DateTime _lastStatusDisplay = DateTime.Now - TimeSpan.FromMinutes(10);  // Force display on first run
  private static DateTime _lastGCTime = DateTime.Now;
  private static bool _isRunning = false;

  private CancellationTokenSource _cancellationTokenSource;
  private CancellationToken _cancellationToken;

  private AccessToken? AuthToken { get; set; }

  private readonly IEventClient _eventClient;
  CancellationTokenSource workerCancelTokenSource = new CancellationTokenSource();
  private readonly TelemetryClient _telemetryClient;
  private readonly ILogger<Backends> _logger;

  private Task? PollerTask;
  //public Backends(List<BackendHost> hosts, HttpClient client, int interval, int successRate)
  public Backends(
      IOptions<BackendOptions> options,
      IBackendHostHealthCollection backendHostCollection, //
      IHostApplicationLifetime appLifetime,               //
      IEventClient? eventClient,
      CancellationTokenSource cancellationTokenSource,    //
      TelemetryClient telemetryClient,
      ILogger<Backends> logger)
  {
    if (options == null) throw new ArgumentNullException(nameof(options));
    if (options.Value == null) throw new ArgumentNullException(nameof(options.Value));
    if (options.Value.Hosts == null) throw new ArgumentNullException(nameof(options.Value.Hosts));
    if (options.Value.Client == null) throw new ArgumentNullException(nameof(options.Value.Client));
    ArgumentNullException.ThrowIfNull(backendHostCollection, nameof(backendHostCollection));
    ArgumentNullException.ThrowIfNull(appLifetime, nameof(appLifetime));
    ArgumentNullException.ThrowIfNull(cancellationTokenSource, nameof(cancellationTokenSource));
    ArgumentNullException.ThrowIfNull(telemetryClient, nameof(telemetryClient));
    ArgumentNullException.ThrowIfNull(logger, nameof(logger));
    ArgumentNullException.ThrowIfNull(eventClient, nameof(eventClient));

    //    appLifetime.ApplicationStopping.Register(OnApplicationStopping);

    _eventClient = eventClient;
    _telemetryClient = telemetryClient;
    _backendHosts = backendHostCollection.Hosts;
    _logger = logger;

    _cancellationTokenSource = cancellationTokenSource;
    _cancellationToken = _cancellationTokenSource.Token;


    var bo = options.Value; // Access the IBackendOptions instance

    _options = bo;
    _activeHosts = [];
    _successRate = bo.SuccessRate / 100.0;
    //_hosts = bo.Hosts;
    FailureThreshold = bo.CircuitBreakerErrorThreshold;
    FailureTimeFrame = bo.CircuitBreakerTimeslice;
    allowableCodes = bo.AcceptableStatusCodes;
  }

  public Task Stop()
  {
    _logger.LogInformation("Stopping backend.");
    _cancellationTokenSource.Cancel();

    return PollerTask ?? Task.CompletedTask;
  }


  public void Start()
  {
    // Start the backend poller task
    PollerTask = Task.Run(() => Run(), _cancellationToken);

    // If OAuth is enabled, fetch the token
    if (_options.UseOAuth)
    {
      GetToken();
    }

    _logger.LogInformation("Backend service started.");
  }

  private readonly List<DateTime> hostFailureTimes = [];
  ConcurrentQueue<DateTime> hostFailureTimes2 = new ConcurrentQueue<DateTime>();
  private readonly int FailureThreshold = 5;
  private readonly int FailureTimeFrame = 10; // seconds
  static int[] allowableCodes = { 200, 401, 403, 408, 410, 412, 417, 400 };

  public List<BackendHostHealth> GetActiveHosts() => _activeHosts;
  public int ActiveHostCount() => _activeHosts.Count;

  static Dictionary<string, string> logerror = new Dictionary<string, string>() { { "Type", "CircuitBreaker-Error-Event" } };
  public void TrackStatus(int code, bool wasException)
  {
    if (allowableCodes.Contains(code) && !wasException)
    {
      return;
    }

    DateTime now = DateTime.UtcNow;

    // truncate older entries
    while (hostFailureTimes2.TryPeek(out var t) && (now - t).TotalSeconds >= FailureTimeFrame)
    {
      hostFailureTimes2.TryDequeue(out var _);
    }

    hostFailureTimes2.Enqueue(now);
    logerror["Code"] = code.ToString();
    logerror["Time"] = now.ToString();
    logerror["WasException"] = wasException.ToString();
    logerror["Count"] = hostFailureTimes2.Count.ToString();

    SendEventData(logerror);
  }

  public List<BackendHostHealth> GetHosts() => _backendHosts;

  // returns true if the service is in failure state
  public bool CheckFailedStatus()
  {
    //    Console.WriteLine($"Checking failed status: {hostFailureTimes2.Count} >= {FailureThreshold}");
    if (hostFailureTimes2.Count < FailureThreshold)
    {
      return false;
    }

    DateTime now = DateTime.UtcNow;
    while (hostFailureTimes2.TryPeek(out var t) && (now - t).TotalSeconds >= FailureTimeFrame)
    {
      hostFailureTimes2.TryDequeue(out var _);
    }
    return hostFailureTimes2.Count >= FailureThreshold;

  }

  public string OAuth2Token()
  {
    while (AuthToken?.ExpiresOn < DateTime.UtcNow)
    {
      Task.Delay(100).Wait();
    }
    return AuthToken?.Token ?? "";
  }

  public async Task WaitForStartup(int timeout)
  {
    var start = DateTime.Now;
    for (int i = 0; i < 10; i++)
    {
      var startTimer = DateTime.Now;
      // Wait for the backend poller to start or until the timeout is reached. Make sure that if a token is required, it is available.
      while (!_isRunning &&
            (!_options.UseOAuth || AuthToken?.Token != "") &&
            (DateTime.Now - startTimer).TotalSeconds < timeout)
      {
        await Task.Delay(1000, _cancellationToken); // Use Task.Delay with cancellation token
        if (_cancellationToken.IsCancellationRequested)
        {
          return;
        }
      }
      if (!_isRunning)
      {
        _logger.LogError($"Backend Poller did not start in the last {timeout} seconds.");
      }
      else
      {
        _logger.LogInformation($"Backend Poller started in {(DateTime.Now - start).TotalSeconds} seconds.");
        return;
      }
    }
    throw new Exception("Backend Poller did not start in time.");
  }

  private readonly Dictionary<string, bool> currentHostStatus = [];
  private async Task Run()
  {

    using (HttpClient _client = CreateHttpClient())
    {
      var intervalTime = TimeSpan.FromMilliseconds(_options.PollInterval).ToString(@"hh\:mm\:ss");
      var timeoutTime = TimeSpan.FromMilliseconds(_options.PollTimeout).ToString(@"hh\:mm\:ss\.fff");
      _logger.LogInformation($"Starting Backend Poller: Interval: {intervalTime}, SuccessRate: {_successRate}, Timeout: {timeoutTime}");

      _client.Timeout = TimeSpan.FromMilliseconds(_options.PollTimeout);

      using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken))
      {
        while (!linkedCts.Token.IsCancellationRequested && !_cancellationToken.IsCancellationRequested)
        {
          try
          {
            await UpdateHostStatus(_client);
            FilterActiveHosts();

            if ((DateTime.Now - _lastStatusDisplay).TotalSeconds > 60)
            {
              DisplayHostStatus();
            }

            await Task.Delay(_options.PollInterval, linkedCts.Token);
          }
          catch (OperationCanceledException)
          {
            _logger.LogInformation("Stopping the backend poller task.");
            break;
          }
          catch (Exception e)
          {
            _logger.LogError($"An unexpected error occurred: {e.Message}");
          }
        }
      }

      _logger.LogInformation("Backend Poller stopped.");
    }
  }

  private HttpClient CreateHttpClient()
  {
    if (Environment.GetEnvironmentVariable("IgnoreSSLCert")?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true)
    {
      var handler = new HttpClientHandler
      {
        ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
      };
      return new HttpClient(handler);
    }
    else
    {
      return new HttpClient();
    }
  }

  private async Task<bool> UpdateHostStatus(HttpClient _client)
  {
    var _statusChanged = false;

    if (_backendHosts == null || _backendHosts.Count == 0)
    {
      return _statusChanged;
    }

    foreach (var host in _backendHosts)
    {
      host.ResetStatus();
      var currentStatus = await GetHostStatus(host, _client);
      bool statusChanged = !currentHostStatus.ContainsKey(host.Host) || currentHostStatus[host.Host] != currentStatus;

      currentHostStatus[host.Host] = currentStatus;
      host.AddCallSuccess(currentStatus);

      if (statusChanged)
      {
        _statusChanged = true;
      }
    }

    if (_debug)
      _logger.LogDebug("Returning status changed: " + _statusChanged);

    return _statusChanged;
  }

  private async Task<bool> GetHostStatus(BackendHostHealth host, HttpClient client)
  {
    ProxyEvent probeData = new()
    {
      EventData =
              {
                ["ProxyHost"] = _options.HostName,
                ["Host"] = host.Host,
                ["Port"] = host.Port.ToString(),
                ["Path"] = host.ProbePath
              }
    };

    if (_debug)
      _logger.LogDebug($"Checking host {host.Url + host.ProbePath}");

    HttpRequestMessage request = new(HttpMethod.Get, host.ProbeUrl);
    if (_options.UseOAuth)
    {
      request.Headers.Authorization = new("Bearer", OAuth2Token());
    }

    var stopwatch = Stopwatch.StartNew();

    try
    {

      var response = await client.SendAsync(request, _cancellationToken);
      stopwatch.Stop();
      var latency = stopwatch.Elapsed.TotalMilliseconds;

      // Update the host with the new latency
      host.AddLatency(latency);

      probeData.EventData["Latency"] = latency.ToString();
      probeData.EventData["Code"] = response.StatusCode.ToString();
      probeData.EventData["Type"] = "Poller";
      probeData.EventData["Host"] = host.Host;
      probeData.EventData["ID"] = _options.IDStr;

      response.EnsureSuccessStatusCode();

      _isRunning = true;

      // If the response is successful, add the host to the active hosts
      return response.IsSuccessStatusCode;
    }
    catch (UriFormatException e)
    {
      _telemetryClient?.TrackException(e);
      _logger.LogError($"Poller: Could not check probe: {e.Message}");
      probeData.EventData["Type"] = "Uri Format Exception";
      probeData.EventData["Code"] = "-";
    }
    catch (System.Threading.Tasks.TaskCanceledException)
    {
      _logger.LogError($"Poller: Host Timeout: {host.Host}");
      probeData.EventData["Type"] = "TaskCanceledException";
      probeData.EventData["Code"] = "-";
    }
    catch (HttpRequestException e)
    {
      _telemetryClient?.TrackException(e);
      _logger.LogError($"Poller: Host {host.Host} is down with exception: {e.Message}");
      probeData.EventData["Type"] = "HttpRequestException";
      probeData.EventData["Code"] = "-";
    }
    catch (OperationCanceledException)
    {
      // Handle the cancellation request (e.g., break the loop, log the cancellation, etc.)
      _logger.LogInformation("Poller: Operation was canceled. Stopping the server.");
      throw; // Exit the loop
    }
    catch (System.Net.Sockets.SocketException e)
    {
      _logger.LogError($"Poller: Host {host.Host} is down:  {e.Message}");
      probeData.EventData["Type"] = "SocketException";
      probeData.EventData["Code"] = "-";
    }
    catch (Exception e)
    {
      _telemetryClient?.TrackException(e);
      _logger.LogError($"Poller: Error: {e.Message}");
      probeData.EventData["Type"] = "Exception " + e.Message;
      probeData.EventData["Code"] = "-";
    }
    finally
    {
      _eventClient.SendData(probeData);
    }

    return false;
  }

  // Filter the active hosts based on the success rate
  private void FilterActiveHosts()
  {
    //Console.WriteLine("Filtering active hosts");
    _activeHosts = _backendHosts
        .Where(h => h.SuccessRate() > _successRate)
        .Select(h =>
        {
          h.CalculatedAverageLatency = h.AverageLatency();
          return h;
        })
        .OrderBy(h => h.CalculatedAverageLatency)
        .ToList();
  }

  public string HostStatus { get; set; } = "-";

  // Display the status of the hosts
  private void DisplayHostStatus()
  {
    StringBuilder sb = new();
    sb.Append("\n\n============ Host Status =========\n");

    int txActivity = 0;

    var statusIndicator = string.Empty;
    if (_backendHosts != null)
      foreach (var host in _backendHosts.OrderBy(h => h.AverageLatency()))
      {
        statusIndicator = host.SuccessRate() > _successRate ? "Good  " : "Errors";
        var roundedLatency = Math.Round(host.AverageLatency(), 3);
        var successRatePercentage = Math.Round(host.SuccessRate() * 100, 2);
        var hoststatus = host.GetStatus(out int calls, out int errors, out double average);
        txActivity += calls;
        txActivity += errors;

        sb.Append($"{statusIndicator} Host: {host.Url} Lat: {roundedLatency}ms Succ: {successRatePercentage}% {hoststatus}\n");
      }


    _lastStatusDisplay = DateTime.Now;
    HostStatus = sb.ToString();

    if (statusIndicator.StartsWith("Good"))
    {
      _logger.LogInformation(HostStatus);
    }
    else
    {
      _logger.LogError(HostStatus);
    }

    if (txActivity == 0 && (DateTime.Now - _lastGCTime).TotalSeconds > 60 * 15)
    {
      // Force garbage collection
      //Console.WriteLine("Running garbage collection");
      GC.Collect();
      GC.WaitForPendingFinalizers();
      _lastGCTime = DateTime.Now;
    }
  }

  private void SendEventData(Dictionary<string, string> eventData)//string urlWithPath, HttpStatusCode statusCode, DateTime requestDate, DateTime responseDate)
  {
    string jsonData = JsonSerializer.Serialize(eventData);
    _eventClient?.SendData(jsonData);
  }

  // Fetches the OAuth2 Token as a seperate task. The token is fetched and updated 100ms before it expires. 
  public void GetToken()
  {
    Task.Run(async () =>
    {
      try
      {
        // Loop until a cancellation is requested
        while (!_cancellationToken.IsCancellationRequested)
        {
          // Fetch the authentication token asynchronously
          AuthToken = await GetTokenAsync();

          if (AuthToken.HasValue)
          {
            var timeout = (AuthToken.Value.ExpiresOn - DateTimeOffset.UtcNow).TotalMilliseconds;

            if (timeout < 500)
            {
              _logger.LogInformation($"Auth Token is about to expire. Retrying in {timeout} ms.");
              await Task.Delay((int)timeout, _cancellationToken);
            }
            else
            {
              // Calculate the time to refresh the token, 100 ms before it expires
              var refreshTime = timeout - 100;
              _logger.LogInformation($"Auth Token expires on: {AuthToken.Value.ExpiresOn} Refresh in: {FormatMilliseconds(refreshTime)} (100 ms grace)");
              // Wait for the calculated refresh time or until a cancellation is requested
              await Task.Delay((int)refreshTime, _cancellationToken);
            }
          }
          else
          {
            // Handle the case where the token is null
            _logger.LogDebug("Auth Token is null. Retrying in 10 seconds.");
            await Task.Delay(TimeSpan.FromMilliseconds(10000), _cancellationToken);
          }

        }
      }
      catch (OperationCanceledException)
      {
        // Handle the cancellation request (e.g., break the loop, log the cancellation, etc.)
        _logger.LogInformation("Exiting fetching Auth Token: Operation was canceled.");
      }
      catch (Exception e)
      {
        // Handle any unexpected errors that occur during token fetching
        _logger.LogError($"An unexpected error occurred while fetching Auth Token: {e.Message}");
      }
    }, _cancellationToken);
  }

  public static string FormatMilliseconds(double milliseconds)
  {
    var timeSpan = TimeSpan.FromMilliseconds(milliseconds);
    return string.Format("{0:D2}:{1:D2}:{2:D2} {3:D3} milliseconds",
                         timeSpan.Hours,
                         timeSpan.Minutes,
                         timeSpan.Seconds,
                         timeSpan.Milliseconds);
  }

  public async Task<AccessToken> GetTokenAsync()
  {
    try
    {
      DefaultAzureCredential credential = new();
      TokenRequestContext context = new([_options.OAuthAudience]);
      return await credential.GetTokenAsync(context);
    }
    catch (AuthenticationFailedException ex)
    {
      _logger.LogError($"Authentication failed: {ex.Message}");
      // Handle the exception as needed, e.g., return a default value or rethrow the exception
      throw;
    }
    catch (Exception ex)
    {
      _logger.LogError($"An unexpected error occurred: {ex.Message}");
      // Handle other potential exceptions
      throw;
    }
  }

  // private void WriteOutput(string data = "", Dictionary<string, string>? eventData = null)
  // {
  //   // Log the data to the console
  //   if (!string.IsNullOrEmpty(data))
  //   {
  //     Console.WriteLine(data);

  //     // if eventData is null, create a new dictionary and add the message to it
  //     if (eventData == null)
  //     {
  //       eventData = new Dictionary<string, string>();
  //       eventData.Add("Message", data);
  //     }
  //   }

  //   if (eventData == null)
  //     eventData = new Dictionary<string, string>();

  //   if (!eventData.TryGetValue("Type", out var typeValue))
  //   {
  //     eventData["Type"] = "S7P-Backend-Status";
  //   }

  //   string jsonData = JsonSerializer.Serialize(eventData);
  //   _eventHubClient?.SendData(jsonData);
  // }

}
