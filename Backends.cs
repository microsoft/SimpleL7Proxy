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



// This code has 3 objectives:  
// * Check the status of each backend host and measure its latency
// * Filter the active hosts based on the success rate
// * Fetch the OAuth2 token and refresh it 100ms minutes before it expires
public class Backends : IBackendService
{
    private List<BackendHost> _hosts;
    private List<BackendHost> _activeHosts;

    private BackendOptions _options;
    private static bool _debug=false;

    private static double _successRate;
    private static DateTime _lastStatusDisplay = DateTime.Now;
    private static DateTime _lastGCTime = DateTime.Now;
    private static bool _isRunning = false;
    private CancellationToken _cancellationToken;

    private Azure.Core.AccessToken? AuthToken { get; set; }

    //public Backends(List<BackendHost> hosts, HttpClient client, int interval, int successRate)
    public Backends(IOptions<BackendOptions> options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (options.Value == null) throw new ArgumentNullException(nameof(options.Value));
        if (options.Value.Hosts == null) throw new ArgumentNullException(nameof(options.Value.Hosts));
        if (options.Value.Client == null) throw new ArgumentNullException(nameof(options.Value.Client));

        var bo = options.Value; // Access the IBackendOptions instance

        _hosts = bo.Hosts;
        _options = bo;
        _activeHosts = new List<BackendHost>();
        _successRate = bo.SuccessRate / 100.0;
    }

    public void Start(CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        Task.Run(() => Run());

        if (_options.UseOAuth)
        {
            GetToken();
        }
    }   

    public List<BackendHost> GetActiveHosts()
    {
        return _activeHosts;
    }

    public string OAuth2Token() {
        while (AuthToken?.ExpiresOn < DateTime.UtcNow)
        {
            Task.Delay(100).Wait();
        }
        return AuthToken?.Token ?? "";
    }

    public async Task waitForStartup(int timeout)
    {
        var start = DateTime.Now;
        for (int i=0; i < 10; i++ ) 
        {
            var startTimer = DateTime.Now;
            // Wait for the backend poller to start or until the timeout is reached. Make sure that if a token is required, it is available.
            while (!_isRunning && 
                  (!_options.UseOAuth || AuthToken?.Token != "" ) && 
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
    private async Task Run() {

        using (HttpClient _client = CreateHttpClient()) {
            var intervalTime = TimeSpan.FromMilliseconds(_options.PollInterval).ToString(@"hh\:mm\:ss");
            var timeoutTime = TimeSpan.FromMilliseconds(_options.PollTimeout).ToString(@"hh\:mm\:ss\.fff");
            Console.WriteLine($"Starting Backend Poller: Interval: {intervalTime}, SuccessRate: {_successRate}, Timeout: {timeoutTime}");

            _client.Timeout = TimeSpan.FromMilliseconds(_options.PollTimeout);

            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken)) {
                while (!linkedCts.Token.IsCancellationRequested) {
                    try {
                        await UpdateHostStatus(_client);
                        FilterActiveHosts();

                        if ((DateTime.Now - _lastStatusDisplay).TotalSeconds > 60) {
                            DisplayHostStatus();
                        }
                    } catch (OperationCanceledException) {
                        Console.WriteLine("Operation was canceled. Stopping the server.");
                        break;
                    } catch (Exception e) {
                        Console.WriteLine($"An unexpected error occurred: {e.Message}");
                    }

                    await Task.Delay(_options.PollInterval, linkedCts.Token);
                }
            }
        }
    }

    private HttpClient CreateHttpClient() {
        if (Environment.GetEnvironmentVariable("IgnoreSSLCert")?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true) {
            var handler = new HttpClientHandler {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
            };
            return new HttpClient(handler);
        } else {
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

        foreach (var host in _hosts )
        {
            var currentStatus = await GetHostStatus(host, _client);
            bool statusChanged = !currentHostStatus.ContainsKey(host.host) || currentHostStatus[host.host] != currentStatus;

            currentHostStatus[host.host] = currentStatus;
            host.AddCallSuccess(currentStatus);

            if (statusChanged)
            {
                _statusChanged = true;
            }
        }

        return _statusChanged;
    }

    private async Task<bool> GetHostStatus(BackendHost host, HttpClient client)
    {
        if (_debug)
            Console.WriteLine($"Checking host {host.url + host.probe_path}");

        var request = new HttpRequestMessage(HttpMethod.Get, host.probeurl);
        if (_options.UseOAuth)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", OAuth2Token());
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {

            var response = await client.SendAsync(request, _cancellationToken);
            stopwatch.Stop();
            var latency = stopwatch.Elapsed.TotalMilliseconds;

            // Update the host with the new latency
            host.AddLatency(latency);

            response.EnsureSuccessStatusCode();

            _isRunning = true;

            // If the response is successful, add the host to the active hosts
            return response.IsSuccessStatusCode;
        }
        catch (UriFormatException e) {
            Program.telemetryClient?.TrackException(e);
            Console.WriteLine($"Poller: Could not check probe: {e.Message}");
        }
        catch (System.Threading.Tasks.TaskCanceledException) {
            Console.WriteLine($"Poller: Host Timeout: {host.host}");
        }
        catch (HttpRequestException e) {
            Program.telemetryClient?.TrackException(e);
            Console.WriteLine($"Poller: Host {host.host} is down with exception: {e.Message}");
        }
        catch (OperationCanceledException) {
            // Handle the cancellation request (e.g., break the loop, log the cancellation, etc.)
            Console.WriteLine("Poller: Operation was canceled. Stopping the server.");
            throw; // Exit the loop
        }
        catch (System.Net.Sockets.SocketException e) {
            Console.WriteLine($"Poller: Host {host.host} is down:  {e.Message}");
        }
        catch (Exception e) {
            Program.telemetryClient?.TrackException(e);
            Console.WriteLine($"Poller: Error: {e.Message}");
        }

        return false;
    }

    // Filter the active hosts based on the success rate
    private void FilterActiveHosts()
    {
        _activeHosts = _hosts
            .Where(h => h.SuccessRate() > _successRate)
            .OrderBy(h => h.AverageLatency())
            .ToList();
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
        StringBuilder   sb = new StringBuilder();
        sb.Append("\n\n============ Host Status =========\n");

        int txActivity=0;

        if (_hosts != null )
            foreach (var host in _hosts )
            {
                string statusIndicator = host.SuccessRate() > _successRate ? "Good  " : "Errors";
                double roundedLatency = Math.Round(host.AverageLatency(), 3);
                double successRatePercentage = Math.Round(host.SuccessRate() * 100, 2);

                string hoststatus=host.GetStatus(out int calls, out int errors, out double average);
                txActivity += calls;
                txActivity += errors;

                sb.Append($"{statusIndicator} Host: {host.url} Lat: {roundedLatency}ms Succ: {successRatePercentage}% {hoststatus}\n");
            }


        _lastStatusDisplay = DateTime.Now;
        _hostStatus = sb.ToString();
        Console.WriteLine(_hostStatus);

        //Console.WriteLine($"Total Transactions: {txActivity}   Time to go: {DateTime.Now - _lastGCTime}" );
        if (txActivity == 0 && (DateTime.Now - _lastGCTime).TotalSeconds > (60*15) )
        {
            // Force garbage collection
            //Console.WriteLine("Running garbage collection");
            GC.Collect();
            GC.WaitForPendingFinalizers();
            _lastGCTime = DateTime.Now;
        }
    }

    
    // Fetches the OAuth2 Token as a seperate task. The token is fetched and updated 100ms before it expires. 
    public void GetToken() {
        Task.Run(async () => { 
            try { 
                // Loop until a cancellation is requested
                while (!_cancellationToken.IsCancellationRequested) {
                    // Fetch the authentication token asynchronously
                    AuthToken = await GetTokenAsync();

                    if (AuthToken.HasValue)
                    {
                        var timeout =(AuthToken?.ExpiresOn - DateTimeOffset.UtcNow).Value.TotalMilliseconds;

                        if ( timeout < 500 )
                        {
                            Console.WriteLine($"Auth Token is about to expire. Retrying in {timeout} ms.");
                            await Task.Delay((int)timeout, _cancellationToken);
                        } else {
                            // Calculate the time to refresh the token, 100 ms before it expires
                            var refreshTime = timeout - 100;
                            Console.WriteLine($"Auth Token expires on: {AuthToken?.ExpiresOn} Refresh in: {FormatMilliseconds(refreshTime)} (100 ms grace)");
                            // Wait for the calculated refresh time or until a cancellation is requested
                            await Task.Delay((int)refreshTime, _cancellationToken);
                        }
                    }
                    else
                    {
                        // Handle the case where the token is null
                        Console.WriteLine("Auth Token is null. Retrying in 10 seconds.");
                        await Task.Delay(TimeSpan.FromMilliseconds(10000), _cancellationToken);
                    }

                }
            } 
            catch (OperationCanceledException) {
                // Handle the cancellation request (e.g., break the loop, log the cancellation, etc.)
                Console.WriteLine("Exiting fetching Auth Token: Operation was canceled.");
            }   
            catch (Exception e) {
                // Handle any unexpected errors that occur during token fetching
                Console.WriteLine($"An unexpected error occurred while fetching Auth Token: {e.Message}");
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
            var credential = new DefaultAzureCredential();
            var context = new TokenRequestContext(new[] { _options.OAuthAudience });
            var token = await credential.GetTokenAsync(context);

            return token;
        }
        catch (AuthenticationFailedException ex)
        {
            Console.WriteLine($"Authentication failed: {ex.Message}");
            // Handle the exception as needed, e.g., return a default value or rethrow the exception
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An unexpected error occurred: {ex.Message}");
            // Handle other potential exceptions
            throw;
        }
    }
}