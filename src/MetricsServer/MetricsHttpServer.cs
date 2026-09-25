using System.Buffers;
using System.Collections.Frozen;
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

using SimpleL7Proxy.Tokenomics;

namespace MetricsServer;

/// <summary>
/// Kestrel-based metrics server. Accepts rollup data from other services and answers rolled up
/// status queries for users and models. All state is kept in memory only.
/// </summary>
public sealed class MetricsHttpServer : BackgroundService
{
    /// <summary>
    /// Identity extracted once per request from the <c>u</c> (user) and <c>b</c> (batch) query
    /// parameters.
    /// </summary>
    public readonly record struct RequestIdentity(string? UserId, string? BatchId);

    private static readonly byte[] s_okBytes = Encoding.UTF8.GetBytes("OK\n");
    private static readonly byte[] s_recordsProperty = Encoding.UTF8.GetBytes("\"records\"");

    private readonly ILogger<MetricsHttpServer> _logger;
    private readonly MetricsOptions _options;
    private readonly MetricsStore _store;
    private readonly TelemetryClient? _telemetryClient;
    private readonly TokenomicsRollupProcessor _tokenomicsRollupProcessor;
    private readonly TokenomicsMetricsStore _tokenomicsMetricsStore;
    public readonly FrozenDictionary<string, (Func<HttpContext, RequestIdentity, Task> Dispatch, bool AllowHead)> getMap;
    private readonly FrozenDictionary<string, Func<HttpContext, RequestIdentity, Task>> _postRoutes;
    private WebApplication? _app;

    public MetricsHttpServer(
        ILogger<MetricsHttpServer> logger,
        MetricsOptions options,
        MetricsStore store,
        TokenomicsRollupProcessor tokenomicsRollupProcessor,
        TokenomicsMetricsStore tokenomicsMetricsStore,
        TelemetryClient? telemetryClient = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _tokenomicsRollupProcessor = tokenomicsRollupProcessor ?? throw new ArgumentNullException(nameof(tokenomicsRollupProcessor));
        _tokenomicsMetricsStore = tokenomicsMetricsStore ?? throw new ArgumentNullException(nameof(tokenomicsMetricsStore));
        _telemetryClient = telemetryClient;

        getMap = new Dictionary<string, (Func<HttpContext, RequestIdentity, Task> Dispatch, bool AllowHead)>(StringComparer.OrdinalIgnoreCase)
        {
            [Constants.Health] = (Health, true),
            [Constants.Liveness] = (Liveness, true),
            [Constants.Readiness] = (Readiness, true),
            [Constants.Status] = (Status, false),
            [Constants.Users] = (Users, false),
            [Constants.Models] = (Models, false),
            [Constants.Series] = (Series, false),
            [Constants.Stats] = (Stats, false),
            [Constants.TokenomicsLookup] = (TokenomicsLookup, false)
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        _postRoutes = new Dictionary<string, Func<HttpContext, RequestIdentity, Task>>(StringComparer.OrdinalIgnoreCase)
        {
            [Constants.TokenomicsUpload] = TokenomicsUploadAsync
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
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
            MaintenanceLoopAsync(cancellationToken),
            _tokenomicsRollupProcessor.RunAsync(cancellationToken)).ConfigureAwait(false);
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
        var path = ctx.Request.Path.Value ?? string.Empty;
        var method = ctx.Request.Method;
        var identity = ParseIdentity(ctx.Request.Query);

        if (HttpMethods.IsPost(method) && _postRoutes.TryGetValue(path, out var postRoute))
        {
            return postRoute(ctx, identity);
        }

        if (getMap.TryGetValue(path, out var getRoute))
        {
            if (HttpMethods.IsGet(method)
                || (getRoute.AllowHead && HttpMethods.IsHead(method)))
            {
                return getRoute.Dispatch(ctx, identity);
            }

            return WriteMethodNotAllowedAsync(ctx.Response, getRoute.AllowHead ? "GET, HEAD" : "GET");
        }

        if (_postRoutes.ContainsKey(path))
        {
            return WriteMethodNotAllowedAsync(ctx.Response, "POST");
        }

        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        ctx.Response.ContentLength = 0;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Extracts the <c>u</c> (user) and <c>b</c> (batch) query parameters once per request so
    /// every GET and POST handler shares the same parsed identity.
    /// </summary>
    private static RequestIdentity ParseIdentity(IQueryCollection query)
    {
        var userId = query.TryGetValue("u", out var u) ? u.ToString() : null;
        var batchId = query.TryGetValue("b", out var b) ? b.ToString() : null;
        return new RequestIdentity(
            string.IsNullOrEmpty(userId) ? null : userId,
            string.IsNullOrEmpty(batchId) ? null : batchId);
    }

    public static Task Health(HttpContext ctx, RequestIdentity identity) => HandleProbeAsync(ctx);

    public static Task Liveness(HttpContext ctx, RequestIdentity identity) => HandleProbeAsync(ctx);

    public static Task Readiness(HttpContext ctx, RequestIdentity identity) => HandleProbeAsync(ctx);

    public Task Status(HttpContext ctx, RequestIdentity identity)
    {
        var query = ctx.Request.Query;
        var status = _store.Query(query["user"], query["model"], ReadWindow(query));
        return WriteJsonAsync(ctx.Response, status, MetricsJsonContext.Default.StatusResponse);
    }

    public Task Users(HttpContext ctx, RequestIdentity identity)
    {
        var users = _store.ListUsers(ctx.Request.Query["model"]);
        return WriteJsonAsync(ctx.Response, users, MetricsJsonContext.Default.NamesResponse);
    }

    public Task Models(HttpContext ctx, RequestIdentity identity)
    {
        var models = _store.ListModels(ctx.Request.Query["user"]);
        return WriteJsonAsync(ctx.Response, models, MetricsJsonContext.Default.NamesResponse);
    }

    public Task Series(HttpContext ctx, RequestIdentity identity)
    {
        var query = ctx.Request.Query;
        var series = _store.QuerySeries(query["user"], query["model"], ReadWindow(query));
        return WriteJsonAsync(ctx.Response, series, MetricsJsonContext.Default.SeriesResponse);
    }

    public Task Stats(HttpContext ctx, RequestIdentity identity) =>
        WriteJsonAsync(ctx.Response, _store.Stats(), MetricsJsonContext.Default.StatsResponse);

    public Task TokenomicsLookup(HttpContext ctx, RequestIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.UserId))
        {
            return WriteErrorAsync(ctx.Response, StatusCodes.Status400BadRequest, "Missing required 'u' query parameter.");
        }

        var model = ctx.Request.Query.TryGetValue("m", out var modelValue)
            ? modelValue.ToString()
            : null;
        if (string.IsNullOrWhiteSpace(model))
        {
            return WriteErrorAsync(ctx.Response, StatusCodes.Status400BadRequest, "Missing required 'm' query parameter.");
        }

        var response = _tokenomicsMetricsStore.GetMetrics(identity.UserId, model);
        return WriteJsonAsync(
            ctx.Response,
            response,
            MetricsJsonContext.Default.ResponseMetric);
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

    /// <summary>
    /// Single upload endpoint for tokenomics data pushed by proxy instances. Accepts a CSV batch
    /// of token/spend deltas and enqueues the raw body for later processing. Parsing happens on
    /// the dequeue side, inside <see cref="TokenomicsRollupProcessor.RunAsync"/>. This replaces
    /// the former separate rollups/outcomes/model-throttles routes; token, safety, status, and
    /// latency deltas are sent and parsed in the same rollup rows.
    /// 
    /// PAYLOAD FORMAT:
    /// ReplicaId: <replica-id>
    /// BatchId: <batch-id>
    /// <csv-headers><csv-content>
    /// 
    /// BatchId: <batch-id>
    /// <csv-headers><csv-content>
    /// 
    /// </summary>
    private async Task TokenomicsUploadAsync(HttpContext ctx, RequestIdentity identity)
    {
        try
        {
            var rlr = new ReplicaPayloadReader(ctx.Request.BodyReader);
            var replicaPayload = await rlr.ReadAsync();

            foreach (KeyValuePair<string, string> batch in replicaPayload.Batches)
            {
                _tokenomicsRollupProcessor.Enqueue(replicaPayload.ReplicaId, batch.Key, batch.Value);
            }

            var response = new MetricsServerResponse
            {
                BatchId = identity.BatchId,
                ProcessedBatches = _tokenomicsRollupProcessor.PeekRecentBatches(replicaPayload.ReplicaId),
                PendingBatches = [.. _tokenomicsRollupProcessor.GetPendingBatches(replicaPayload.ReplicaId)]
            };

            ctx.Response.StatusCode = StatusCodes.Status202Accepted;
            await WriteJsonBodyAsync(ctx.Response, response, MetricsJsonContext.Default.MetricsServerResponse)
            .ConfigureAwait(false);


        }
        catch (BadHttpRequestException)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status413PayloadTooLarge, "Request body too large.")
                .ConfigureAwait(false);
            return;
        }

    }

    private async Task IngestAsync(HttpContext ctx, RequestIdentity identity)
    {

        var response = new IngestResponse { Accepted = 0, Rejected = 0 };
        await WriteJsonBodyAsync(ctx.Response, response, MetricsJsonContext.Default.IngestResponse)
            .ConfigureAwait(false);
    }
    /// <summary>
    /// Parses the payload into individual batches.
    /// </summary>
    /// <param name="data">The raw payload data as an array of byte arrays.</param>
    /// <remarks>
    /// PAYLOAD FORMAT:
    /// ReplicaId: <replica-id>
    /// BatchId: <batch-id>
    /// <csv-headers><csv-content>
    /// 
    /// BatchId: <batch-id>
    /// <csv-headers><csv-content>
    /// 
    /// </remarks>
    public void parsePayload(byte[] data)
    {
        var payloadText = Encoding.UTF8.GetString(data);
        var payloadLines = payloadText.Split('\n');

        string? replicaId = null;
        string? currentBatchId = null;
        var currentContent = new StringBuilder();

        foreach (var rawLine in payloadLines)
        {
            var line = rawLine.TrimEnd('\r');

            if (replicaId is null && line.StartsWith("ReplicaId:", StringComparison.OrdinalIgnoreCase))
            {
                replicaId = line["ReplicaId:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("BatchId:", StringComparison.OrdinalIgnoreCase))
            {
                if (currentBatchId is not null)
                {
                    // Process the previous batch here if needed
                }

                currentBatchId = line["BatchId:".Length..].Trim();
                currentContent.Clear();
                continue;
            }

            if (currentBatchId is not null)
            {
                currentContent.AppendLine(line);
            }
        }

        if (currentBatchId is not null)
        {
            // Process the last batch here if needed
        }
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
