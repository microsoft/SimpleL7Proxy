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
    private static readonly PathString s_readinessPath = new(Constants.Readiness);
    private static readonly PathString s_startupPath = new(Constants.Startup);
    private static readonly PathString s_livenessPath = new(Constants.Liveness);
    private static readonly PathString s_updateStatusPath = new("/internal/update-status");

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

        // Configure endpoints
        ConfigureEndpoints(_app);

        _logger.LogInformation("Probe server starting on port {Port}", _port);

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
        _readinessStatus = readinessStatus;
        _startupStatus = startupStatus;
        Volatile.Write(ref s_lastUpdateTimestamp, Stopwatch.GetTimestamp());
    }

    static readonly byte[] s_okBytes = Encoding.UTF8.GetBytes("OK\n");
    static readonly int s_okLength = s_okBytes.Length;
    private static readonly byte[] s_zeroHosts = Encoding.UTF8.GetBytes("Not Healthy.  Active Hosts: 0\n");
    private static readonly int s_zeroHostsLength = s_zeroHosts.Length;
    private static readonly byte[] s_failedHosts = Encoding.UTF8.GetBytes("Not Healthy.  Failed Hosts: True\n");
    private static readonly int s_failedHostsLength = s_failedHosts.Length;
    private static readonly long s_statusUpdateStaleAfterTicks = Stopwatch.Frequency * 20L;
    private static readonly Action<ILogger, Exception?> s_missingUpdateStatusParametersLog =
        LoggerMessage.Define(LogLevel.Warning, new EventId(1, "MissingUpdateStatusParameters"), "Missing update-status query parameters.");
    private static readonly Action<ILogger, Exception?> s_invalidUpdateStatusParametersLog =
        LoggerMessage.Define(LogLevel.Warning, new EventId(2, "InvalidUpdateStatusParameters"), "Invalid update-status query parameters.");

    // Timestamp of last status update (Stopwatch ticks for minimal overhead)
    // Initialized to 0 to represent uninitialized state
    private static long s_lastUpdateTimestamp = 0;

    private void ConfigureEndpoints(WebApplication app)
    {
        app.Run(HandleRequestAsync);
    }

    private Task HandleRequestAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path;
        var method = ctx.Request.Method;

        if (path == s_livenessPath)
        {
            if (HttpMethods.IsHead(method))
            {
                WriteLivenessHeadResponse(ctx.Response);
                return Task.CompletedTask;
            }

            if (HttpMethods.IsGet(method))
            {
                WriteLivenessGetResponse(ctx.Response);
                return Task.CompletedTask;
            }

            return WriteMethodNotAllowed(ctx.Response, "GET, HEAD");
        }

        if (path == s_readinessPath)
        {
            if (HttpMethods.IsHead(method))
            {
                WriteReadinessHeadResponse(ctx.Response);
                return Task.CompletedTask;
            }

            if (HttpMethods.IsGet(method))
            {
                WriteReadinessGetResponse(ctx.Response);
                return Task.CompletedTask;
            }

            return WriteMethodNotAllowed(ctx.Response, "GET, HEAD");
        }

        if (path == s_startupPath)
        {
            if (HttpMethods.IsHead(method))
            {
                WriteStartupHeadResponse(ctx.Response);
                return Task.CompletedTask;
            }

            if (HttpMethods.IsGet(method))
            {
                WriteStartupGetResponse(ctx.Response);
                return Task.CompletedTask;
            }

            return WriteMethodNotAllowed(ctx.Response, "GET, HEAD");
        }

        if (path == s_updateStatusPath)  //  "/internal/update-status"
        {
            if (!HttpMethods.IsGet(method))
            {
                return WriteMethodNotAllowed(ctx.Response, "GET");
            }

            if (!TryParseUpdateStatusQuery(ctx.Request.QueryString.Value, out var readiness, out var startup, out var parseResult))
            {
                if (parseResult == UpdateStatusParseResult.MissingParameters)
                {
                    s_missingUpdateStatusParametersLog(_logger, null);
                }
                else
                {
                    s_invalidUpdateStatusParametersLog(_logger, null);
                }

                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                ctx.Response.ContentType = "text/plain";
                ctx.Response.ContentLength = 0;
                return Task.CompletedTask;
            }

            UpdateHealthStatus(readiness, startup);
            WriteBodyResponse(ctx.Response, StatusCodes.Status200OK, s_okBytes, s_okLength);
            return Task.CompletedTask;
        }

        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        ctx.Response.ContentLength = 0;
        return Task.CompletedTask;
    }

    private Task HandleUpdateStatus(HttpContext ctx)
    {

    }

    private static Task WriteMethodNotAllowed(HttpResponse response, string allowHeader)
    {
        response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        response.Headers["Allow"] = allowHeader;
        response.ContentLength = 0;
        return Task.CompletedTask;
    }

    private static void WriteLivenessGetResponse(HttpResponse response)
    {
        if (IsStatusUpdateStale())
        {
            WriteBodyResponse(response, StatusCodes.Status503ServiceUnavailable, s_failedHosts, s_failedHostsLength);
            return;
        }

        WriteBodyResponse(response, StatusCodes.Status200OK, s_okBytes, s_okLength);
    }

    private static void WriteReadinessGetResponse(HttpResponse response)
    {
        if (IsStatusUpdateStale())
        {
            WriteBodyResponse(response, StatusCodes.Status503ServiceUnavailable, s_failedHosts, s_failedHostsLength);
            return;
        }

        switch (_readinessStatus)
        {
            case HealthStatusEnum.ReadinessZeroHosts:
                WriteBodyResponse(response, StatusCodes.Status503ServiceUnavailable, s_zeroHosts, s_zeroHostsLength);
                break;
            case HealthStatusEnum.ReadinessFailedHosts:
                WriteBodyResponse(response, StatusCodes.Status503ServiceUnavailable, s_failedHosts, s_failedHostsLength);
                break;
            case HealthStatusEnum.ReadinessReady:
            default:
                WriteBodyResponse(response, StatusCodes.Status200OK, s_okBytes, s_okLength);
                break;
        }
    }

    private static void WriteStartupGetResponse(HttpResponse response)
    {
        if (IsStatusUpdateStale())
        {
            WriteBodyResponse(response, StatusCodes.Status503ServiceUnavailable, s_failedHosts, s_failedHostsLength);
            return;
        }

        switch (_startupStatus)
        {
            case HealthStatusEnum.StartupZeroHosts:
                WriteBodyResponse(response, StatusCodes.Status503ServiceUnavailable, s_zeroHosts, s_zeroHostsLength);
                break;
            case HealthStatusEnum.StartupFailedHosts:
                WriteBodyResponse(response, StatusCodes.Status503ServiceUnavailable, s_failedHosts, s_failedHostsLength);
                break;
            case HealthStatusEnum.StartupReady:
            default:
                WriteBodyResponse(response, StatusCodes.Status200OK, s_okBytes, s_okLength);
                break;
        }
    }

    private static void WriteLivenessHeadResponse(HttpResponse response)
    {
        WriteHeadResponse(response, StatusCodes.Status200OK, s_okLength);
    }

    private static void WriteReadinessHeadResponse(HttpResponse response)
    {
        switch (_readinessStatus)
        {
            case HealthStatusEnum.ReadinessZeroHosts:
                WriteHeadResponse(response, StatusCodes.Status503ServiceUnavailable, s_zeroHostsLength);
                break;
            case HealthStatusEnum.ReadinessFailedHosts:
                WriteHeadResponse(response, StatusCodes.Status503ServiceUnavailable, s_failedHostsLength);
                break;
            case HealthStatusEnum.ReadinessReady:
            default:
                WriteHeadResponse(response, StatusCodes.Status200OK, s_okLength);
                break;
        }
    }

    private static void WriteStartupHeadResponse(HttpResponse response)
    {
        switch (_startupStatus)
        {
            case HealthStatusEnum.StartupZeroHosts:
                WriteHeadResponse(response, StatusCodes.Status503ServiceUnavailable, s_zeroHostsLength);
                break;
            case HealthStatusEnum.StartupFailedHosts:
                WriteHeadResponse(response, StatusCodes.Status503ServiceUnavailable, s_failedHostsLength);
                break;
            case HealthStatusEnum.StartupReady:
            default:
                WriteHeadResponse(response, StatusCodes.Status200OK, s_okLength);
                break;
        }
    }

    private static void WriteBodyResponse(HttpResponse response, int statusCode, byte[] payload, int payloadLength)
    {
        response.StatusCode = statusCode;
        response.ContentType = "text/plain";
        response.Headers["Cache-Control"] = "no-cache";
        response.ContentLength = payloadLength;
        var destination = response.BodyWriter.GetSpan(payloadLength);
        payload.AsSpan(0, payloadLength).CopyTo(destination);
        response.BodyWriter.Advance(payloadLength);
    }

    private static void WriteHeadResponse(HttpResponse response, int statusCode, int payloadLength)
    {
        response.StatusCode = statusCode;
        response.ContentType = "text/plain";
        response.Headers["Cache-Control"] = "no-cache";
        response.ContentLength = payloadLength;
    }

    private static bool IsStatusUpdateStale()
    {
        var lastUpdateTimestamp = Volatile.Read(ref s_lastUpdateTimestamp);
        return lastUpdateTimestamp != 0 && lastUpdateTimestamp < Stopwatch.GetTimestamp() - s_statusUpdateStaleAfterTicks;
    }

    private static bool TryParseUpdateStatusQuery(
        string? queryString,
        out HealthStatusEnum readiness,
        out HealthStatusEnum startup,
        out UpdateStatusParseResult parseResult)
    {
        readiness = default;
        startup = default;

        if (string.IsNullOrEmpty(queryString))
        {
            parseResult = UpdateStatusParseResult.MissingParameters;
            return false;
        }

        ReadOnlySpan<char> query = queryString.AsSpan();
        if (!query.IsEmpty && query[0] == '?')
        {
            query = query[1..];
        }

        ReadOnlySpan<char> readinessValue = default;
        ReadOnlySpan<char> startupValue = default;

        while (!query.IsEmpty)
        {
            var ampIndex = query.IndexOf('&');
            ReadOnlySpan<char> segment;
            if (ampIndex < 0)
            {
                segment = query;
                query = default;
            }
            else
            {
                segment = query[..ampIndex];
                query = query[(ampIndex + 1)..];
            }

            if (segment.IsEmpty)
            {
                continue;
            }

            var equalsIndex = segment.IndexOf('=');
            if (equalsIndex <= 0 || equalsIndex == segment.Length - 1)
            {
                continue;
            }

            var name = segment[..equalsIndex];
            var value = segment[(equalsIndex + 1)..];

            if (name.SequenceEqual("readiness"))
            {
                readinessValue = value;
                continue;
            }

            if (name.SequenceEqual("startup"))
            {
                startupValue = value;
            }
        }

        if (readinessValue.IsEmpty || startupValue.IsEmpty)
        {
            parseResult = UpdateStatusParseResult.MissingParameters;
            return false;
        }

        if (!Enum.TryParse(readinessValue, out readiness) || !Enum.TryParse(startupValue, out startup))
        {
            parseResult = UpdateStatusParseResult.InvalidParameters;
            return false;
        }

        parseResult = UpdateStatusParseResult.Success;
        return true;
    }

    private enum UpdateStatusParseResult
    {
        Success,
        MissingParameters,
        InvalidParameters
    }
}
