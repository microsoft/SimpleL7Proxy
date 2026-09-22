using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MetricsServer;

/// <summary>
/// Kestrel-based metrics server. Accepts rollup data from other services and answers rolled up
/// status queries for users and models. All state is kept in memory only.
/// </summary>
public sealed class MetricsHttpServer : BackgroundService
{
    private static readonly PathString s_healthPath = new(Constants.Health);
    private static readonly PathString s_livenessPath = new(Constants.Liveness);
    private static readonly PathString s_readinessPath = new(Constants.Readiness);
    private static readonly PathString s_rollupPath = new(Constants.Rollup);
    private static readonly PathString s_statusPath = new(Constants.Status);
    private static readonly PathString s_usersPath = new(Constants.Users);
    private static readonly PathString s_modelsPath = new(Constants.Models);
    private static readonly PathString s_seriesPath = new(Constants.Series);
    private static readonly PathString s_statsPath = new(Constants.Stats);

    private static readonly byte[] s_okBytes = Encoding.UTF8.GetBytes("OK\n");
    private static readonly byte[] s_recordsProperty = Encoding.UTF8.GetBytes("\"records\"");

    private readonly ILogger<MetricsHttpServer> _logger;
    private readonly MetricsOptions _options;
    private readonly MetricsStore _store;
    private readonly TelemetryClient? _telemetryClient;
    private WebApplication? _app;

    public MetricsHttpServer(
        ILogger<MetricsHttpServer> logger,
        MetricsOptions options,
        MetricsStore store,
        TelemetryClient? telemetryClient = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _telemetryClient = telemetryClient;
    }

    /// <summary>
    /// Starts the metrics server and the background prune loop.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // Ensure sufficient ThreadPool capacity under load.
        ThreadPool.GetMinThreads(out var minWorkers, out var minIo);
        var desiredWorkers = Math.Max(minWorkers, 100);
        var desiredIo = Math.Max(minIo, 100);
        ThreadPool.SetMinThreads(desiredWorkers, desiredIo);

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
            options.Limits.MaxConcurrentConnections = 5000;
            options.Limits.MaxConcurrentUpgradedConnections = 5000;
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
            _telemetryClient is null ? "disabled" : "enabled");

        await Task.WhenAll(
            _app.RunAsync(cancellationToken),
            MaintenanceLoopAsync(cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Periodically removes idle series and publishes server counters to Application Insights.
    /// </summary>
    private async Task MaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Min(
            Math.Max(_options.BucketSeconds, 30),
            _options.TelemetryIntervalSeconds));
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var removed = _store.Prune();
                if (removed > 0)
                {
                    _logger.LogInformation("Pruned {Removed} idle series", removed);
                }

                PublishTelemetry();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
    }

    private void PublishTelemetry()
    {
        if (_telemetryClient is null)
        {
            return;
        }

        var stats = _store.Stats();
        _telemetryClient.GetMetric("MetricsServer.SeriesCount").TrackValue(stats.SeriesCount);
        _telemetryClient.GetMetric("MetricsServer.UserCount").TrackValue(stats.UserCount);
        _telemetryClient.GetMetric("MetricsServer.ModelCount").TrackValue(stats.ModelCount);
        _telemetryClient.GetMetric("MetricsServer.RecordsIngested").TrackValue(stats.RecordsIngested);
        _telemetryClient.GetMetric("MetricsServer.RecordsDropped").TrackValue(stats.RecordsDropped);
    }

    private Task HandleRequestAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path;
        var method = ctx.Request.Method;

        if (path == s_rollupPath)
        {
            return HttpMethods.IsPost(method)
                ? IngestAsync(ctx)
                : WriteMethodNotAllowedAsync(ctx.Response, "POST");
        }

        if (path == s_statusPath)
        {
            if (!HttpMethods.IsGet(method))
            {
                return WriteMethodNotAllowedAsync(ctx.Response, "GET");
            }

            var query = ctx.Request.Query;
            var status = _store.Query(query["user"], query["model"], ReadWindow(query));
            return WriteJsonAsync(ctx.Response, status, MetricsJsonContext.Default.StatusResponse);
        }

        if (path == s_seriesPath)
        {
            if (!HttpMethods.IsGet(method))
            {
                return WriteMethodNotAllowedAsync(ctx.Response, "GET");
            }

            var query = ctx.Request.Query;
            var series = _store.QuerySeries(query["user"], query["model"], ReadWindow(query));
            return WriteJsonAsync(ctx.Response, series, MetricsJsonContext.Default.SeriesResponse);
        }

        if (path == s_usersPath)
        {
            if (!HttpMethods.IsGet(method))
            {
                return WriteMethodNotAllowedAsync(ctx.Response, "GET");
            }

            var users = _store.ListUsers(ctx.Request.Query["model"]);
            return WriteJsonAsync(ctx.Response, users, MetricsJsonContext.Default.NamesResponse);
        }

        if (path == s_modelsPath)
        {
            if (!HttpMethods.IsGet(method))
            {
                return WriteMethodNotAllowedAsync(ctx.Response, "GET");
            }

            var models = _store.ListModels(ctx.Request.Query["user"]);
            return WriteJsonAsync(ctx.Response, models, MetricsJsonContext.Default.NamesResponse);
        }

        if (path == s_statsPath)
        {
            return HttpMethods.IsGet(method)
                ? WriteJsonAsync(ctx.Response, _store.Stats(), MetricsJsonContext.Default.StatsResponse)
                : WriteMethodNotAllowedAsync(ctx.Response, "GET");
        }

        if (path == s_healthPath || path == s_livenessPath || path == s_readinessPath)
        {
            if (HttpMethods.IsHead(method))
            {
                WriteProbeHeaders(ctx.Response);
                return Task.CompletedTask;
            }

            if (HttpMethods.IsGet(method))
            {
                WriteProbeHeaders(ctx.Response);
                var destination = ctx.Response.BodyWriter.GetSpan(s_okBytes.Length);
                s_okBytes.AsSpan().CopyTo(destination);
                ctx.Response.BodyWriter.Advance(s_okBytes.Length);
                return Task.CompletedTask;
            }

            return WriteMethodNotAllowedAsync(ctx.Response, "GET, HEAD");
        }

        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        ctx.Response.ContentLength = 0;
        return Task.CompletedTask;
    }

    private async Task IngestAsync(HttpContext ctx)
    {
        byte[]? body;
        try
        {
            body = await ReadBodyAsync(ctx.Request.BodyReader, _options.MaxRequestBodyBytes, ctx.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (BadHttpRequestException)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status413PayloadTooLarge, "Request body too large.")
                .ConfigureAwait(false);
            return;
        }

        if (body is null)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status413PayloadTooLarge, "Request body too large.")
                .ConfigureAwait(false);
            return;
        }

        if (body.Length == 0)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status400BadRequest, "Request body is empty.")
                .ConfigureAwait(false);
            return;
        }

        var accepted = 0;
        var rejected = 0;
        var capacityReached = false;

        try
        {
            if (IsJsonArray(body))
            {
                var records = JsonSerializer.Deserialize(body, MetricsJsonContext.Default.RollupRecordArray);
                if (records is not null)
                {
                    foreach (var record in records)
                    {
                        Count(record, ref accepted, ref rejected, ref capacityReached);
                    }
                }
            }
            else if (HasRecordsProperty(body))
            {
                var batch = JsonSerializer.Deserialize(body, MetricsJsonContext.Default.RollupBatch);
                if (batch?.Records is { Count: > 0 } batchRecords)
                {
                    foreach (var record in batchRecords)
                    {
                        Count(record, ref accepted, ref rejected, ref capacityReached);
                    }
                }
            }
            else
            {
                var record = JsonSerializer.Deserialize(body, MetricsJsonContext.Default.RollupRecord);
                Count(record, ref accepted, ref rejected, ref capacityReached);
            }
        }
        catch (JsonException)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status400BadRequest, "Invalid JSON payload.")
                .ConfigureAwait(false);
            return;
        }

        var response = new IngestResponse { Accepted = accepted, Rejected = rejected };

        if (accepted > 0)
        {
            ctx.Response.StatusCode = StatusCodes.Status202Accepted;
        }
        else if (capacityReached)
        {
            // The series limit is a server capacity condition, so retrying later can succeed.
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }
        else if (rejected > 0)
        {
            // Stale timestamps and unusable records are client errors; retrying will not help.
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
        else
        {
            ctx.Response.StatusCode = StatusCodes.Status202Accepted;
        }

        await WriteJsonBodyAsync(ctx.Response, response, MetricsJsonContext.Default.IngestResponse)
            .ConfigureAwait(false);
    }

    private void Count(RollupRecord? record, ref int accepted, ref int rejected, ref bool capacityReached)
    {
        var result = _store.Ingest(record);
        if (result == IngestResult.Accepted)
        {
            accepted++;
            return;
        }

        if (result == IngestResult.CapacityReached)
        {
            capacityReached = true;
        }

        rejected++;
    }

    private static int ReadWindow(IQueryCollection query)
    {
        var raw = query["window"].ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var window) ? window : 0;
    }

    private static bool HasRecordsProperty(ReadOnlySpan<byte> body) =>
        body.IndexOf(s_recordsProperty) >= 0;

    private static bool IsJsonArray(ReadOnlySpan<byte> body)
    {
        foreach (var b in body)
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                continue;
            }

            return b == (byte)'[';
        }

        return false;
    }

    private static async Task<byte[]?> ReadBodyAsync(PipeReader reader, int maxBytes, CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (buffer.Length > maxBytes)
            {
                reader.AdvanceTo(buffer.Start, buffer.End);

                // Stop reading the oversized body instead of leaving it pending on the pipe.
                await reader.CompleteAsync().ConfigureAwait(false);
                return null;
            }

            if (result.IsCompleted)
            {
                var body = buffer.ToArray();
                reader.AdvanceTo(buffer.End);
                return body;
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
        }
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

    private static Task WriteErrorAsync(HttpResponse response, int statusCode, string message)
    {
        response.StatusCode = statusCode;
        return WriteJsonBodyAsync(response, new ErrorResponse { Error = message }, MetricsJsonContext.Default.ErrorResponse);
    }

    private static Task WriteJsonAsync<T>(HttpResponse response, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        response.StatusCode = StatusCodes.Status200OK;
        return WriteJsonBodyAsync(response, value, typeInfo);
    }

    private static Task WriteJsonBodyAsync<T>(HttpResponse response, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        response.ContentType = "application/json";
        response.Headers["Cache-Control"] = "no-cache";

        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        response.ContentLength = bytes.Length;
        return response.BodyWriter.WriteAsync(bytes, response.HttpContext.RequestAborted).AsTask();
    }

    /// <summary>
    /// Stops the metrics server gracefully.
    /// </summary>
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
