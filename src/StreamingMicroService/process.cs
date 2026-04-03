using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SimpleL7Proxy.StreamProcessor;

namespace StreamingMicroService;

public class process
{
    private readonly ILogger<process> _logger;
    private readonly StreamProcessorFactory _processorFactory;
    private readonly TelemetryClient _telemetryClient;
    private static readonly HttpClient _httpClient = new();
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
        var url = req.Query["url"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new BadRequestObjectResult("Missing or invalid 'url' query parameter.");

        _logger.LogInformation("Streaming from {Url}", url);

        using var upstreamResponse = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        upstreamResponse.EnsureSuccessStatusCode();

        var mediaType = upstreamResponse.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var explicitProcessor = req.Query["processor"].FirstOrDefault();
        var processorName = !string.IsNullOrWhiteSpace(explicitProcessor)
            ? explicitProcessor
            : StreamProcessorFactory.DetermineStreamProcessor(upstreamResponse, mediaType);

        var processor = _processorFactory.GetStreamProcessor(processorName, out var resolvedName);
        _logger.LogInformation("Using stream processor: {Processor}", resolvedName);

        req.HttpContext.Response.ContentType =
            upstreamResponse.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";

        await processor.CopyToAsync(upstreamResponse.Content, req.HttpContext.Response.Body);

        var stats = new Dictionary<string, string>(_baseStats);
        processor.GetStats(stats, upstreamResponse.Headers);

        if (stats.Count > 0)
        {
            var telemetry = new EventTelemetry("StreamProcessor.Stats");
            foreach (var kvp in stats)
            {
                // Console.WriteLine($"[Stats] {kvp.Key}: {kvp.Value}");
                telemetry.Properties[kvp.Key] = kvp.Value;
                if (double.TryParse(kvp.Value, out var metric))
                    _telemetryClient.GetMetric(kvp.Key).TrackValue(metric);
            }
            _telemetryClient.TrackEvent(telemetry);
        }

        return new EmptyResult();
    }
}
