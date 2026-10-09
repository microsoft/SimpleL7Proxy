using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using SimpleL7Proxy.Tokenomics;

namespace MetricsServer;

/// <summary>
/// Request router and endpoint handlers for the metrics server. Owns route dispatch, the data
/// endpoint handlers, and the periodic maintenance/telemetry loop. Kestrel hosting and container
/// probes live in <see cref="MetricsWebServer"/>. All state is kept in memory only.
/// </summary>
public sealed class MetricsHttpServer : IDisposable
{
    /// <summary>
    /// Identity extracted once per request from the <c>u</c> (user) and <c>b</c> (batch) query
    /// parameters.
    /// </summary>
    public readonly record struct RequestIdentity(string? UserId, string? BatchId);

    private readonly ILogger<MetricsHttpServer> _logger;
    private readonly MetricsOptions _options;
    private readonly MetricsStore _store;
    private readonly TelemetryClient? _telemetryClient;
    private readonly TokenomicsRollupProcessor _tokenomicsRollupProcessor;
    private readonly TokenomicsMetricsStore _tokenomicsMetricsStore;
    private readonly AppConfigurationReader? _appConfigurationReader;
    public readonly FrozenDictionary<string, Func<HttpContext, RequestIdentity, Task>> getMap;
    private readonly FrozenDictionary<string, Func<HttpContext, RequestIdentity, Task>> _postRoutes;

    public MetricsHttpServer(
        ILogger<MetricsHttpServer> logger,
        MetricsOptions options,
        MetricsStore store,
        TokenomicsRollupProcessor tokenomicsRollupProcessor,
        TokenomicsMetricsStore tokenomicsMetricsStore,
        TelemetryClient? telemetryClient = null,
        AppConfigurationReader? appConfigurationReader = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _tokenomicsRollupProcessor = tokenomicsRollupProcessor ?? throw new ArgumentNullException(nameof(tokenomicsRollupProcessor));
        _tokenomicsMetricsStore = tokenomicsMetricsStore ?? throw new ArgumentNullException(nameof(tokenomicsMetricsStore));
        _telemetryClient = telemetryClient;
        _appConfigurationReader = appConfigurationReader;

        getMap = new Dictionary<string, Func<HttpContext, RequestIdentity, Task>>(StringComparer.OrdinalIgnoreCase)
        {
            [Constants.Status] = Status,
            [Constants.Users] = Users,
            [Constants.Models] = Models,
            [Constants.Series] = Series,
            [Constants.Stats] = Stats,
            [Constants.TokenomicsLookup] = TokenomicsLookup
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        _postRoutes = new Dictionary<string, Func<HttpContext, RequestIdentity, Task>>(StringComparer.OrdinalIgnoreCase)
        {
            [Constants.TokenomicsUpload] = TokenomicsUploadAsync
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether Application Insights telemetry is configured.</summary>
    public bool TelemetryEnabled => _telemetryClient is not null;

    /// <summary>
    /// Routes one request to its handler. Wired to the Kestrel pipeline by the web server after
    /// probe paths have been handled.
    /// </summary>
    public Task HandleRequestAsync(HttpContext ctx)
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
            if (HttpMethods.IsGet(method))
            {
                return getRoute(ctx, identity);
            }

            return WriteMethodNotAllowedAsync(ctx.Response, "GET");
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
    /// Periodically removes idle series and publishes server counters to Application Insights.
    /// Driven by the web server so it shares the host's lifetime.
    /// </summary>
    public async Task RunMaintenanceAsync(CancellationToken cancellationToken)
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

        var response = _tokenomicsMetricsStore.GetMetrics(identity.UserId, model, _appConfigurationReader?.TokenomicsSettings);
        return WriteJsonAsync(
            ctx.Response,
            response,
            MetricsJsonContext.Default.ResponseMetric);
    }

    /// <summary>
    /// Single upload endpoint for tokenomics data pushed by proxy instances. The replica id is read
    /// from the <c>r</c> query parameter. The body carries a manifest line listing the batch ids,
    /// followed by the per-batch CSV sections. This handler reads the body once and parses only the
    /// manifest; CSV parsing happens on the dequeue side in the rollup processor.
    ///
    /// URL:  POST /tokenomics/metrics/upload?r=&lt;replica-id&gt;
    /// BODY:
    /// Tokenomics-Version: 1
    /// BatchIds: &lt;id1&gt;,&lt;id2&gt;,...
    /// BatchId: &lt;id1&gt;
    /// &lt;csv-header&gt;&lt;csv-rows&gt;
    /// BatchId: &lt;id2&gt;
    /// &lt;csv-header&gt;&lt;csv-rows&gt;
    /// </summary>
    private async Task TokenomicsUploadAsync(HttpContext ctx, RequestIdentity identity)
    {
        try
        {
            var replicaId = ctx.Request.Query.TryGetValue("r", out var r) && !string.IsNullOrEmpty(r)
                ? r.ToString()
                : "unknown";

            // Read the whole content-length body once; the reader parses only the manifest line.
            var reader = new ReplicaPayloadReader(ctx.Request.BodyReader);
            var payload = await reader.ReadAsync().ConfigureAwait(false);

            // One enqueue per upload; the rollup iterator parses the CSV sections later.
            _tokenomicsRollupProcessor.Enqueue(replicaId, payload.BatchIds, payload.Body);

            var response = new MetricsServerResponse
            {
                BatchId = identity.BatchId,
                ProcessedBatches = _tokenomicsRollupProcessor.PeekRecentBatches(replicaId),
                PendingBatches = _tokenomicsRollupProcessor.GetPendingBatches(replicaId)
            };

            ctx.Response.StatusCode = StatusCodes.Status202Accepted;
            await WriteJsonBodyAsync(ctx.Response, response, MetricsJsonContext.Default.MetricsServerResponse)
                .ConfigureAwait(false);
        }
        catch (BadHttpRequestException)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status413PayloadTooLarge, "Request body too large.")
                .ConfigureAwait(false);
        }
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

    // Present so callers can own the handler with a `using` scope; nothing to release.
    public void Dispose()
    {
    }
}