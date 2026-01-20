using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using System.Threading;
using System.Diagnostics;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using System.IO.Pipelines;

using Shared.HealthProbe;

namespace HealthProbe;

/// <summary>
/// Standalone probe server using Kestrel on port 9000.
/// Provides health check endpoints for Kubernetes/container orchestration.
/// </summary>
public class ProbeServer : BackgroundService
{

    private readonly ILogger<ProbeServer> _logger;
    private readonly int _port;
    private WebApplication? _app;

    private static HealthStatusEnum _readinessStatus = HealthStatusEnum.ReadinessZeroHosts;
    private static HealthStatusEnum _startupStatus = HealthStatusEnum.StartupZeroHosts;

    public ProbeServer(ILogger<ProbeServer> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        // Read port from environment variable or use default
        var portStr = Environment.GetEnvironmentVariable("HEALTHPROBE_PORT");
        if (!string.IsNullOrEmpty(portStr) && int.TryParse(portStr, out var parsedPort))
        {
            _port = parsedPort;
        }
        else
        {
            _port = 9000; // Default probe server port
        }
    }

    /// <summary>
    /// Starts the probe server on the configured port.
    /// </summary>
    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // Ensure sufficient ThreadPool capacity under load
        ThreadPool.GetMinThreads(out var minWorkers, out var minIo);
        var desiredWorkers = Math.Max(minWorkers, 100);
        var desiredIo = Math.Max(minIo, 100);
        if (ThreadPool.SetMinThreads(desiredWorkers, desiredIo))
        {
            _logger.LogWarning("ThreadPool min threads set to {Workers}/{IO}", desiredWorkers, desiredIo);
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            EnvironmentName = Environments.Production
        });

        // Suppress default Kestrel configuration - we configure everything explicitly
        builder.Configuration.Sources.Clear();
        
        // Configure Kestrel for probe server
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(_port);
            
            // Critical: Increase limits to handle probe requests under load
            options.Limits.MaxConcurrentConnections = 1000;
            options.Limits.MaxConcurrentUpgradedConnections = 1000;
            
            // Reduce keep-alive to free up connections faster
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(5);
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
        });

        // TCP transport tuning (NoDelay, Backlog) must be set via SocketTransportOptions
        builder.Services.Configure<SocketTransportOptions>(opts =>
        {
            opts.NoDelay = true;     // Disable Nagle for tiny probe responses
            opts.Backlog = 4096;     // Increase accept queue depth under bursty load
            
            // CRITICAL: Use inline I/O completion to avoid ThreadPool queueing
            opts.UnsafePreferInlineScheduling = true;
        });

        // Minimal logging for probe server
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // Add services
        //builder.Services.AddHealthChecks();

        _app = builder.Build();

        // Seed snapshots before starting the timer

        // Configure endpoints
        ConfigureEndpoints(_app);

        _logger.LogInformation("Probe server starting on port {Port}", _port);
        
        // Log all registered endpoints
        var endpoints = _app.Services.GetService<Microsoft.AspNetCore.Routing.EndpointDataSource>();
        if (endpoints != null)
        {
            foreach (var endpoint in endpoints.Endpoints)
            {
                if (endpoint is Microsoft.AspNetCore.Routing.RouteEndpoint routeEndpoint)
                {
                    _logger.LogInformation("Registered endpoint: {Method} {Pattern}", 
                        string.Join(",", routeEndpoint.Metadata.OfType<Microsoft.AspNetCore.Routing.HttpMethodMetadata>().SelectMany(m => m.HttpMethods)),
                        routeEndpoint.RoutePattern.RawText);
                }
            }
        }
        
        return _app.RunAsync(cancellationToken);
    }

    /// <summary>
    /// Stops the probe server gracefully.
    /// </summary>
    public async Task StopAsync()
    {
        if (_app != null)
        {
            _logger.LogInformation("Probe server stopping");
            
            // // Stop health monitor thread
            // if (_healthMonitorCts != null)
            // {
            //     _healthMonitorCts.Cancel();
            //     if (_healthMonitorTask != null)
            //     {
            //         try
            //         {
            //             await _healthMonitorTask.ConfigureAwait(false);
            //         }
            //         catch (OperationCanceledException)
            //         {
            //             // Expected during shutdown
            //         }
            //     }
            //     _healthMonitorCts.Dispose();
            // }
            
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    /// <summary>
    /// Updates the health status for readiness and startup probes.
    /// Thread-safe method to be called by the proxy service.
    /// </summary>
    /// <param name="readinessStatus">The new readiness status</param>
    /// <param name="startupStatus">The new startup status</param>
    public static void UpdateHealthStatus(HealthStatusEnum readinessStatus, HealthStatusEnum startupStatus)
    {
        var currentReadiness = _readinessStatus;
        var currentStartup = _startupStatus;
        if  ( currentReadiness != readinessStatus || currentStartup != startupStatus )
        {
            Console.WriteLine($"Health status updated. Readiness: {currentReadiness} -> {readinessStatus}, Startup: {currentStartup} -> {startupStatus}");
        }
        _readinessStatus = readinessStatus;
        _startupStatus = startupStatus;
        s_lastUpdateTimestamp = Stopwatch.GetTimestamp();
    }

    static readonly byte[] s_okBytes = Encoding.UTF8.GetBytes("OK\n");
    static readonly int s_okLength = s_okBytes.Length; 
    private static readonly byte[] s_zeroHosts = Encoding.UTF8.GetBytes("Not Healthy.  Active Hosts: 0\n");
    private static readonly int s_zeroHostsLength = s_zeroHosts.Length;
    private static readonly byte[] s_failedHosts = Encoding.UTF8.GetBytes("Not Healthy.  Failed Hosts: True\n");
    private static readonly int s_failedHostsLength = s_failedHosts.Length;

    // Timestamp of last status update (Stopwatch ticks for minimal overhead)
    // Initialized to 0 to represent uninitialized state
    private static long s_lastUpdateTimestamp = 0;

    static int s_ReadinessCheckCount = 0;
    static DateTime s_lastReadinessLogTime = DateTime.MinValue;
    static int s_livenessCheckCount = 0;
    static DateTime s_lastLivenessLogTime = DateTime.MinValue;

    private void ConfigureEndpoints(WebApplication app)
    {
        // Readiness endpoint - checks if service is ready to accept traffic
        // CRITICAL: Synchronous handler to avoid ThreadPool dispatch
        app.MapGet(Constants.Readiness, (HttpContext ctx) =>
        {

            Interlocked.Increment(ref s_ReadinessCheckCount);
            // once per minute, log the readiness check
            if ((DateTime.UtcNow - s_lastReadinessLogTime).TotalSeconds >= 60)
            {
                s_lastReadinessLogTime = DateTime.UtcNow;
                Console.WriteLine($"Readiness checks in last minute: {s_ReadinessCheckCount}");
                Volatile.Write(ref s_ReadinessCheckCount, 0);
            }

            ctx.Response.ContentType = "text/plain";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            //ctx.Response.Headers["Connection"] = "close";

            ReadOnlySpan<byte> data;

            // Check if status updates are stale (no updates in last 10 seconds)
            // Skip check if never initialized (timestamp == 0)
            if (s_lastUpdateTimestamp != 0 && s_lastUpdateTimestamp < Stopwatch.GetTimestamp() - Stopwatch.Frequency * 10)
            {
                // No updates received in last 10 seconds - mark as failed
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                ctx.Response.ContentLength = s_failedHostsLength;
                data = s_failedHosts;
            }
            else
            {
                var currentStatus = _readinessStatus;
                switch (currentStatus)
                {
                    case HealthStatusEnum.ReadinessZeroHosts:
                        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                        ctx.Response.ContentLength = s_zeroHostsLength;
                        data = s_zeroHosts;
                        break;
                    case HealthStatusEnum.ReadinessFailedHosts:
                        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                        ctx.Response.ContentLength = s_failedHostsLength;
                        data = s_failedHosts;
                        break;
                    case HealthStatusEnum.ReadinessReady:
                    default:
                        ctx.Response.StatusCode = StatusCodes.Status200OK;
                        ctx.Response.ContentLength = s_okLength;
                        data = s_okBytes;
                        break;
                }
            }
            
            // Synchronous zero-allocation write using PipeWriter
            var memory = ctx.Response.BodyWriter.GetMemory(data.Length);
            data.CopyTo(memory.Span);
            ctx.Response.BodyWriter.Advance(data.Length);
            ctx.Response.BodyWriter.Complete();
            
            return Task.CompletedTask;
        });

        // Startup endpoint - checks if service has completed initialization
        // CRITICAL: Synchronous handler to avoid ThreadPool dispatch
        app.MapGet(Constants.Startup, (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/plain";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            //ctx.Response.Headers["Connection"] = "close";

            ReadOnlySpan<byte> data;
            
            // Check if status updates are stale (no updates in last 10 seconds)
            // Skip check if never initialized (timestamp == 0)
            if (s_lastUpdateTimestamp != 0 && s_lastUpdateTimestamp < Stopwatch.GetTimestamp() - Stopwatch.Frequency * 10)
            {
                // No updates received in last 10 seconds - mark as failed
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                ctx.Response.ContentLength = s_failedHostsLength;
                data = s_failedHosts;
            }
            else
            {
                var currentStatus = _startupStatus;
                switch (currentStatus)
                {
                    case HealthStatusEnum.StartupZeroHosts:
                        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                        ctx.Response.ContentLength = s_zeroHostsLength;
                        data = s_zeroHosts;
                        break;
                    case HealthStatusEnum.StartupFailedHosts:
                        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                        ctx.Response.ContentLength = s_failedHostsLength;
                        data = s_failedHosts;
                        break;
                    case HealthStatusEnum.StartupReady:
                    default:
                        ctx.Response.StatusCode = StatusCodes.Status200OK;
                        ctx.Response.ContentLength = s_okLength;
                        data = s_okBytes;
                        break;
                }
            }
            
            // Synchronous zero-allocation write using PipeWriter
            var memory = ctx.Response.BodyWriter.GetMemory(data.Length);
            data.CopyTo(memory.Span);
            ctx.Response.BodyWriter.Advance(data.Length);
            ctx.Response.BodyWriter.Complete();
            
            return Task.CompletedTask;
        });


        // Liveness endpoint - checks if service is alive and functioning
        // CRITICAL: Synchronous handler to avoid ThreadPool dispatch overhead
        app.MapGet(Constants.Liveness, (HttpContext ctx) =>
        {
            Interlocked.Increment(ref s_livenessCheckCount);

            // once per minute, log the liveness check
            if ((DateTime.UtcNow - s_lastLivenessLogTime).TotalSeconds >= 60)
            {
                s_lastLivenessLogTime = DateTime.UtcNow;
                Console.WriteLine($"Liveness checks in last minute: {s_livenessCheckCount}");
                Volatile.Write(ref s_livenessCheckCount, 0);
            }

            ctx.Response.ContentType = "text/plain";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            //ctx.Response.Headers["Connection"] = "close";

            ReadOnlySpan<byte> data;
            

            if (s_lastUpdateTimestamp != 0 && s_lastUpdateTimestamp < Stopwatch.GetTimestamp() - Stopwatch.Frequency * 10)
            {
                // No updates received in last 10 seconds - mark as failed
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                ctx.Response.ContentLength = s_failedHostsLength;
                data = s_failedHosts;
            }
            else
            {                
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentLength = s_okLength;
                data = s_okBytes;
            }
                
            // Synchronous zero-allocation write using PipeWriter - no async dispatch
            var memory = ctx.Response.BodyWriter.GetMemory(data.Length);
            data.CopyTo(memory.Span);
            ctx.Response.BodyWriter.Advance(data.Length);
            ctx.Response.BodyWriter.Complete();

            return Task.CompletedTask;
        });

        // Support HEAD method for probe endpoints for compatibility with certain load balancers
        app.MapMethods(Constants.Liveness, new[] { "HEAD" }, (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/plain";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
           // ctx.Response.Headers["Connection"] = "close";
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentLength = s_okLength;
            ctx.Response.BodyWriter.Complete();
            return Task.CompletedTask;
        });
        app.MapMethods(Constants.Readiness, new[] { "HEAD" }, (HttpContext ctx) =>
        {
            var currentStatus =  _readinessStatus;
            ctx.Response.ContentType = "text/plain";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            //ctx.Response.Headers["Connection"] = "close";
            switch (currentStatus)
            {
                case HealthStatusEnum.ReadinessZeroHosts:
                    ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    ctx.Response.ContentLength = s_zeroHostsLength;
                    break;
                case HealthStatusEnum.ReadinessFailedHosts:
                    ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    ctx.Response.ContentLength = s_failedHostsLength;
                    break;
                case HealthStatusEnum.ReadinessReady:
                    ctx.Response.StatusCode = StatusCodes.Status200OK;
                    ctx.Response.ContentLength = s_okLength;
                    break;
            }
            ctx.Response.BodyWriter.Complete();
            return Task.CompletedTask;
        });
        app.MapMethods(Constants.Startup, new[] { "HEAD" }, (HttpContext ctx) =>
        {
            var currentStatus = _startupStatus;
            ctx.Response.ContentType = "text/plain";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            //ctx.Response.Headers["Connection"] = "close";
            switch (currentStatus)
            {
                case HealthStatusEnum.StartupZeroHosts:
                    ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    ctx.Response.ContentLength = s_zeroHostsLength;
                    break;
                case HealthStatusEnum.StartupFailedHosts:
                    ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    ctx.Response.ContentLength = s_failedHostsLength;
                    break;
                case HealthStatusEnum.StartupReady:
                    ctx.Response.StatusCode = StatusCodes.Status200OK;
                    ctx.Response.ContentLength = s_okLength;
                    break;
            }
            ctx.Response.BodyWriter.Complete();
            return Task.CompletedTask;
        });

        // Update status endpoint - allows proxy to update health status
        // Query string parameters for zero-allocation direct update
        app.MapGet("/internal/update-status", (HttpContext ctx) =>
        {            
            // Parse query parameters directly - no JSON deserialization overhead
            if (!ctx.Request.Query.TryGetValue("readiness", out var readinessStr) ||
                !ctx.Request.Query.TryGetValue("startup", out var startupStr))
            {
                _logger.LogWarning("Missing query parameters. Readiness: {Readiness}, Startup: {Startup}", 
                    ctx.Request.Query.ContainsKey("readiness"), ctx.Request.Query.ContainsKey("startup"));
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                ctx.Response.ContentType = "text/plain";
                ctx.Response.BodyWriter.Complete();
                return Task.CompletedTask;
            }

            if (!Enum.TryParse<HealthStatusEnum>(readinessStr, out var readiness) ||
                !Enum.TryParse<HealthStatusEnum>(startupStr, out var startup))
            {
                _logger.LogWarning("Invalid enum values. Readiness: {ReadinessStr}, Startup: {StartupStr}", 
                    readinessStr, startupStr);
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                ctx.Response.ContentType = "text/plain";
                ctx.Response.BodyWriter.Complete();
                return Task.CompletedTask;
            }

            UpdateHealthStatus(readiness, startup);

            
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "text/plain";
            ctx.Response.ContentLength = s_okLength;
            var memory = ctx.Response.BodyWriter.GetMemory(s_okLength);
            s_okBytes.AsSpan().CopyTo(memory.Span);
            ctx.Response.BodyWriter.Advance(s_okLength);
            ctx.Response.BodyWriter.Complete();
            
            return Task.CompletedTask;
        });

    
    }
}
