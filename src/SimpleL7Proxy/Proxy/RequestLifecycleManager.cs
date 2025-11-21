using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.ServiceBus;
using Shared.RequestAPI.Models;

namespace SimpleL7Proxy.Proxy;

/// <summary>
/// Manages the lifecycle and status transitions of requests through the proxy.
/// Centralizes all status update logic to ensure consistency and prevent bugs
/// where RequestAPIStatus is not properly set for async requests.
/// </summary>
public class RequestLifecycleManager
{
    private readonly ILogger<RequestLifecycleManager> _logger;
    private readonly BackendOptions _options;

    public RequestLifecycleManager(ILogger<RequestLifecycleManager> logger, IOptions<BackendOptions> options)
    {
        _logger = logger;
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    // ========== STATE TRANSITIONS ==========

    /// <summary>
    /// Transition request to Processing state based on request type.
    /// Called when request is dequeued and ready for processing.
    /// </summary>
    public void TransitionToProcessing(RequestData request)
    {
        switch (request.Type)
        {
            case RequestType.AsyncBackgroundCheck:
                SetStatus(request, ServiceBusMessageStatusEnum.CheckingBackgroundRequestStatus, null);
                _logger.LogDebug("Request {Guid} (BackgroundCheck) transitioned to CheckingBackgroundRequestStatus", request.Guid);
                break;
            
            default:
                SetStatus(request, ServiceBusMessageStatusEnum.Processing, null);
                _logger.LogDebug("Request {Guid} ({Type}) transitioned to Processing", request.Guid, request.Type);
                break;
        }
    }

    /// <summary>
    /// Transition request to success state based on request type.
    /// Called when backend returns HTTP 200 OK.
    /// </summary>
    public void TransitionToSuccess(RequestData request, HttpStatusCode statusCode)
    {
        switch (request.Type)
        {
            case RequestType.Sync:
                SetStatus(request, ServiceBusMessageStatusEnum.Processed, null);
                _logger.LogDebug("Sync request {Guid} processed successfully", request.Guid);
                break;

            case RequestType.Async:
                SetStatus(request, ServiceBusMessageStatusEnum.AsyncProcessed, RequestAPIStatusEnum.Completed);
                _logger.LogInformation("Async request {Guid} completed successfully", request.Guid);
                break;

            case RequestType.AsyncBackground:
                SetStatus(request, ServiceBusMessageStatusEnum.BackgroundRequestSubmitted, RequestAPIStatusEnum.BackgroundProcessing);
                _logger.LogInformation("Background request {Guid} submitted with ID {BackgroundRequestId}", 
                    request.Guid, request.BackgroundRequestId);
                break;

            case RequestType.AsyncBackgroundCheck:
                if (request.BackgroundRequestCompleted)
                {
                    SetStatus(request, ServiceBusMessageStatusEnum.AsyncProcessed, RequestAPIStatusEnum.Completed);
                    _logger.LogInformation("Background check {Guid} completed successfully", request.Guid);
                }
                else
                {
                    SetStatus(request, ServiceBusMessageStatusEnum.CheckingBackgroundRequestStatus, RequestAPIStatusEnum.BackgroundProcessing);
                    _logger.LogInformation("Background check {Guid} still processing", request.Guid);
                }
                break;
        }
    }

    /// <summary>
    /// Transition request to failed state based on request type.
    /// Called when backend returns non-200 status or exceptions occur.
    /// </summary>
    public void TransitionToFailed(RequestData request, HttpStatusCode statusCode, string? reason = null)
    {
        switch (request.Type)
        {
            case RequestType.Sync:
                SetStatus(request, ServiceBusMessageStatusEnum.Failed, null);
                _logger.LogWarning("Sync request {Guid} failed with status {StatusCode}. Reason: {Reason}", 
                    request.Guid, statusCode, reason ?? "Unknown");
                break;

            case RequestType.Async:
            case RequestType.AsyncBackground:
            case RequestType.AsyncBackgroundCheck:
                SetStatus(request, ServiceBusMessageStatusEnum.Failed, RequestAPIStatusEnum.Failed);
                _logger.LogWarning("Request {Guid} (Type: {Type}) failed with status {StatusCode}. Reason: {Reason}", 
                    request.Guid, request.Type, statusCode, reason ?? "Unknown");
                break;
        }
    }

    /// <summary>
    /// Transition request to expired state.
    /// Called when request exceeds TTL or receives 412/408 status.
    /// </summary>
    public void TransitionToExpired(RequestData request)
    {
        SetStatus(request, ServiceBusMessageStatusEnum.Expired, 
            request.runAsync ? RequestAPIStatusEnum.Failed : null);
        
        _logger.LogWarning("Request {Guid} ({Type}) expired at {ExpiresAt}. Reason: {Reason}", 
            request.Guid, request.Type, request.ExpiresAt, request.ExpireReason);
    }

    /// <summary>
    /// Mark request as requeued. Status remains unchanged.
    /// Called when request is being retried with delay.
    /// </summary>
    public void TransitionToRequeued(RequestData request)
    {
        request.Requeued = true;
        _logger.LogInformation("Request {Guid} ({Type}) requeued for retry", request.Guid, request.Type);
    }

    // ========== VALIDATION ==========

    /// <summary>
    /// Validates that the request has not expired based on its ExpiresAt timestamp.
    /// Called before processing to prevent wasted backend calls.
    /// </summary>
    /// <param name="request">The request to validate</param>
    /// <exception cref="ProxyErrorException">Thrown when request has expired</exception>
    public void ValidateRequestNotExpired(RequestData request)
    {
        if (request.ExpiresAt < DateTimeOffset.UtcNow)
        {
            string errorMessage = $"Request has expired: Time: {DateTime.UtcNow:o}  Reason: {request.ExpireReason}";
            request.EventData["Disposition"] = "Expired";
            request.SkipDispose = false;
            
            _logger.LogWarning("Request {Guid} validation failed: expired at {ExpiresAt}. Reason: {Reason}", 
                request.Guid, request.ExpiresAt, request.ExpireReason);
            
            throw new ProxyErrorException(ProxyErrorException.ErrorType.TTLExpired,
                                        HttpStatusCode.PreconditionFailed,
                                        errorMessage);
        }
    }

    // ========== HELPER METHODS ==========

    /// <summary>
    /// Internal helper to set both SBStatus and RequestAPIStatus consistently.
    /// Ensures RequestAPIStatus is only set for async requests.
    /// </summary>
    private void SetStatus(RequestData request, ServiceBusMessageStatusEnum sbStatus, RequestAPIStatusEnum? apiStatus)
    {
        request.SBStatus = sbStatus;
        
        if (apiStatus.HasValue && request.runAsync)
        {
            request.RequestAPIStatus = apiStatus.Value;
        }
    }

    // ========== LIFECYCLE DECISION HELPERS ==========

    /// <summary>
    /// Determines if request should go through finalization block.
    /// Background requests skip finalization as their status is managed earlier.
    /// </summary>
    public bool ShouldFinalize(RequestData request)
    {
        return request.Type != RequestType.AsyncBackground && 
               request.Type != RequestType.AsyncBackgroundCheck;
    }

    /// <summary>
    /// Determines if request should be explicitly cleaned up and disposed.
    /// Background requests are disposed via 'await using' pattern instead.
    /// </summary>
    public bool ShouldCleanup(RequestData request, bool isRequeued, bool asyncExpelInProgress)
    {
        return !isRequeued &&
               !asyncExpelInProgress &&
               request.Type != RequestType.AsyncBackground &&
               request.Type != RequestType.AsyncBackgroundCheck;
    }

    // ========== STATUS QUERY HELPERS ==========

    /// <summary>
    /// Check if request has completed successfully.
    /// </summary>
    public bool IsCompleted(RequestData request)
    {
        return request.RequestAPIStatus == RequestAPIStatusEnum.Completed ||
               request.SBStatus == ServiceBusMessageStatusEnum.Processed ||
               request.SBStatus == ServiceBusMessageStatusEnum.AsyncProcessed;
    }

    /// <summary>
    /// Check if request has failed.
    /// </summary>
    public bool IsFailed(RequestData request)
    {
        return request.RequestAPIStatus == RequestAPIStatusEnum.Failed ||
               request.SBStatus == ServiceBusMessageStatusEnum.Failed ||
               request.SBStatus == ServiceBusMessageStatusEnum.Expired;
    }

    /// <summary>
    /// Check if request is currently being processed.
    /// </summary>
    public bool IsProcessing(RequestData request)
    {
        return request.RequestAPIStatus == RequestAPIStatusEnum.InProgress ||
               request.RequestAPIStatus == RequestAPIStatusEnum.BackgroundProcessing ||
               request.SBStatus == ServiceBusMessageStatusEnum.Processing ||
               request.SBStatus == ServiceBusMessageStatusEnum.CheckingBackgroundRequestStatus;
    }

    /// <summary>
    /// Finalizes the request status based on success/failure.
    /// Called for non-background requests only.
    /// </summary>
    public void FinalizeStatus(RequestData request, bool isSuccessful)
    {
        if (request.runAsync && ShouldFinalize(request))
        {
            request.RequestAPIStatus = isSuccessful 
                ? RequestAPIStatusEnum.Completed 
                : RequestAPIStatusEnum.Failed;
            
            _logger.LogDebug("Finalized request {Guid} status to {Status}", 
                request.Guid, request.RequestAPIStatus);
        }
    }

    // ========== ENRICHMENT AND EVENT DATA POPULATION ==========

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
                if (_options.LogAllResponseHeadersExcept == null || !_options.LogAllResponseHeadersExcept.Contains(header))
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

    // ========== BACKGROUND REQUEST MANAGEMENT ==========

    /// <summary>
    /// Handles background request lifecycle management after streaming completes.
    /// When a processor detects a background request ID during streaming (e.g., OpenAI batch API),
    /// we track it for periodic status polling via the background checker component.
    /// 
    /// SCENARIOS HANDLED:
    /// 1. Initial background request submission - Mark as BackgroundProcessing to trigger periodic polling
    /// 2. Background check found completed task - Update status to Completed so caller can retrieve final results
    /// 3. Background check found task still running - Keep incomplete status, will be polled again later
    /// </summary>
    /// <param name="request">The request being processed</param>
    /// <param name="processor">The stream processor that handled the response</param>
    public async Task HandleBackgroundRequestLifecycle(RequestData request, SimpleL7Proxy.StreamProcessor.IStreamProcessor processor)
    {
        if (string.IsNullOrEmpty(processor.BackgroundRequestId) || !request.runAsync)
        {
            _logger.LogDebug("No background request ID found in processor for request {Guid}", request.Guid);
            return;
        }

        _logger.LogInformation("Found background request for GUID: {Guid}, BackgroundRequestId: {BackgroundRequestId}",
            request.Guid, processor.BackgroundRequestId);
        
        request.IsBackground = true;
        request.BackgroundRequestId = processor.BackgroundRequestId;

        if (request.asyncWorker == null)
        {
            _logger.LogError("AsyncWorker is null but runAsync is true for request {Guid}", request.Guid);
            return;
        }

        // Handle three distinct scenarios based on request type and completion status:
        if (!request.IsBackgroundCheck)
        {
            // Scenario 1: Initial background request submission
            // Mark as BackgroundProcessing to trigger periodic polling
            _logger.LogInformation("Updating async worker for background GUID: {Guid}, BackgroundRequestId: {BackgroundRequestId}",
                request.Guid, processor.BackgroundRequestId);
            await request.asyncWorker.UpdateBackup().ConfigureAwait(false);
            request.BackgroundRequestCompleted = false;
        }
        else if (processor.BackgroundCompleted)
        {
            // Scenario 2: Background check found completed task
            // Update status to Completed so caller can retrieve final results
            _logger.LogInformation("Background processing completed for GUID: {Guid}, BackgroundRequestId: {BackgroundRequestId}",
                request.Guid, processor.BackgroundRequestId);
            request.BackgroundRequestCompleted = true;
        }
        else
        {
            // Scenario 3: Background check found task still running
            // retrigger the API to continue polling
            _logger.LogInformation("Background processing still in progress for GUID: {Guid}, BackgroundRequestId: {BackgroundRequestId}",
                request.Guid, processor.BackgroundRequestId);
            request.BackgroundRequestCompleted = false;
        }
    }
}
