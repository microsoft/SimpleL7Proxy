
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

using SimpleL7Proxy.Queue;
using SimpleL7Proxy.ServiceBus;
using Shared.RequestAPI.Models;


namespace SimpleL7Proxy.Proxy;


public class RequeueDelayWorker : IRequeueWorker, IDisposable
{
    private readonly ILogger<RequeueDelayWorker> _logger;
    private readonly IConcurrentPriQueue<RequestData> _requestsQueue;
    
    // Track delays by request GUID
    private readonly ConcurrentDictionary<Guid, Task> _activeDelays = new();
    
    // One cancellation source for all async requests
    private readonly CancellationTokenSource _asyncCancellationSource = new();

    public RequeueDelayWorker(ILogger<RequeueDelayWorker> logger, IConcurrentPriQueue<RequestData> requestsQueue)
    {
        _logger = logger;
        _requestsQueue = requestsQueue;
    }

    public void DelayAsync(RequestData request, int delayMs)
    {
        request.Requeued = true;
        request.SkipDispose = true;

        // Track this delay by request GUID - no global token passed
        _activeDelays[request.Guid] = DelayAndRequeueAsync(request, delayMs);
    }

    private Task DelayAndRequeueAsync(RequestData request, int delayMs)
    {
        // Detaches a new task that will requeue the request after the delay
        return Task.Run(async () =>
        {
            // Only async requests use cancellable delay
            Task delayTask;
            if (request.runAsync)
            {
                delayTask = Task.Delay(delayMs, _asyncCancellationSource.Token);
            }
            else
            {
                // Non-async requests use non-cancellable delay
                delayTask = Task.Delay(delayMs);
            }
            
            try
            {
                // Requeue the request after the retry-after value
                request.SBStatus = ServiceBusMessageStatusEnum.RetryScheduled;

                _logger.LogDebug("[RequeueDelayWorker] ⏳ Starting requeue delay of {DelayMs}ms for request {Guid}", 
                    delayMs, request.Guid);

                await delayTask.ConfigureAwait(false);

                request.SBStatus = ServiceBusMessageStatusEnum.Requeued;

                // Requeue the request
                _logger.LogCritical("Requeued request, Pri: {Priority}, Expires-At: {ExpiresAt}, GUID: {Guid}",
                    request.Priority, request.ExpiresAtString, request.Guid);
                    
                _requestsQueue.Requeue(request, request.Priority, request.Priority2, request.EnqueueTime);
            }
            catch (TaskCanceledException)
            {
                _logger.LogWarning("[RequeueDelayWorker] ⚠️ Requeue delay of {DelayMs}ms was cancelled for request {Guid}", 
                    delayMs, request.Guid);

                if (request.asyncWorker != null)
                {
                    _logger.LogInformation("Aborting async request, updating backup: {Guid}", request.Guid);
                    await request.asyncWorker.AbortAsync().ConfigureAwait(false);
                    _logger.LogInformation("AsyncWorker: Expel completed, backup updated: {Guid}", request.Guid);
                    request.asyncWorker = null;
                }
                else
                {
                    _logger.LogError("Async expel operation but asyncWorker is null for request {Guid}", request.Guid);
                }
            }
            finally
            {
                // Remove just this task
                _activeDelays.TryRemove(request.Guid, out _);
            }
        });
    }

    public async Task CancelAllCancelableTasks()
    {
        // Cancel only async requests
        _asyncCancellationSource.Cancel();
        
        // Wait for all active delays to complete (both cancellable and non-cancellable)
        if (_activeDelays.Count > 0)
        {
            await Task.WhenAll(_activeDelays.Values);
        }
    }
    
    public void Dispose()
    {
        _asyncCancellationSource.Dispose();
    }
}