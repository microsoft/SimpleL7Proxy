using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.Proxy;

namespace SimpleL7Proxy.Events;

/// <summary>
/// Builds and enriches event data for telemetry and logging purposes.
/// Handles population of request headers, response data, and event metadata.
/// </summary>
public class EventDataBuilder
{
    private readonly ILogger<EventDataBuilder> _logger;
    private readonly BackendOptions _options;

    public EventDataBuilder(ILogger<EventDataBuilder> logger, IOptions<BackendOptions> options)
    {
        _logger = logger;
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    // ========== HEADER ENRICHMENT ==========

    /// <summary>
    /// Enriches request headers with x-Request-* headers for tracking and debugging.
    /// Adds queue duration, process duration, worker ID, priority, and request type.
    /// </summary>
    public void EnrichRequestHeaders(RequestData request, string workerId)
    {
        request.Headers["x-Request-Queue-Duration"] = (request.DequeueTime! - request.EnqueueTime!).TotalMilliseconds.ToString();
        request.Headers["x-Request-Process-Duration"] = (DateTime.UtcNow - request.DequeueTime).TotalMilliseconds.ToString();
        request.Headers["x-Request-Worker"] = workerId;
        request.Headers["x-S7P-ID"] = request.MID ?? "N/A";
        request.Headers["x-S7PPriority"] = request.Priority.ToString();
        request.Headers["x-S7PPriority2"] = request.Priority2.ToString();
        request.Headers["x-Request-Type"] = request.Type.ToString();

        _logger.LogTrace("Enriched headers for request {Guid}", request.Guid);
    }

    // ========== EVENT DATA POPULATION ==========

    /// <summary>
    /// Populates initial event data when request processing begins.
    /// Sets event type, timestamps, and initial metrics.
    /// </summary>
    public void PopulateInitialEventData(RequestData request)
    {
        var eventData = request.EventData;
        
        eventData.Type = EventType.ProxyRequest;
        eventData["EnqueueTime"] = request.EnqueueTime.ToString("o");
        eventData["RequestContentLength"] = request.Headers["Content-Length"] ?? "N/A";
        eventData["RequestType"] = request.Type.ToString();
        eventData["Request-Queue-Duration"] = request.Headers["x-Request-Queue-Duration"] ?? "N/A";
        eventData["Request-Process-Duration"] = request.Headers["x-Request-Process-Duration"] ?? "N/A";

        _logger.LogTrace("Populated initial event data for request {Guid}", request.Guid);
    }

    /// <summary>
    /// Populates event data after proxying to backend.
    /// Adds URL, latency, backend information, and attempt counts.
    /// </summary>
    public void PopulateProxyEventData(RequestData request, ProxyData proxyData)
    {
        var eventData = request.EventData;
        
        eventData["Url"] = request.FullURL;
        var timeTaken = DateTime.UtcNow - request.EnqueueTime;
        eventData.Duration = timeTaken;
        eventData["Total-Latency"] = timeTaken.TotalMilliseconds.ToString("F3");
        eventData["Attempts"] = request.BackendAttempts.ToString();
        
        if (proxyData != null)
        {
            eventData["Backend-Host"] = !string.IsNullOrEmpty(proxyData.BackendHostname) 
                ? proxyData.BackendHostname 
                : "N/A";
            eventData["Response-Latency"] = proxyData.ResponseDate != default && request.DequeueTime != default
                ? (proxyData.ResponseDate - request.DequeueTime).TotalMilliseconds.ToString("F3") 
                : "N/A";
            eventData["Average-Backend-Probe-Latency"] = proxyData.CalculatedHostLatency != 0 
                ? proxyData.CalculatedHostLatency.ToString("F3") + " ms" 
                : "N/A";
            
            if (proxyData.Headers != null)
            {
                proxyData.Headers["x-Attempts"] = request.BackendAttempts.ToString();
            }
        }

        _logger.LogTrace("Populated proxy event data for request {Guid}", request.Guid);
    }

    /// <summary>
    /// Populates event data with request and response headers based on configuration.
    /// Logs headers according to LogAllRequestHeaders, LogAllResponseHeaders, and LogHeaders settings.
    /// </summary>
    /// <param name="request">The request data</param>
    /// <param name="responseHeaders">The response headers from ProxyData</param>
    public void PopulateHeaderEventData(RequestData request, System.Collections.Specialized.NameValueCollection responseHeaders)
    {
        var eventData = request.EventData;

        // Log request headers if configured
        if (_options.LogAllRequestHeaders)
        {
            foreach (var header in request.Headers.AllKeys)
            {
                if (_options.LogAllRequestHeadersExcept == null || !_options.LogAllRequestHeadersExcept.Contains(header))
                {
                    eventData["Request-" + header] = request.Headers[header] ?? "N/A";
                }
            }
        }

        // Log response headers if configured
        if (_options.LogAllResponseHeaders)
        {
            foreach (var header in responseHeaders.AllKeys)
            {
                if (header != null && (_options.LogAllResponseHeadersExcept == null || !_options.LogAllResponseHeadersExcept.Contains(header)))
                {
                    eventData["Response-" + header] = responseHeaders[header] ?? "N/A";
                }
            }
        }
        else if (_options.LogHeaders?.Count > 0)
        {
            foreach (var header in _options.LogHeaders)
            {
                eventData["Response-" + header] = responseHeaders[header] ?? "N/A";
            }
        }

        _logger.LogTrace("Populated header event data for request {Guid}", request.Guid);
    }

    /// <summary>
    /// Populates final event data with response information and incomplete requests.
    /// </summary>
    public void PopulateFinalEventData(RequestData request, HttpListenerContext? context)
    {
        var eventData = request.EventData;
        
        eventData["Content-Length"] = context?.Response?.ContentLength64.ToString() ?? "N/A";
        eventData["Content-Type"] = context?.Response?.ContentType ?? "N/A";

        if (request.incompleteRequests != null)
        {
            ProxyHelperUtils.AddIncompleteRequestsToEventData(request.incompleteRequests, eventData);
        }

        _logger.LogTrace("Populated final event data for request {Guid}", request.Guid);
    }
}
