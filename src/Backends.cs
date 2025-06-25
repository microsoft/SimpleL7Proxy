using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using System.Text;
using Azure.Identity;
using Azure.Core;
using System.Security.AccessControl;
using System.Text.Json;
using System.Collections.Concurrent;



// This code has 3 objectives:  
// * Check the status of each backend host and measure its latency
// * Filter the active hosts based on the success rate
// * Fetch the OAuth2 token and refresh it 100ms minutes before it expires
public class Backends : IBackendService
{
    public List<BackendHost> _hosts { get; set; }
    private List<BackendHost> _activeHosts;

    private BackendOptions _options;
    private static bool _debug = false;

    private static double _successRate;
    private static DateTime _lastStatusDisplay = DateTime.Now - TimeSpan.FromMinutes(10);  // Force display on first run
    private static DateTime _lastGCTime = DateTime.Now;
    private static bool _isRunning = false;
    private CancellationToken _cancellationToken;

    private Azure.Core.AccessToken? AuthToken { get; set; }
    private readonly IEventHubClient? _eventHubClient;
    CancellationTokenSource workerCancelTokenSource = new CancellationTokenSource();
    private ProxyEvent staticEvent = new() { Type = EventType.Backend }; 


    //public Backends(List<BackendHost> hosts, HttpClient client, int interval, int successRate)
    public Backends(IOptions<BackendOptions> options, IEventHubClient? eventHubClient)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (options.Value == null) throw new ArgumentNullException(nameof(options.Value));
        if (options.Value.Hosts == null) throw new ArgumentNullException(nameof(options.Value.Hosts));
        if (options.Value.Client == null) throw new ArgumentNullException(nameof(options.Value.Client));

        _eventHubClient = eventHubClient;


        var bo = options.Value; // Access the IBackendOptions instance

        _hosts = bo.Hosts;
        _options = bo;
        _activeHosts = new List<BackendHost>();
        _successRate = bo.SuccessRate / 100.0;
        FailureThreshold = bo.CircuitBreakerErrorThreshold;
        FailureTimeFrame = bo.CircuitBreakerTimeslice;
        allowableCodes = bo.AcceptableStatusCodes;
    }

    public Task Start()
    {
        _cancellationToken = workerCancelTokenSource.Token;

        var T = Task.Run(() => Run());

        if (_options.UseOAuth)
        {
            GetToken();
        }

        return T;
    }

    public void Stop()
    {
        workerCancelTokenSource.Cancel();
    }

    List<DateTime> hostFailureTimes = new List<DateTime>();
    ConcurrentQueue<DateTime> hostFailureTimes2 = new ConcurrentQueue<DateTime>();
    private readonly int FailureThreshold = 5;
    private readonly int FailureTimeFrame = 10; // seconds
    static int[] allowableCodes = { 200, 401, 403, 408, 410, 412, 417, 400 };

    public List<BackendHost> GetActiveHosts()
    {
        return _activeHosts;
    }
    public int ActiveHostCount()
    {
        return _activeHosts.Count;
    }

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
        ProxyEvent logerror = new ProxyEvent()
        {
            ["Code"] = code.ToString(),
            ["Time"] = now.ToString(),
            ["WasException"] = wasException.ToString(),
            ["Count"] = hostFailureTimes2.Count.ToString(),
            Type = EventType.CircuitBreakerError
        };

        logerror.SendEvent();
    }

    // returns true if the service is in failure state
    public bool CheckFailedStatus()
    {
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
                Console.WriteLine($"Backend Poller did not start in the last {timeout} seconds.");
            }
            else
            {
                Console.WriteLine($"Backend Poller started in {(DateTime.Now - start).TotalSeconds} seconds.");
                return;
            }
        }
        throw new Exception("Backend Poller did not start in time.");
    }

    Dictionary<string, bool> currentHostStatus = new Dictionary<string, bool>();
    private async Task Run()
    {

        using (HttpClient _client = CreateHttpClient())
        {
            var intervalTime = TimeSpan.FromMilliseconds(_options.PollInterval).ToString(@"hh\:mm\:ss");
            var timeoutTime = TimeSpan.FromMilliseconds(_options.PollTimeout).ToString(@"hh\:mm\:ss\.fff");
            Console.WriteLine($"Starting Backend Poller: Interval: {intervalTime}, SuccessRate: {_successRate}, Timeout: {timeoutTime}");

            _client.Timeout = TimeSpan.FromMilliseconds(_options.PollTimeout);

            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken))
            {
                while (!linkedCts.Token.IsCancellationRequested && _cancellationToken.IsCancellationRequested == false)
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
                        Console.WriteLine("Operation was canceled. Stopping the backend poller task.");
                        break; ;
                    }
                    catch (Exception e)
                    {
                        Console.Error.WriteLine($"An unexpected error occurred: {e.Message}");
                    }
                }
            }

            Console.WriteLine("Backend Poller stopped.");
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

        if (_hosts == null)
        {
            return _statusChanged;
        }

        foreach (var host in _hosts)
        {
            host.ResetStatus();
            var currentStatus = await GetHostStatus(host, _client);
            bool statusChanged = !currentHostStatus.ContainsKey(host.host) || currentHostStatus[host.host] != currentStatus;

            currentHostStatus[host.host] = currentStatus;
            host.AddCallSuccess(currentStatus);

            if (statusChanged)
            {
                _statusChanged = true;
            }
        }

        if (_debug)
            staticEvent.WriteOutput("Returning status changed: " + _statusChanged);

        return _statusChanged;
    }

    private async Task<bool> GetHostStatus(BackendHost host, HttpClient client)
    {
        double latency = 0;
        ProxyEvent probeData = new()
        {
            ["ProxyHost"] = _options.HostName,
            ["Backend-Host"] = host.host,
            ["Port"] = host.port.ToString(),
            ["Path"] = host.probe_path,
            Type = EventType.Poller
        };

        try
        {

            if (_debug)
                staticEvent.WriteOutput($"Checking host {host.url + host.probe_path}");


            var request = new HttpRequestMessage(HttpMethod.Get, host.probeurl);
            if (_options.UseOAuth)
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", OAuth2Token());
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
        catch (UriFormatException e)
        {
            // WriteOutput($"Poller: Could not check probe: {e.Message}");
            probeData.Type = EventType.Exception;
            probeData.Exception = e;
            //"S7P-Uri Format Exception";
            probeData["Code"] = "-";
        }
        catch (System.Threading.Tasks.TaskCanceledException e)
        {
            // WriteOutput($"Poller: Host Timeout: {host.host}");
            probeData.Type = EventType.Exception;
            probeData.Exception = e;
            probeData["Code"] = "-";
            probeData["Timeout"] = client.Timeout.TotalMilliseconds.ToString();
        }
        catch (HttpRequestException e)
        {
            // WriteOutput($"Poller: Host {host.host} is down with exception: {e.Message}");
            probeData.Type = EventType.Exception;
            probeData.Exception = e;
            probeData["Code"] = "-";
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
            probeData.Type = EventType.Exception;
            probeData.Exception = e;
            probeData["Code"] = "-";
        }
        catch (Exception e)
        {
            // Program.telemetryClient?.TrackException(e);
            // WriteErrorOutput($"Poller: Error: {e.Message}");
            probeData.Type = EventType.Exception;
            probeData.Exception = e;
            probeData["Code"] = "-";
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
        var hosts = _hosts
                .Where(h => h.SuccessRate() > _successRate)
                .Select(h =>
                {
                    h.calculatedAverageLatency = h.AverageLatency();
                    return h;
                });

        switch (_options.LoadBalanceMode)
        {
            case Constants.Latency:
                _activeHosts = hosts.OrderBy(h => h.calculatedAverageLatency).ToList();
                break;
            case Constants.RoundRobin:
                _activeHosts = hosts.ToList();  // roundrobin is handled in proxyWroker
                break;
            default:
                _activeHosts = hosts.OrderBy(_ => Guid.NewGuid()).ToList();

                break;
        }
    }

    public string _hostStatus { get; set; } = "-";

    public string HostStatus()
    {
        // Implementation here
        return _hostStatus;
    }
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

        if (_hosts != null)
            foreach (var host in _hosts)
            {
                string statusIndicator = host.SuccessRate() > _successRate ? "Good  " : "Errors";
                double roundedLatency = Math.Round(host.AverageLatency(), 3);
                double successRatePercentage = Math.Round(host.SuccessRate() * 100, 2);
                counter++;

                string hoststatus = host.GetStatus(out int calls, out int errors, out double average);
                txActivity += calls;
                txActivity += errors;

                sb.Append($"{statusIndicator} Host: {host.url} Lat: {roundedLatency}ms Succ: {successRatePercentage}% {hoststatus}\n");

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
        _hostStatus = sb.ToString();

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
                            staticEvent.WriteOutput($"Auth Token is about to expire. Retrying in {timeout} ms.");
                            await Task.Delay((int)timeout, _cancellationToken);
                        }
                        else
                        {
                            // Calculate the time to refresh the token, 100 ms before it expires
                            var refreshTime = timeout - 100;
                            staticEvent.WriteOutput($"Auth Token expires on: {AuthToken.Value.ExpiresOn} Refresh in: {FormatMilliseconds(refreshTime)} (100 ms grace)");
                            // Wait for the calculated refresh time or until a cancellation is requested
                            await Task.Delay((int)refreshTime, _cancellationToken);
                        }
                    }
                    else
                    {
                        // Handle the case where the token is null
                        staticEvent.WriteOutput("Auth Token is null. Retrying in 10 seconds.");
                        await Task.Delay(TimeSpan.FromMilliseconds(10000), _cancellationToken);
                    }

                }
            }
            catch (OperationCanceledException e)
            {
                ProxyEvent logEvent = new ProxyEvent
                {
                    Type = EventType.Exception,
                    Exception = e,
                    ["Message"] = "Auth Token fetching operation was canceled.",
                    ["OAuthAudience"] = _options.OAuthAudience
                };
                logEvent.SendEvent();
            }
            catch (Exception e)
            {
                ProxyEvent logEvent = new ProxyEvent
                {
                    Type = EventType.Exception,
                    Exception = e,
                    ["Message"] = $"An error occurred while fetching Auth Token: {e.Message}",
                    ["OAuthAudience"] = _options.OAuthAudience
                };
                logEvent.SendEvent();

            }
        }, _cancellationToken);
    }

    public static string FormatMilliseconds(double milliseconds)
    {
        TimeSpan timeSpan = TimeSpan.FromMilliseconds(milliseconds);
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
            var options = new DefaultAzureCredentialOptions();

            if (_options.UseOAuthGov == true)
            {
                options.AuthorityHost = AzureAuthorityHosts.AzureGovernment;
                //options = new DefaultAzureCredentialOptions { AuthorityHost = AzureAuthorityHosts.AzureGovernment };
            }

            var credential = new DefaultAzureCredential(options);
            var context = new TokenRequestContext(new[] { _options.OAuthAudience });
            var token = await credential.GetTokenAsync(context);

            return token;
        }
        catch (AuthenticationFailedException ex)
        {
            var logEvent = new ProxyEvent
            {
                Type = EventType.Exception,
                Exception = ex,
                ["Message"] = $"Authentication failed: {ex.Message}",
                ["OAuthAudience"] = _options.OAuthAudience
            };
            logEvent.SendEvent();
            // Handle the exception as needed, e.g., return a default value or rethrow the exception
            throw;
        }
        catch (Exception ex)
        {
            ProxyEvent logEvent = new ProxyEvent
            {
                Type = EventType.Exception,
                Exception = ex,
                ["Message"] = $"An unexpected error occurred while fetching the token: {ex.Message}",
                ["OAuthAudience"] = _options.OAuthAudience
            };
            logEvent.SendEvent();

            // Handle other potential exceptions
            throw;
        }
    }
}
