using System.Collections.Frozen;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MetricsServer;

/// <summary>
/// Hosts the Kestrel web server for the metrics service, answers container probes, and runs the
/// maintenance loop. Data request handling is delegated to <see cref="MetricsHttpServer"/>.
/// </summary>
public sealed class MetricsWebServer : BackgroundService
{
    private static readonly byte[] s_okBytes = Encoding.UTF8.GetBytes("OK\n");

    private static readonly FrozenSet<string> s_probePaths =
        new[] { Constants.Health, Constants.Liveness, Constants.Readiness }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger<MetricsWebServer> _logger;
    private readonly MetricsOptions _options;
    private readonly MetricsHttpServer _handler;
    private WebApplication? _app;

    public MetricsWebServer(
        ILogger<MetricsWebServer> logger,
        MetricsOptions options,
        MetricsHttpServer handler)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // Ensure sufficient ThreadPool capacity under load.
        ThreadPool.GetMinThreads(out var minWorkers, out var minIo);
        ThreadPool.SetMinThreads(Math.Max(minWorkers, 100), Math.Max(minIo, 100));

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            EnvironmentName = Environments.Production
        });

        // Configure everything explicitly instead of inheriting ambient configuration.
        builder.Configuration.Sources.Clear();

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(_options.Port);
            options.AddServerHeader = false;
            options.Limits.MaxConcurrentConnections = 100000;  // Allow high concurrency for benchmarks
            options.Limits.MaxConcurrentUpgradedConnections = 100000;
            options.Limits.MaxRequestBodySize = _options.MaxRequestBodyBytes;
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        });

        builder.Services.Configure<SocketTransportOptions>(opts =>
        {
            opts.NoDelay = true;
            opts.Backlog = 4096;
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        _app = builder.Build();
        _app.Run(HandleRequestAsync);

        _logger.LogInformation(
            "Metrics server {Version} starting on port {Port} (bucket {BucketSeconds}s x {BucketCount}, max series {MaxSeries}, app insights {AppInsights})",
            Constants.VERSION,
            _options.Port,
            _options.BucketSeconds,
            _options.BucketCount,
            _options.MaxSeries,
            _handler.TelemetryEnabled ? "enabled" : "disabled");

        await Task.WhenAll(
            _app.RunAsync(cancellationToken),
            _handler.RunMaintenanceAsync(cancellationToken)).ConfigureAwait(false);
    }

    private Task HandleRequestAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? string.Empty;

        if (s_probePaths.Contains(path))
        {
            var method = ctx.Request.Method;
            if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method))
            {
                return HandleProbeAsync(ctx);
            }

            return WriteMethodNotAllowedAsync(ctx.Response, "GET, HEAD");
        }

        return _handler.HandleRequestAsync(ctx);
    }

    private static Task HandleProbeAsync(HttpContext ctx)
    {
        WriteProbeHeaders(ctx.Response);
        if (!HttpMethods.IsHead(ctx.Request.Method))
        {
            var destination = ctx.Response.BodyWriter.GetSpan(s_okBytes.Length);
            s_okBytes.AsSpan().CopyTo(destination);
            ctx.Response.BodyWriter.Advance(s_okBytes.Length);
        }

        return Task.CompletedTask;
    }

    private static void WriteProbeHeaders(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/plain";
        response.Headers["Cache-Control"] = "no-cache";
        response.ContentLength = s_okBytes.Length;
    }

    private static Task WriteMethodNotAllowedAsync(HttpResponse response, string allowHeader)
    {
        response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        response.Headers["Allow"] = allowHeader;
        response.ContentLength = 0;
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        if (_app is not null)
        {
            _logger.LogInformation("Metrics server stopping");
            await _app.StopAsync(cancellationToken).ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }
    }
}