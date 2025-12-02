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

    // ========== STATE TRANSITIONS ==========

    /// <summary>
    /// Transition request to Processing state based on request type.
    /// Called when request is dequeued and ready for processing.
    /// </summary>
    public void TransitionToProcessing(RequestData request)
    {
        var oldStatus = request.SBStatus;
        
        switch (request.Type)
        {
            case RequestType.AsyncBackgroundCheck:
                SetStatus(request, ServiceBusMessageStatusEnum.CheckingBackgroundRequestStatus, null);
                _logger.LogDebug("[Lifecycle:{Guid}] State transition: {OldStatus} → CheckingBackgroundRequestStatus - Type: {RequestType}", 
                    request.Guid, oldStatus, request.Type);
                break;
            
            default:
                SetStatus(request, ServiceBusMessageStatusEnum.Processing, null);
                _logger.LogDebug("[Lifecycle:{Guid}] State transition: {OldStatus} → Processing - Type: {RequestType}", 
                    request.Guid, oldStatus, request.Type);
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
                _logger.LogDebug("[Lifecycle:{Guid}] Sync request processed successfully - Status: {StatusCode}", 
                    request.Guid, statusCode);
                break;

            case RequestType.Async:
                SetStatus(request, ServiceBusMessageStatusEnum.AsyncProcessed, RequestAPIStatusEnum.Completed);
                _logger.LogInformation("[Lifecycle:{Guid}] Async request completed successfully - Status: {StatusCode}", 
                    request.Guid, statusCode);
                break;

            case RequestType.AsyncBackground:
                SetStatus(request, ServiceBusMessageStatusEnum.BackgroundRequestSubmitted, RequestAPIStatusEnum.BackgroundProcessing);
                _logger.LogInformation("[Lifecycle:{Guid}] Background request submitted - BackgroundRequestId: {BackgroundRequestId}, Status: {StatusCode}", 
                    request.Guid, request.BackgroundRequestId, statusCode);
                break;

            case RequestType.AsyncBackgroundCheck:
                if (request.BackgroundRequestCompleted)
                {
                    SetStatus(request, ServiceBusMessageStatusEnum.AsyncProcessed, RequestAPIStatusEnum.Completed);
                    _logger.LogInformation("[Lifecycle:{Guid}] Background check completed successfully - Status: {StatusCode}", 
                        request.Guid, statusCode);
                }
                else
                {
                    SetStatus(request, ServiceBusMessageStatusEnum.CheckingBackgroundRequestStatus, RequestAPIStatusEnum.BackgroundProcessing);
                    _logger.LogInformation("[Lifecycle:{Guid}] Background check still processing - Status: {StatusCode}", 
                        request.Guid, statusCode);
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

    // ========== PRIVATE HELPERS ==========

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
}
