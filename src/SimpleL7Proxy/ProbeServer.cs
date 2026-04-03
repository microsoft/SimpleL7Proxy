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
using SimpleL7Proxy.Events;

namespace SimpleL7Proxy;

/// <summary>
/// Standalone probe server using Kestrel on port 9000.
/// Provides health check endpoints for Kubernetes/container orchestration.
/// </summary>
public class ProbeServer : BackgroundService, IConfigChangeSubscriber
{
    private readonly IBackendService _backends;
    private readonly ILogger<ProbeServer> _logger;
    private readonly HealthCheckService _healthService;

    private static HealthStatusEnum _readinessStatus = HealthStatusEnum.ReadinessZeroHosts;
    private static HealthStatusEnum _startupStatus = HealthStatusEnum.StartupZeroHosts;
    private static int _activeUndrainedEvents = 0;


    // Active snapshots published to readers (use Volatile.Read/Write for memory ordering)

    private Timer? _probeTimer;
    private readonly ProxyConfig _backendOptions;
    private HttpClient? _selfCheckClient;
    private IEventClient? _eventClient;

    static readonly byte[] s_okBytes = Encoding.UTF8.GetBytes("OK\n");
    static readonly int s_okLength = s_okBytes.Length; 
    private static readonly byte[] s_zeroHosts = Encoding.UTF8.GetBytes("Not Healthy.  Active Hosts: 0\n");
    private static readonly int s_zeroHostsLength = s_zeroHosts.Length;
    private static readonly byte[] s_failedHosts = Encoding.UTF8.GetBytes("Not Healthy.  Failed Hosts: True\n");
    private static readonly int s_failedHostsLength = s_failedHosts.Length;
    public static HealthStatusEnum ReadinessStatus = HealthStatusEnum.ReadinessZeroHosts;
    public static HealthStatusEnum StartupStatus = HealthStatusEnum.StartupZeroHosts;

    private static int FailedAttempts = 0;
    public ProbeServer(
        IBackendService backends, 
        HealthCheckService healthService, 
        ILogger<ProbeServer> logger, 
        IOptions<ProxyConfig> backendOptions, 
        ConfigChangeNotifier configChangeNotifier,
        IEventClient eventClient)
    {
        _backends = backends ?? throw new ArgumentNullException(nameof(backends));
        _healthService = healthService ?? throw new ArgumentNullException(nameof(healthService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _backendOptions = backendOptions?.Value ?? throw new ArgumentNullException(nameof(backendOptions));
        _eventClient = eventClient ?? throw new ArgumentNullException(nameof(eventClient));

        // Subscribe for HealthProbeSidecar changes (HealthProbeSidecarEnabled & Url are parsed from it)
        configChangeNotifier.Subscribe(this, options => options.HealthProbeSidecar);
    }

    /// <summary>
    /// Starts the probe server on the configured port.
    /// </summary>
    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        StartProbeServer();
        return Task.CompletedTask;
    }

    /// <summary>
    /// (Re)initializes the sidecar client and probe timer.
    /// Safe to call multiple times — tears down the previous instance first.
    /// </summary>
    private void StartProbeServer()
    {
        if (_backendOptions.HealthProbeSidecarEnabled)
        {
            _logger.LogInformation("[INIT] ✓ Health probe sidecar enabled at {Url}", _backendOptions.HealthProbeSidecarUrl);
            _selfCheckClient = CreateSelfCheckClient();
        }
        else
        {
            _logger.LogInformation("[INIT] Health probe running in standalone mode (no sidecar)");
        }

        // Single timer for status updates and optional sidecar push
        _probeTimer = new Timer(_ =>
        {
            (_startupStatus, _readinessStatus, _activeUndrainedEvents) = _healthService.GetStatus();

            // Push to sidecar if enabled (fire-and-forget async to avoid blocking threadpool)
            var client = _selfCheckClient;
            if (client != null)
            {
                _ = PushStatusToSidecarAsync(client);
            }

        }, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10)); // initial delay, interval

        FailedAttempts = 0;
    }

    /// <summary>
    /// Stops the timer and disposes the sidecar client.
    /// </summary>
    private void StopProbeServer()
    {
        _probeTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _probeTimer?.Dispose();
        _probeTimer = null;

        _selfCheckClient?.Dispose();
        _selfCheckClient = null;
    }

    private async Task PushStatusToSidecarAsync(HttpClient selfCheckClient)
    {
        try
        {
            var url = $"{_backendOptions.HealthProbeSidecarUrl}/internal/update-status?readiness={_readinessStatus}&startup={_startupStatus}";
            using var response = await selfCheckClient.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                FailedAttempts++;
                _logger.LogWarning("[FAIL] Probe server updated failed. {attempts} attempts. HTTP {StatusCode}", FailedAttempts, response.StatusCode);
            }
            else
            {
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
    }

    public int EventCount => _activeUndrainedEvents;

    // TODO: no need for stopwatch any longer
    public async Task<HttpStatusCode> LivenessResponseAsync(HttpListenerContext lc)
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
        return HttpStatusCode.OK;
    }

    public async Task<HttpStatusCode> ReadinessResponseAsync(HttpListenerContext lc)
    {
        HttpStatusCode statusCode = HttpStatusCode.OK; // default to 200, may be overridden in switch
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
                    statusCode = HttpStatusCode.ServiceUnavailable;
                    break;
                case HealthStatusEnum.ReadinessFailedHosts:
                    lc.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                    lc.Response.ContentLength64 = s_failedHostsLength;
                    await lc.Response.OutputStream.WriteAsync(s_failedHosts, 0, s_failedHostsLength);
                    statusCode = HttpStatusCode.ServiceUnavailable;
                    break;
                case HealthStatusEnum.ReadinessReady:
                    lc.Response.StatusCode = (int)HttpStatusCode.OK;
                    lc.Response.ContentLength64 = s_okLength;
                    await lc.Response.OutputStream.WriteAsync(s_okBytes, 0, s_okLength);
                    statusCode = HttpStatusCode.OK;

                    break;
            }
        } finally {
            try
            {
                lc.Response.Close();
            }
            catch { }
        }
        return statusCode;
    }

    public async Task<HttpStatusCode> StartupResponseAsync(HttpListenerContext lc)
    {
        HttpStatusCode statusCode = HttpStatusCode.OK; // default to 200, may be overridden in switch
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
                    statusCode = HttpStatusCode.ServiceUnavailable;
                    break;
                case HealthStatusEnum.StartupFailedHosts:
                    lc.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                    lc.Response.ContentLength64 = s_failedHostsLength;
                    await lc.Response.OutputStream.WriteAsync(s_failedHosts, 0, s_failedHostsLength);
                    statusCode = HttpStatusCode.ServiceUnavailable;
                    break;
                case HealthStatusEnum.StartupReady:
                    lc.Response.StatusCode = (int)HttpStatusCode.OK;
                    lc.Response.ContentLength64 = s_okLength;
                    await lc.Response.OutputStream.WriteAsync(s_okBytes, 0, s_okLength);
                    statusCode = HttpStatusCode.OK;
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
        return statusCode;
    }

    /// <summary>
    /// Called by the host on shutdown — stops the probe server gracefully.
    /// </summary>
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        StopProbeServer();
        return base.StopAsync(cancellationToken);
    }

    public void InitVars()
    {
        // no config-dependent variables to init for now, but this is a placeholder for future ones
    }

    /// <summary>
    /// Called when HealthProbeSidecar config changes at runtime.
    /// Does a clean restart: stops timer, disposes client, re-initializes everything.
    /// </summary>
    public Task OnConfigChangedAsync(
        IReadOnlyList<ConfigChange> changes,
        ProxyConfig backendOptions,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("[CONFIG] HealthProbeSidecar changed — restarting probe server");
        // apply the changes
        StopProbeServer();
        StartProbeServer();
        return Task.CompletedTask;
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
