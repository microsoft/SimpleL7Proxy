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

using Shared.HealthProbe;


/// <summary>
/// Standalone probe server using Kestrel on port 9000.
/// Provides health check endpoints for Kubernetes/container orchestration.
/// </summary>
public class ProbeServer
{

    static readonly byte[] s_okBytes = Encoding.UTF8.GetBytes("OK\n");
    static readonly int s_okLength = s_okBytes.Length; 
    private static readonly byte[] s_zeroHosts = Encoding.UTF8.GetBytes("Not Healthy.  Active Hosts: 0\n");
    private static readonly int s_zeroHostsLength = s_zeroHosts.Length;
    private static readonly byte[] s_failedHosts = Encoding.UTF8.GetBytes("Not Healthy.  Failed Hosts: True\n");
    private static readonly int s_failedHostsLength = s_failedHosts.Length;

    private static HealthStatusEnum _readinessStatus = HealthStatusEnum.ReadinessZeroHosts;
    private static HealthStatusEnum _startupStatus = HealthStatusEnum.StartupZeroHosts;

    private readonly Func<HealthStatusEnum> _getStatus;
    private Timer? _probeTimer;

    public ProbeServer(Func<HealthStatusEnum> getStatus)
    {
        _getStatus = getStatus ?? throw new ArgumentNullException(nameof(getStatus));
    }

    /// <summary>
    /// Starts the probe server on the configured port.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
 
        // Lightweight timer only for status updates (no async work)
        _probeTimer = new Timer(_ =>
        {
            try {
                _startupStatus = _readinessStatus = _getStatus();
            } catch (Exception ex) {
                Console.WriteLine($"ProbeServer: Exception in status update: {ex.Message}, Retrying...");
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
                Console.WriteLine($"Liveness latency {elapsedMs} ms (>= 5 ms)");
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
                case HealthStatusEnum.StartupWorkersNotReady:
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
                case HealthStatusEnum.StartupWorkersNotReady:
                case HealthStatusEnum.StartupZeroHosts:
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
    public Task StopAsync()
    {
        // cancel the timer
        _probeTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _probeTimer?.Dispose();
        return Task.CompletedTask;
    }
}
