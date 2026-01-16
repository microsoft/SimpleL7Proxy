using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using System.Net;
using System.Threading;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;

using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Proxy;
using SimpleL7Proxy.Config;
using Shared.HealthProbe;
using SimpleL7Proxy.Queue;

namespace SimpleL7Proxy;

/// <summary>
/// Standalone probe server using Kestrel on port 9000.
/// Provides health check endpoints for Kubernetes/container orchestration.
/// </summary>
public class ProbeServer : BackgroundService
{
    private readonly IBackendService _backends;
    private readonly ILogger<ProbeServer> _logger;
    private readonly HealthCheckService _healthService;

    private static HealthStatusEnum _readinessStatus = HealthStatusEnum.ReadinessZeroHosts;
    private static HealthStatusEnum _startupStatus = HealthStatusEnum.StartupZeroHosts;


    // Active snapshots published to readers (use Volatile.Read/Write for memory ordering)

    private Timer? _probeTimer;
    private readonly BackendOptions _backendOptions;

    static readonly byte[] s_okBytes = Encoding.UTF8.GetBytes("OK\n");
    static readonly int s_okLength = s_okBytes.Length; 
    private static readonly byte[] s_zeroHosts = Encoding.UTF8.GetBytes("Not Healthy.  Active Hosts: 0\n");
    private static readonly int s_zeroHostsLength = s_zeroHosts.Length;
    private static readonly byte[] s_failedHosts = Encoding.UTF8.GetBytes("Not Healthy.  Failed Hosts: True\n");
    private static readonly int s_failedHostsLength = s_failedHosts.Length;
    public static HealthStatusEnum ReadinessStatus = HealthStatusEnum.ReadinessZeroHosts;
    public static HealthStatusEnum StartupStatus = HealthStatusEnum.StartupZeroHosts;

    private static int FailedAttempts = 0;
    public ProbeServer(IBackendService backends, HealthCheckService healthService, ILogger<ProbeServer> logger, IOptions<BackendOptions> backendOptions)
    {
        _backends = backends ?? throw new ArgumentNullException(nameof(backends));
        _healthService = healthService ?? throw new ArgumentNullException(nameof(healthService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _backendOptions = backendOptions?.Value ?? throw new ArgumentNullException(nameof(backendOptions));
        //_port = _backendOptions.ProbeServerPort; // Default probe server port
    }

    /// <summary>
    /// Starts the probe server on the configured port.
    /// </summary>
    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if ( !_backendOptions.HealthProbeSidecarEnabled )
        {
            return Task.CompletedTask;
        }
        _logger.LogInformation("[INIT] ✓ Health probe sidecar enabled at {Url}", _backendOptions.HealthProbeSidecarUrl);
        HttpClient selfCheckClient = CreateSelfCheckClient();
  
        // Lightweight timer only for status updates (no async work)
        _probeTimer = new Timer(_ =>
        {
            _readinessStatus = _healthService.GetReadinessStatus();
            _startupStatus = _healthService.GetStartupStatus();

            // call the probeserver and submit status at http://localhost:9000/internal/update-status
            try
            {
                var url = $"{_backendOptions.HealthProbeSidecarUrl}/internal/update-status?readiness={_readinessStatus}&startup={_startupStatus}";
                var response = selfCheckClient.GetAsync(url).Result;
                if (!response.IsSuccessStatusCode)
                {   
                    FailedAttempts++;
                    _logger.LogWarning("[FAIL] Probe server updated failed. {attempts} attempts. HTTP {StatusCode}", FailedAttempts, response.StatusCode);
                } else {
                    if (FailedAttempts > 0)
                    {
                        _logger.LogInformation("[RECOVER] Probe server update succeeded after {attempts} failed attempts", FailedAttempts);
                        FailedAttempts = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                FailedAttempts++;
                _logger.LogWarning("[FAIL] Probe server updated failed. {attempts} attempts. Exception: {Message}", FailedAttempts, ex.Message);

            }

        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        
        
        return Task.CompletedTask;
    }

    public async Task LivenessResponseAsync(HttpListenerContext lc)
    {
        // Liveness probe check - use pre-allocated objects
        try
        {
            var sw = Stopwatch.StartNew();

            lc.Response.StatusCode = (int)HttpStatusCode.OK;
            lc.Response.ContentType = "text/plain";
            lc.Response.Headers.Add("Cache-Control", "no-cache");
            lc.Response.Headers.Add("Connection", "close");
            lc.Response.ContentLength64 = s_okLength;
            await lc.Response.OutputStream.WriteAsync(s_okBytes, 0, s_okLength);

            sw.Stop();
            var elapsedMs = sw.ElapsedMilliseconds;
            if (elapsedMs >= 5)
            {
                _logger.LogWarning("Liveness latency {ElapsedMs} ms (>= 5 ms)", elapsedMs);
            }
        }
        finally
        {
            try
            {
                lc.Response.Close();
            }
            catch { }
            
        }
    }

    public async Task ReadinessResponseAsync(HttpListenerContext lc)
    {
        try
        {
            lc.Response.ContentType = "text/plain";
            lc.Response.Headers["Cache-Control"] = "no-cache";
            lc.Response.Headers["Connection"] = "close";

            switch (_readinessStatus)
            {
                case HealthStatusEnum.ReadinessZeroHosts:
                    lc.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                    lc.Response.ContentLength64 = s_zeroHostsLength;
                    await lc.Response.OutputStream.WriteAsync(s_zeroHosts, 0, s_zeroHostsLength);
                    break;
                case HealthStatusEnum.ReadinessFailedHosts:
                    lc.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                    lc.Response.ContentLength64 = s_failedHostsLength;
                    await lc.Response.OutputStream.WriteAsync(s_failedHosts, 0, s_failedHostsLength);
                    break;
                case HealthStatusEnum.ReadinessReady:
                    lc.Response.StatusCode = (int)HttpStatusCode.OK;
                    lc.Response.ContentLength64 = s_okLength;
                    await lc.Response.OutputStream.WriteAsync(s_okBytes, 0, s_okLength);
                    break;
            }
        } finally {
            try
            {
                lc.Response.Close();
            }
            catch { }
        }
    }

    public async Task StartupResponseAsync(HttpListenerContext lc)
    {
        try
        {
            lc.Response.ContentType = "text/plain";
            lc.Response.Headers["Cache-Control"] = "no-cache";
            lc.Response.Headers["Connection"] = "close";

            switch (_startupStatus)
            {
                case HealthStatusEnum.StartupZeroHosts:
                    lc.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                    lc.Response.ContentLength64 = s_zeroHostsLength;
                    await lc.Response.OutputStream.WriteAsync(s_zeroHosts, 0, s_zeroHostsLength);
                    break;
                case HealthStatusEnum.StartupFailedHosts:
                    lc.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                    lc.Response.ContentLength64 = s_failedHostsLength;
                    await lc.Response.OutputStream.WriteAsync(s_failedHosts, 0, s_failedHostsLength);
                    break;
                case HealthStatusEnum.StartupReady:
                    lc.Response.StatusCode = (int)HttpStatusCode.OK;
                    lc.Response.ContentLength64 = s_okLength;
                    await lc.Response.OutputStream.WriteAsync(s_okBytes, 0, s_okLength);
                    break;
            }
        }
        finally
        {
            try
            {
                lc.Response.Close();
            }
            catch { }

        }
    }

    /// <summary>
    /// Stops the probe server gracefully.
    /// </summary>
    public async Task StopAsync()
    {
        // cancel the timer
        _probeTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _probeTimer?.Dispose();
    }


    private static HttpClient CreateSelfCheckClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(500),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = 256,
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(1)
        };
    }
}
