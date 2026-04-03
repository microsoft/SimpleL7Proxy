using System.Diagnostics;
using Microsoft.Extensions.Options;
using System.Text;
using Azure.Core;
using System.Text.Json;
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
  private List<BaseHostHealth> _activeHosts;
  private readonly IHostHealthCollection _backendHostCollection;

  /// <summary>
  /// All registered hosts from the current snapshot.
  /// Always reads the latest snapshot — safe for concurrent access.
  /// </summary>
  private List<BaseHostHealth> _backendHosts => _backendHostCollection.Current.Hosts;

  private readonly ProxyConfig _options;
  private static readonly bool _debug = false;

  private static double _successRate;
  private static DateTime _lastStatusDisplay = DateTime.Now - TimeSpan.FromMinutes(10);  // Force display on first run
  private static DateTime _lastGCTime = DateTime.Now;
  private readonly ICircuitBreaker _circuitBreaker;

  private CancellationTokenSource _cancellationTokenSource;
  private CancellationToken _cancellationToken;

  public Task<bool> CheckFailedStatusAsync(bool nosleep=false) => _circuitBreaker.CheckFailedStatusAsync(nosleep);

  private readonly IEventClient _eventClient;
  private readonly ISharedIteratorRegistry? _sharedIteratorRegistry;
  private List<Guid> _lastLatencyOrder = new();

  // Reusable ProxyEvent instances for backend poller to reduce allocations
  private readonly ProxyEvent _statusEvent = new ProxyEvent(25);  // 4 fixed (Timestamp, LoadBalanceMode, ActiveHostsCount, SuccessRate) + 7*N per host (assumes ~3 hosts)
  private readonly ProxyEvent _probeEvent = new ProxyEvent(6);  // ProxyHost, Backend-Host, Port, Path, Code, Latency/Timeout


  CancellationTokenSource workerCancelTokenSource = new CancellationTokenSource();
  private readonly ILogger<Backends> _logger;
  private static readonly ProxyEvent staticEvent = new ProxyEvent() { Type = EventType.Backend };

  private Task? PollerTask;
  //public Backends(List<BackendHost> hosts, HttpClient client, int interval, int successRate)
  public Backends(
      IOptions<ProxyConfig> options,
      ICircuitBreaker circuitBreaker,
      IHostHealthCollection backendHostCollection, //
      IHostApplicationLifetime appLifetime,               //
      IEventClient? eventClient,
      CancellationTokenSource cancellationTokenSource,    //
      ILogger<Backends> logger,
      ISharedIteratorRegistry? sharedIteratorRegistry = null)
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
    ArgumentNullException.ThrowIfNull(circuitBreaker, nameof(circuitBreaker));

    //    appLifetime.ApplicationStopping.Register(OnApplicationStopping);

    _eventClient = eventClient;
    _circuitBreaker = circuitBreaker;
    _sharedIteratorRegistry = sharedIteratorRegistry;
    _backendHostCollection = backendHostCollection;
    _options = options.Value;
    _logger = logger;

    _cancellationTokenSource = cancellationTokenSource;
    _cancellationToken = _cancellationTokenSource.Token;


    var bo = options.Value; // Access the IBackendOptions instance

    _options = bo;
    _activeHosts = [];
    _successRate = bo.SuccessRate / 100.0;

    // Hosts are staged and activated by ConfigBootstrapper.RegisterBackends

    _logger.LogDebug("[INIT] Backend health-polling service created");

  }

  public Task Stop()
  {
    _logger.LogInformation("[SHUTDOWN] ⏹ Backend health poller stopping");
    _cancellationTokenSource.Cancel();

    return PollerTask ?? Task.CompletedTask;
  }


  public void Start()
  {
    // Start the backend poller task
    PollerTask = Task.Run(() => Run(), _cancellationToken);
    PollerTask.ContinueWith(task =>
    {
      if (task.Exception != null)
      {
        _logger.LogError(task.Exception, "[SERVICE] ✗ Backend health poller task faulted {Time} {Exception}", DateTime.UtcNow, task.Exception.Flatten());
      }
    }, TaskContinuationOptions.OnlyOnFaulted);
  }


  #region Circuit Breaker
  // moved from Backends.cs to CircuitBreaker.cs

  #endregion

  public List<BaseHostHealth> GetActiveHosts() => _activeHosts;
  public int ActiveHostCount() => _activeHosts.Count;
  public List<BaseHostHealth> GetHosts() => _backendHosts;

  public List<BaseHostHealth> GetSpecificPathHosts()
  {
    return _backendHostCollection.Current.SpecificPathHosts;
  }

  public List<BaseHostHealth> GetCatchAllHosts()
  {
    return _backendHostCollection.Current.CatchAllHosts;
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
      // Console.WriteLine($"[BACKENDS-STARTUP-ERROR] {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} Backend Token Provider did not initialize tokens in the last {timeout} seconds.");
      _logger.LogError("Backend Token Provider did not initialize tokens in the last {Timeout} seconds.", timeout);
      throw new Exception("Backend Token Provider did not initialize tokens in time.");
    }
  }

  private readonly Dictionary<string, bool> currentHostStatus = [];
  private async Task Run()
  {
    try
    {
      using var _client = CreateHttpClient();
      var intervalTime = TimeSpan.FromMilliseconds(_options.PollInterval).ToString(@"hh\:mm\:ss");
      var timeoutTime = TimeSpan.FromMilliseconds(_options.PollTimeout).ToString(@"hh\:mm\:ss\.fff");
      _logger.LogInformation($"[SERVICE] ✓ Backend health poller started — polling every {intervalTime}, healthy threshold: {_successRate}, probe timeout: {timeoutTime}");

      _client.Timeout = TimeSpan.FromMilliseconds(_options.PollTimeout);

      using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.PollInterval));
      // Run first poll immediately, then wait on timer
      bool firstRun = true;
      while (!_cancellationToken.IsCancellationRequested)
      {
        try
        {
          if (!firstRun)
          {
            if (!await timer.WaitForNextTickAsync(_cancellationToken).ConfigureAwait(false))
              break;
          }
          firstRun = false;

          await UpdateHostStatus(_client);
          FilterActiveHosts();

          if ((DateTime.Now - _lastStatusDisplay).TotalSeconds > 60)
          {
            DisplayHostStatus();
          }
        }
        catch (OperationCanceledException)
        {
          _logger.LogInformation("[SHUTDOWN] ⏹ Backend health poller cancelled — draining");
          break;
        }
        catch (Exception e)
        {
          _logger.LogError(e, "[SERVICE] ⚠ Backend health poller hit an error — retrying next cycle");
        }
      }

      _logger.LogInformation("[SHUTDOWN] ✓ Backend health poller stopped");
    }
    catch (Exception ex)
    {
      // Catch any unhandled exceptions to prevent background service from crashing the host
      _logger.LogError(ex, "[SERVICE] ✗ Backend health poller crashed — service stopping");
      throw; // Rethrow to let the host know the background service failed, but at least we logged it
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
    _probeEvent.Clear();
    _probeEvent["ProxyHost"] = _options.HostName;
    _probeEvent["Backend-Host"] = host.Host;
    _probeEvent["Port"] = host.Port.ToString();
    _probeEvent["Path"] = probeableHost.ProbePath;
    _probeEvent.Type = EventType.Poller;

    try
    {
      if (_debug)
      {
        string eventMessage = $"Checking host: {host.Host} Hostname: {host.Hostname}  Probe: {probeableHost.ProbeUrl}";
        staticEvent.WriteOutput(eventMessage);
      }

      using var request = new HttpRequestMessage(HttpMethod.Get, probeableHost.ProbeUrl);
      if (host.Config.UseOAuth)
      {
        string token = await host.Config.OAuth2Token().ConfigureAwait(false);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
      }

      var stopwatch = Stopwatch.StartNew();

      try
      {
        // send and read the entire response
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cancellationToken);

        _probeEvent["Code"] = response.StatusCode.ToString();

        // CRITICAL: Drain the response body to allow connection reuse
        // Without this, undrained responses accumulate and leak memory
        if (response.Content != null)
        {
          await response.Content.ReadAsByteArrayAsync(_cancellationToken);
        }

        // If the response is successful, add the host to the active hosts
        _probeEvent["Success"] = response.IsSuccessStatusCode.ToString();
        return response.IsSuccessStatusCode;
      }
      finally
      {
        stopwatch.Stop();
        latency = stopwatch.Elapsed.TotalMilliseconds;

        // Update the host with the new latency
        host.AddLatency(latency);
        _probeEvent["Latency"] = latency.ToString() + " ms";
      }
    }
    catch (UriFormatException e)
    {
      // WriteOutput($"Poller: Could not check probe: {e.Message}");
      _probeEvent.Type = EventType.Exception;
      _probeEvent.Exception = e;
      //"S7P-Uri Format Exception";
      _probeEvent["Code"] = "-";
    }
    catch (System.Threading.Tasks.TaskCanceledException)
    {
      // Probe timeout is a normal operational signal — host is slow/down.
      // Not an exception; the circuit breaker handles it via AddCallSuccess(false).
      _probeEvent.Type = EventType.Poller;
      _probeEvent["Code"] = "Timeout";
      _probeEvent["Timeout"] = client.Timeout.TotalMilliseconds.ToString();
 

      _circuitBreaker.TrackStatus(408, true, "ProbeTimeout");
    }
    catch (HttpRequestException e)
    {
      // WriteOutput($"Poller: Host {host.host} is down with exception: {e.Message}");
      _probeEvent.Type = EventType.Exception;
      _probeEvent.Exception = e;
      _probeEvent["Code"] = "-";
    }
    catch (OperationCanceledException)
    {
      // Handle the cancellation request (e.g., break the loop, log the cancellation, etc.)
      staticEvent.WriteOutput("Poller: Stopping the server.");
      throw; // Exit the loop
    }
    catch (System.Net.Sockets.SocketException e)
    {
      // WriteOutput($"Poller: Host {host.host} is down:  {e.Message}");
      _probeEvent.Type = EventType.Exception;
      _probeEvent.Exception = e;
      _probeEvent["Code"] = "-";
    }
    catch (Exception e)
    {
      // Program.telemetryClient?.TrackException(e);
      // WriteErrorOutput($"Poller: Error: {e.Message}");
      _probeEvent.Type = EventType.Exception;
      _probeEvent.Exception = e;
      _probeEvent["Code"] = "-";
    }
    finally
    {
      _probeEvent.SendEvent();
    }

    return false;
  }

  // Filter the active hosts based on the success rate
  private void FilterActiveHosts()
  {
    var newActiveHosts = _backendHosts
            .Where(h => h.SuccessRate() >= _successRate)
            .Select(h =>
            {
              h.CalculatedAverageLatency = h.AverageLatency();
              return h;
            })
            .ToList();

    // Check if the host list actually changed before invalidating cache
    bool hostsChanged = !AreHostListsEqual(_activeHosts, newActiveHosts);

    _activeHosts = newActiveHosts;

    if (hostsChanged)
    {
      InvalidateIteratorCache();
      _lastLatencyOrder = newActiveHosts.OrderBy(h => h.CalculatedAverageLatency).Select(h => h.guid).ToList();
    }
    else if (string.Equals(_options.LoadBalanceMode, Constants.Latency, StringComparison.OrdinalIgnoreCase))
    {
      // Only invalidate shared iterators when the latency-based ordering actually changed
      var newOrder = newActiveHosts.OrderBy(h => h.CalculatedAverageLatency).Select(h => h.guid).ToList();
      if (!newOrder.SequenceEqual(_lastLatencyOrder))
      {
        _sharedIteratorRegistry?.InvalidateAll();
        _lastLatencyOrder = newOrder;
      }
    }
    // For roundrobin/random modes with unchanged host list, no invalidation needed
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
    // Reuse and clear the status event instance
    _statusEvent.Clear();
    _statusEvent.Type = EventType.Backend;
    _statusEvent["Timestamp"] = DateTime.UtcNow.ToString("o");
    _statusEvent["LoadBalanceMode"] = _options.LoadBalanceMode;
    _statusEvent["ActiveHostsCount"] = ActiveHostCount().ToString();
    _statusEvent["SuccessRate"] = _successRate.ToString();

    StringBuilder sb = new StringBuilder();
    sb.Append("\n\n============ Host Status =========\n");

    int txActivity = 0;
    int counter = 0;

    var statusIndicator = string.Empty;
    if (_backendHosts != null)
      foreach (var host in _backendHosts.OrderBy(h => h.AverageLatency()))
      {
        statusIndicator = host.SuccessRate() >= _successRate ? "✓ Active" : "✗ Below threshold";
        var roundedLatency = Math.Round(host.AverageLatency(), 3);
        var successRatePercentage = Math.Round(host.SuccessRate() * 100, 2);
        var hoststatus = host.GetStatus(out int calls, out int errors, out double average);

        counter++;
        txActivity += calls;
        txActivity += errors;

        sb.Append($"{statusIndicator} Host: {host.Url} Lat: {roundedLatency}ms Succ: {successRatePercentage}% {hoststatus}\n");

        _statusEvent[$"{counter}-Host"] = host.ToString();
        _statusEvent[$"{counter}-Latency"] = roundedLatency.ToString();
        _statusEvent[$"{counter}-SuccessRate"] = successRatePercentage.ToString();
        _statusEvent[$"{counter}-Calls"] = calls.ToString();
        _statusEvent[$"{counter}-Errors"] = errors.ToString();
        _statusEvent[$"{counter}-Average"] = average.ToString();
        _statusEvent[$"{counter}-Status"] = statusIndicator;
      }

    _statusEvent.SendEvent();

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
    
    // Also invalidate shared iterators so they get fresh latency ordering
    _sharedIteratorRegistry?.InvalidateAll();
  }

}
