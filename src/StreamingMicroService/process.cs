//#define LLM_CACHE_UPSTREAM_RESPONSE

using System.Globalization;
#if LLM_CACHE_UPSTREAM_RESPONSE
using System.Collections.Concurrent;
using System.Net;
#endif
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SimpleL7Proxy.StreamProcessor;
using SharedParser;
using SharedParser.Tokenomics.LLM.Models;

namespace StreamingMicroService;

public class process
{
    private readonly ILogger<process> _logger;
    private readonly StreamProcessorFactory _processorFactory;
    private readonly TelemetryClient _telemetryClient;
    private static readonly HttpClient _httpClient = new();
#if LLM_CACHE_UPSTREAM_RESPONSE
    private static readonly ConcurrentDictionary<Uri, (byte[] Body, HttpStatusCode StatusCode,
        KeyValuePair<string, string[]>[] Headers, KeyValuePair<string, string[]>[] ContentHeaders)> _responseCache = new();
    private static readonly SemaphoreSlim _responseCacheLock = new(1, 1);
#endif
    private static readonly Dictionary<string, string> _baseStats = new()
    {
        ["Module"] = VersionInfo.Module,
        ["Ver"] = VersionInfo.Version
    };

    public process(ILogger<process> logger, StreamProcessorFactory processorFactory, TelemetryClient telemetryClient)
    {
        _logger = logger;
        _processorFactory = processorFactory;
        _telemetryClient = telemetryClient;
    }

    [Function("process")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req)
    {
        var explicitProcessor = new NonNullString(req.Query["processor"].FirstOrDefault() ?? string.Empty);
#if LLM_CACHE_UPSTREAM_RESPONSE
        if (explicitProcessor.Value.Equals("ReSeTCaChe", StringComparison.OrdinalIgnoreCase))
        {
            await _responseCacheLock.WaitAsync(req.HttpContext.RequestAborted);
            try
            {
                _responseCache.Clear();
            }
            finally
            {
                _responseCacheLock.Release();
            }
            return new OkObjectResult("Response cache cleared.");
        }
#endif
        var url = new NonNullString(req.Query["url"].FirstOrDefault() ?? string.Empty);
        if (!Uri.TryCreate(url.Value, UriKind.Absolute, out var uri))
            return new BadRequestObjectResult("Missing or invalid 'url' query parameter.");

        ValidModel? validatedSourceModel = null;
        ValidModel? validatedTargetModel = null;
        if (req.Query.ContainsKey("sourceModel") || req.Query.ContainsKey("targetModel"))
        {
            var sourceModel = new NonNullString(req.Query["sourceModel"].FirstOrDefault() ?? string.Empty);
            var targetModel = new NonNullString(req.Query["targetModel"].FirstOrDefault() ?? string.Empty);

            if (!ValidModel.TryParse(sourceModel, out validatedSourceModel)
                || !ValidModel.TryParse(targetModel, out validatedTargetModel))
            {
                validatedSourceModel = null;
                validatedTargetModel = null;
                _logger.LogDebug("'sourceModel' and 'targetModel' must be valid model.APIFormat values. Using normal stream processing.");
            }
        }

        _logger.LogInformation("Streaming from {Url}", url.Value);

        using var upstreamResponse = await GetUpstreamResponseAsync(uri, req.HttpContext.RequestAborted);
        upstreamResponse.EnsureSuccessStatusCode();

        var mediaType = new NonNullString(upstreamResponse.Content.Headers.ContentType?.MediaType ?? string.Empty);
        var streaming = mediaType.Value.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase);
        var processor = ResolveProcessor(explicitProcessor, validatedSourceModel, validatedTargetModel,
            upstreamResponse, mediaType.Value, streaming, out var resolvedName);

        try
        {
            _logger.LogInformation("Content-Type: {ContentType}, processor selected: {Processor}",
                upstreamResponse.Content.Headers.ContentType?.ToString() ?? "(missing)", resolvedName);

            req.HttpContext.Response.ContentType =
                resolvedName == "ModelMapping"
                    ? streaming ? "text/event-stream; charset=utf-8" : "application/json; charset=utf-8"
                    : upstreamResponse.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";

            await processor.CopyToAsync(upstreamResponse.Content, req.HttpContext.Response.Body, debug: false);
            TrackProcessorStats(processor, upstreamResponse);
        }
        finally
        {
            if (!string.Equals(resolvedName, StreamProcessorFactory.DEFAULT_PROCESSOR, StringComparison.OrdinalIgnoreCase))
                processor.Dispose();
        }

        return new EmptyResult();
    }

    private static async Task<HttpResponseMessage> GetUpstreamResponseAsync(Uri uri, CancellationToken cancellationToken)
    {
#if LLM_CACHE_UPSTREAM_RESPONSE
        if (!_responseCache.TryGetValue(uri, out var cachedResponse))
        {
            await _responseCacheLock.WaitAsync(cancellationToken);
            try
            {
                if (!_responseCache.TryGetValue(uri, out cachedResponse))
                {
                    using var fetchedResponse = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    fetchedResponse.EnsureSuccessStatusCode();
                    var body = await fetchedResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                    cachedResponse = (body, fetchedResponse.StatusCode,
                        fetchedResponse.Headers.Select(header => new KeyValuePair<string, string[]>(header.Key, header.Value.ToArray())).ToArray(),
                        fetchedResponse.Content.Headers.Select(header => new KeyValuePair<string, string[]>(header.Key, header.Value.ToArray())).ToArray());
                    _responseCache.TryAdd(uri, cachedResponse);
                }
            }
            finally
            {
                _responseCacheLock.Release();
            }
        }
        var upstreamResponse = new HttpResponseMessage(cachedResponse.StatusCode)
        {
            Content = new ByteArrayContent(cachedResponse.Body)
        };
        foreach (var header in cachedResponse.Headers)
            upstreamResponse.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var header in cachedResponse.ContentHeaders)
            upstreamResponse.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return upstreamResponse;
#else
        return await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
#endif
    }

    private IStreamProcessor ResolveProcessor(NonNullString explicitProcessor, ValidModel? validatedSourceModel,
        ValidModel? validatedTargetModel, HttpResponseMessage upstreamResponse, string mediaType,
        bool streaming, out string resolvedName)
    {
        if (validatedSourceModel is not null && validatedTargetModel is not null &&
            (validatedSourceModel.Name.Value != validatedTargetModel.Name.Value ||
             validatedSourceModel.ApiFormat != validatedTargetModel.ApiFormat))
        {
            return _processorFactory.GetMapStreamProcessor(validatedSourceModel, validatedTargetModel, out resolvedName, streaming);
        }

        var processorName = !explicitProcessor.Value.AsSpan().IsWhiteSpace()
            ? explicitProcessor.Value
            : StreamProcessorFactory.DetermineStreamProcessor(upstreamResponse, mediaType);
        return _processorFactory.GetStreamProcessor(processorName, out resolvedName);
    }

    private void TrackProcessorStats(IStreamProcessor processor, HttpResponseMessage upstreamResponse)
    {
        var stats = new Dictionary<string, string>(_baseStats);
        processor.GetStats(stats, upstreamResponse.Headers);

        var telemetry = new EventTelemetry("StreamProcessor.Stats");
        foreach (var kvp in stats)
        {
            telemetry.Properties[kvp.Key] = kvp.Value;
            if (double.TryParse(kvp.Value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var metric))
                _telemetryClient.GetMetric(kvp.Key).TrackValue(metric);
        }
        _telemetryClient.TrackEvent(telemetry);
    }
}
