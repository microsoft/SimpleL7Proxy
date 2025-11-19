using System.Diagnostics;
using Microsoft.Extensions.Options;
using System.Text;
using Azure.Identity;
using Azure.Core;
using System.Text.Json;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Collections.Concurrent;

using SimpleL7Proxy.Config;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Backend.Iterators;

namespace SimpleL7Proxy.Backend;

// This code has 3 objectives:  
// * Check the status of each backend host and measure its latency
// * Filter the active hosts based on the success rate
// * Fetch the OAuth2 token and refresh it 100ms minutes before it expires
public class Backends : IBackendService
{
  public List<BaseHostHealth> _backendHosts { get; set; }
  private List<BaseHostHealth> _activeHosts;
  private readonly IHostHealthCollection _backendHostCollection; 

  private readonly BackendOptions _options;
  private static readonly bool _debug = false;

  private static double _successRate;
  private static DateTime _lastStatusDisplay = DateTime.Now - TimeSpan.FromMinutes(10);  // Force display on first run
  private static DateTime _lastGCTime = DateTime.Now;
  private static bool _isRunning = false;
  private static ICircuitBreaker _circuitBreaker;

  private CancellationTokenSource _cancellationTokenSource;
  private CancellationToken _cancellationToken;

  public bool CheckFailedStatus() => _circuitBreaker.CheckFailedStatus();

  private readonly IEventClient _eventClient;
  CancellationTokenSource workerCancelTokenSource = new CancellationTokenSource();
  private readonly ILogger<Backends> _logger;
  private static readonly ProxyEvent staticEvent = new ProxyEvent() { Type = EventType.Backend };

  private Task? PollerTask;
  //public Backends(List<BackendHost> hosts, HttpClient client, int interval, int successRate)
  public Backends(
      IOptions<BackendOptions> options,
      ICircuitBreaker circuitBreaker,
      IHostHealthCollection backendHostCollection, //
      IHostApplicationLifetime appLifetime,               //
      IEventClient? eventClient,
      CancellationTokenSource cancellationTokenSource,    //
      ILogger<Backends> logger)
  {
    if (options == null) throw new ArgumentNullException(nameof(options));
    if (options.Value == null) throw new ArgumentNullException(nameof(options.Value));
    if (options.Value.Hosts == null) throw new ArgumentNullException(nameof(options.Value.Hosts));
    if (options.Value.Client == null) throw new ArgumentNullException(nameof(options.Value.Client));
    ArgumentNullException.ThrowIfNull(backendHostCollection, nameof(backendHostCollection));
    ArgumentNullException.ThrowIfNull(appLifetime, nameof(appLifetime));
    ArgumentNullException.ThrowIfNull(cancellationTokenSource, nameof(cancellationTokenSource));
    ArgumentNullException.ThrowIfNull(logger, nameof(logger));
    ArgumentNullException.ThrowIfNull(eventClient, nameof(eventClient));

    //    appLifetime.ApplicationStopping.Register(OnApplicationStopping);

    _eventClient = eventClient;
    _circuitBreaker = circuitBreaker;
    _backendHostCollection = backendHostCollection;
    _backendHosts = backendHostCollection.Hosts;
    _options = options.Value;
    _logger = logger;

    _cancellationTokenSource = cancellationTokenSource;
    _cancellationToken = _cancellationTokenSource.Token;


    var bo = options.Value; // Access the IBackendOptions instance

    _options = bo;
    _activeHosts = [];
    _successRate = bo.SuccessRate / 100.0;
    //_hosts = bo.Hosts;
    // FailureThreshold = bo.CircuitBreakerErrorThreshold;
    // FailureTimeFrame = bo.CircuitBreakerTimeslice;
    // allowableCodes = bo.AcceptableStatusCodes;

    _logger.LogDebug("[INIT] Backends service starting");

  }

  public Task Stop()
  {
    _logger.LogInformation("[SHUTDOWN] ⏹ Backend stopping");
    _cancellationTokenSource.Cancel();

    return PollerTask ?? Task.CompletedTask;
  }


  public void Start()
  {
    // Start the backend poller task
    PollerTask = Task.Run(() => Run(), _cancellationToken);

    // If OAuth is enabled, start token refresh

    _logger.LogInformation("[SERVICE] ✓ Backend service started");
  }


  #region Circuit Breaker
  // moved from Backends.cs to CircuitBreaker.cs

  #endregion

  public List<BaseHostHealth> GetActiveHosts() => _activeHosts;
  public int ActiveHostCount() => _activeHosts.Count;
  public List<BaseHostHealth> GetHosts() => _backendHosts;

  public List<BaseHostHealth> GetSpecificPathHosts()
  {
      return _backendHostCollection.SpecificPathHosts;
  }

  public List<BaseHostHealth> GetCatchAllHosts()
  {
      return _backendHostCollection.CatchAllHosts;
  }
  public async Task WaitForStartup(int timeout)
  {
    var start = DateTime.Now;
    var startTimer = DateTime.Now;

    // register all audiences with the token provider
    _backendHosts.ForEach(host => host.Config.RegisterWithTokenProvider());

    // Wait for the backend poller to start or until the timeout is reached. Make sure that if a token is required, it is available.
    var tasksToWait = _backendHosts.Select(host => host.Config.OAuth2Token()).ToArray();

    // await all tasks to complete or timeout after 10 seconds
    var allTasks = Task.WhenAll(tasksToWait);

    var delayTask = Task.Delay(timeout * 1000, _cancellationToken);
    var completedTask = await Task.WhenAny(allTasks, delayTask).ConfigureAwait(false);
    if (completedTask == delayTask)
    {
      _logger.LogError($"Backend Token Provider did not initialize tokens in the last {timeout} seconds.");
      throw new Exception("Backend Token Provider did not initialize tokens in time.");
    }

    _logger.LogInformation($"[SERVICE] ✓ Backend Poller started in {(DateTime.Now - start).TotalSeconds:F3}s");

  }

  private readonly Dictionary<string, bool> currentHostStatus = [];
  private async Task Run()
  {

    using (HttpClient _client = CreateHttpClient())
    {
      var intervalTime = TimeSpan.FromMilliseconds(_options.PollInterval).ToString(@"hh\:mm\:ss");
      var timeoutTime = TimeSpan.FromMilliseconds(_options.PollTimeout).ToString(@"hh\:mm\:ss\.fff");
      _logger.LogInformation($"[SERVICE] ✓ Backend Poller starting - Interval: {intervalTime} | Success Rate: {_successRate} | Timeout: {timeoutTime}");

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
            _logger.LogInformation("[SHUTDOWN] ⏹ Backend Poller stopping");
            break;
          }
          catch (Exception e)
          {
            _logger.LogError($"Backends: An unexpected error occurred: {e.Message}");
Console.WriteLine(e.StackTrace);
          }
        }
      }

      _logger.LogInformation("[SHUTDOWN] ✓ Backend Poller stopped");
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

  private async Task<bool> GetHostStatus(BaseHostHealth host, HttpClient client)
  {
    // Skip probing for non-probeable hosts
    if (!host.SupportsProbing)
    {
      // Non-probeable hosts are always considered healthy
      return true;
    }

    // Cast to probeable host to access probe-specific properties
    var probeableHost = (ProbeableHostHealth)host;

    double latency = 0;
    ProxyEvent probeData = new()
    {
      ["ProxyHost"] = _options.HostName,
      ["Backend-Host"] = host.Host,
      ["Port"] = host.Port.ToString(),
      ["Path"] = probeableHost.ProbePath,
      Type = EventType.Poller
    };

    try
    {
      if (_debug)
        staticEvent.WriteOutput($"Checking host {host.Url + probeableHost.ProbePath}");

      var request = new HttpRequestMessage(HttpMethod.Get, probeableHost.ProbeUrl);
      if (host.Config.UseOAuth)
      {
        string token = await host.Config.OAuth2Token().ConfigureAwait(false);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
      }

      var stopwatch = Stopwatch.StartNew();

      try
      {
        // send and read the entire response
        var response = await client.SendAsync(request, _cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(_cancellationToken);
        response.EnsureSuccessStatusCode();

        probeData["Code"] = response.StatusCode.ToString();
        _isRunning = true;

        // If the response is successful, add the host to the active hosts
        return response.IsSuccessStatusCode;
      }
      finally
      {
        stopwatch.Stop();
        latency = stopwatch.Elapsed.TotalMilliseconds;

        // Update the host with the new latency
        host.AddLatency(latency);
        probeData["Latency"] = latency.ToString() + " ms";
      }
    }
    catch (TaskCanceledException e)
    {
      probeData.Type = EventType.Exception;
      probeData.Exception = e;
      probeData["Code"] = "-";
      probeData["Timeout"] = client.Timeout.TotalMilliseconds.ToString();
    }
    catch (OperationCanceledException)
    {
      staticEvent.WriteOutput("Poller: Stopping the server.");
      throw; // Exit the loop
    }
    catch (Exception e)
    {
      probeData.Type = EventType.Exception;
      probeData.Exception = e;
      probeData["Code"] = "-";
      Console.WriteLine (e.StackTrace);
    }
    finally
    {
      probeData.SendEvent();
    }

    return false;
  }

  // Filter the active hosts based on the success rate
  private void FilterActiveHosts()
  {
    var newActiveHosts = _backendHosts
            .Where(h => h.SuccessRate() > _successRate)
            .Select(h =>
            {
              h.CalculatedAverageLatency = h.AverageLatency();
              return h;
            })
            .ToList();

    // Check if the host list actually changed before invalidating cache
    bool hostsChanged = !AreHostListsEqual(_activeHosts, newActiveHosts);

    _activeHosts = newActiveHosts;

    // Invalidate iterator cache only if hosts actually changed
    if (hostsChanged)
    {
      InvalidateIteratorCache();
    }
  }

  /// <summary>
  /// Compares two host lists to determine if they contain the same hosts.
  /// </summary>
  private bool AreHostListsEqual(List<BaseHostHealth> list1, List<BaseHostHealth> list2)
  {
    if (list1.Count != list2.Count)
      return false;

    var guids1 = new HashSet<Guid>(list1.Select(h => h.guid));
    var guids2 = new HashSet<Guid>(list2.Select(h => h.guid));

    return guids1.SetEquals(guids2);
  }

  public string HostStatus { get; set; } = "-";

  // Display the status of the hosts
  private void DisplayHostStatus()
  {
    ProxyEvent statusEvent = new ProxyEvent
    {
      Type = EventType.Backend,
      ["Timestamp"] = DateTime.UtcNow.ToString("o"),
      ["LoadBalanceMode"] = _options.LoadBalanceMode,
      ["ActiveHostsCount"] = ActiveHostCount().ToString(),
      ["SuccessRate"] = _successRate.ToString()
    };

    StringBuilder sb = new StringBuilder();
    sb.Append("\n\n============ Host Status =========\n");

    int txActivity = 0;
    int counter = 0;

    var statusIndicator = string.Empty;
    if (_backendHosts != null)
      foreach (var host in _backendHosts.OrderBy(h => h.AverageLatency()))
      {
        statusIndicator = host.SuccessRate() > _successRate ? "Good  " : "Errors";
        var roundedLatency = Math.Round(host.AverageLatency(), 3);
        var successRatePercentage = Math.Round(host.SuccessRate() * 100, 2);
        var hoststatus = host.GetStatus(out int calls, out int errors, out double average);

        counter++;
        txActivity += calls;
        txActivity += errors;

        sb.Append($"{statusIndicator} Host: {host.Url} Lat: {roundedLatency}ms Succ: {successRatePercentage}% {hoststatus}\n");

        statusEvent[$"{counter}-Host"] = host.ToString();
        statusEvent[$"{counter}-Latency"] = roundedLatency.ToString();
        statusEvent[$"{counter}-SuccessRate"] = successRatePercentage.ToString();
        statusEvent[$"{counter}-Calls"] = calls.ToString();
        statusEvent[$"{counter}-Errors"] = errors.ToString();
        statusEvent[$"{counter}-Average"] = average.ToString();
        statusEvent[$"{counter}-Status"] = statusIndicator;
      }

    statusEvent.SendEvent();

    _lastStatusDisplay = DateTime.Now;
    HostStatus = sb.ToString();

    //Console.WriteLine($"Total Transactions: {txActivity}   Time to go: {DateTime.Now - _lastGCTime}" );
    if (txActivity == 0 && (DateTime.Now - _lastGCTime).TotalSeconds > (60 * 15))
    {
      // Force garbage collection
      //Console.WriteLine("Running garbage collection");
      GC.Collect();
      GC.WaitForPendingFinalizers();
      _lastGCTime = DateTime.Now;
    }
  }

  // Fetches the OAuth2 Token as a seperate task. The token is fetched and updated 100ms before it expires. 

  public static string FormatMilliseconds(double milliseconds)
  {
    var timeSpan = TimeSpan.FromMilliseconds(milliseconds);
    return string.Format("{0:D2}:{1:D2}:{2:D2} {3:D3} milliseconds",
                         timeSpan.Hours,
                         timeSpan.Minutes,
                         timeSpan.Seconds,
                         timeSpan.Milliseconds);
  }



  // public IHostIterator GetHostIterator(
  //     string loadBalanceMode,
  //     IterationModeEnum mode = IterationModeEnum.SinglePass,
  //     int maxRetries = 1,
  //     string fullURL = "/")
  // {
  //   // Use the appropriate factory method based on iteration mode
  //   if (mode == IterationModeEnum.SinglePass)
  //   {
  //     return IteratorFactory.CreateSinglePassIterator(this, loadBalanceMode, fullURL);
  //   }
  //   else
  //   {
  //     return IteratorFactory.CreateMultiPassIterator(this, loadBalanceMode, maxRetries, fullURL);
  //   }
  // }

  // Add method to invalidate iterator cache when hosts change
  private void InvalidateIteratorCache()
  {
    IteratorFactory.InvalidateCache();
  }

}
