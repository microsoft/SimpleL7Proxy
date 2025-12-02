using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;

using SimpleL7Proxy.Config;
using Shared.RequestAPI.Models;
using SimpleL7Proxy.ServiceBus;


namespace SimpleL7Proxy.BackupAPI
{
    public class BackupAPIService : IHostedService, IBackupAPIService
    {

        private readonly BackendOptions _options;
        private readonly ILogger<BackupAPIService> _logger;
        private static readonly ConcurrentQueue<RequestAPIDocument> _statusQueue = new();
        private readonly SemaphoreSlim _queueSignal = new SemaphoreSlim(0);
        private bool isShuttingDown = false;
        private Task? writerTask;
        CancellationTokenSource? _cancellationTokenSource;
        private readonly ServiceBusFactory _senderFactory;

        // Statistics tracking - events sent per minute for the last 10 minutes
        // Single instance, no locking needed - updated only by FeederTask
        private readonly List<(DateTime Timestamp, int Count)> _minuteStats = new(10);
        private int _currentMinuteCount = 0;
        private DateTime _currentMinuteStart = DateTime.UtcNow;
        
        // Error tracking - errors per minute for the last 10 minutes
        private readonly List<(DateTime Timestamp, int Count)> _minuteErrors = new(10);
        private int _currentMinuteErrors = 0;

        // Batch tuning
        private const int MaxDrainPerCycle = 50; // max messages to drain from queue per cycle
        private static readonly TimeSpan FlushIntervalMs = TimeSpan.FromMilliseconds(1000);    // small delay to coalesce bursts (when not shutting down)

        public BackupAPIService(IOptions<BackendOptions> options, ServiceBusFactory senderFactory,ILogger<BackupAPIService> logger)
        {
            _options = options.Value;
            _senderFactory = senderFactory;
            _logger = logger;
        }


        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_options.AsyncModeEnabled)
            {
                _cancellationTokenSource = new CancellationTokenSource();
                _cancellationTokenSource.Token.Register(() =>
                {
                    _logger.LogCritical("Backup API service stopping.");
                });

                // Start the writer task but DON'T await it
                writerTask = Task.Run(() => EventWriter(_cancellationTokenSource.Token), _cancellationTokenSource.Token);

                // Return immediately - let the writer task run in the background
            }

            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("[SHUTDOWN] BackupAPIService stopping...");

            isShuttingDown = true;

            // Cancel the token to wake up any waiting operations
            _cancellationTokenSource?.Cancel();
    
            // The FeederTask will exit when isShuttingDown=true AND queue is empty
            
            if (writerTask != null)
            {
                try
                {
                    // Wait for the writer task to complete all work
                    // Use the provided cancellationToken only for the wait operation
                    await writerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation($"[SHUTDOWN] BackupAPIService stopped successfully. Final queue count: {_statusQueue.Count}");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // The wait was cancelled, but the task is still running
                    _logger.LogWarning("[SHUTDOWN] StopAsync timeout reached, but writer task is still running to complete message flush.");
                }
            }
        
            // Don't dispose the cancellation token source here - let the task complete
        }

        public bool UpdateStatus(RequestAPIDocument message)
        {
            try
            {
                _logger.LogDebug($"Backup: UserId: {message.userID}, Guid: {message.guid}, Status: {message.status}");
                _statusQueue.Enqueue(message);
                _queueSignal.Release();

                return true; // Enqueue succeeded
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enqueue message to the status queue.");
                return false; // Enqueue failed
            }
        }

        /// <summary>
        /// Gets statistics for events sent in the last 10 minutes.
        /// Returns a dictionary where key is minutes ago (0-9) and value is event count.
        /// Thread-safe for infrequent reads from health probes.
        /// </summary>
        public Dictionary<int, int> GetEventStatistics()
        {
            var stats = new Dictionary<int, int>();
            var now = DateTime.UtcNow;

            // Initialize all 10 minutes with 0
            for (int i = 0; i < 10; i++)
            {
                stats[i] = 0;
            }

            // Fill in historical data (immutable after minute rotation)
            for (int i = 0; i < _minuteStats.Count; i++)
            {
                var (timestamp, count) = _minuteStats[i];
                var minutesAgo = (int)(now - timestamp).TotalMinutes;
                if (minutesAgo >= 0 && minutesAgo < 10)
                {
                    stats[minutesAgo] = count;
                }
            }

            // Add current minute (may be stale by a few seconds)
            stats[0] = _currentMinuteCount;

            return stats;
        }

        /// <summary>
        /// Gets error statistics for the last 10 minutes.
        /// Returns a dictionary where key is minutes ago (0-9) and value is error count.
        /// Thread-safe for infrequent reads from health probes.
        /// </summary>
        public Dictionary<int, int> GetErrorStatistics()
        {
            var stats = new Dictionary<int, int>();
            var now = DateTime.UtcNow;

            // Initialize all 10 minutes with 0
            for (int i = 0; i < 10; i++)
            {
                stats[i] = 0;
            }

            // Fill in historical data (immutable after minute rotation)
            for (int i = 0; i < _minuteErrors.Count; i++)
            {
                var (timestamp, count) = _minuteErrors[i];
                var minutesAgo = (int)(now - timestamp).TotalMinutes;
                if (minutesAgo >= 0 && minutesAgo < 10)
                {
                    stats[minutesAgo] = count;
                }
            }

            // Add current minute (may be stale by a few seconds)
            stats[0] = _currentMinuteErrors;

            return stats;
        }

        public async Task EventWriter(CancellationToken token)
        {

            _logger.LogInformation("[SERVICE] ✓ Backup API service starting...");

            try
            {
                await FeederTask(token).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (token.IsCancellationRequested)
            {
                // Task was canceled, but check if we need to flush
                _logger.LogInformation($"Backup API service task was canceled. Queue items: {_statusQueue.Count}");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Operation was canceled, but check if we need to flush
                _logger.LogInformation($"Backup API service shutdown initiated: {_statusQueue.Count} items need to be flushed.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred in Backup API service");
            }
            finally
            {
                // Always flush remaining items
                var drained = new List<RequestAPIDocument>();
                while (_statusQueue.TryDequeue(out var statusMessage))
                {
                    drained.Add(statusMessage);
                }

                if (drained.Count > 0)
                {
                    _logger.LogWarning($"[SHUTDOWN] Flushing {drained.Count} remaining messages...");
                    
                    try
                    {
                        // Use a new token with generous timeout for final flush
                        using var flushCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                        await SendBatch(drained, flushCts.Token).ConfigureAwait(false);
                        _logger.LogInformation($"[SHUTDOWN] Successfully flushed {drained.Count} messages.");
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogError($"[SHUTDOWN] Failed to flush {drained.Count} messages - timeout exceeded!");
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, $"[SHUTDOWN] Error while flushing {drained.Count} messages.");
                    }
                }
                
                _cancellationTokenSource?.Dispose();
            }

            _logger.LogInformation("Backup API service is stopping.");
        }

        DateTime _lastDrainTime = DateTime.UtcNow;

        private async Task FeederTask(CancellationToken token)
        {
            var drained = new List<RequestAPIDocument>(MaxDrainPerCycle);
            
            // Continue until shutdown AND queue is empty
            while (!isShuttingDown || !_statusQueue.IsEmpty)
            {
                try
                {
                    // Don't delay during shutdown
                    if (!isShuttingDown)
                    {
                        var delta = DateTime.UtcNow - _lastDrainTime;
                        if (delta < FlushIntervalMs && !token.IsCancellationRequested)
                        {
                            delta = FlushIntervalMs - delta;
                            await Task.Delay(delta, token).ConfigureAwait(false);
                        }
                    }
                    _lastDrainTime = DateTime.UtcNow;

                    // Drain all available items
                    while (_statusQueue.TryDequeue(out var item))
                    {
                        drained.Add(item);
                        if (drained.Count >= MaxDrainPerCycle)
                        {
                            // Always use CancellationToken.None for send operations to prevent data loss
                            await SendBatchWithRetry(drained, CancellationToken.None).ConfigureAwait(false);
                            drained.Clear();
                        }
                    }

                    // Send any remaining items
                    if (drained.Count > 0)
                    {
                        // Always use CancellationToken.None for send operations to prevent data loss
                        await SendBatchWithRetry(drained, CancellationToken.None).ConfigureAwait(false);
                        drained.Clear();
                    }

                    // Wait for signal only if not shutting down
                    if (!isShuttingDown && _statusQueue.IsEmpty)
                    {
                        try
                        {
                            await _queueSignal.WaitAsync(token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (isShuttingDown)
                        {
                            // Expected during shutdown, continue to drain queue
                        }
                    }
                    
                    if ( !isShuttingDown ) {
                        _logger.LogDebug("Backup Status: waiting: queueCount: {count}", _statusQueue.Count);
                    } else {
                        _logger.LogDebug("Loop: shutting down: queueCount: {count}", _statusQueue.Count);
                    }
                }
                catch (OperationCanceledException) when (isShuttingDown && _statusQueue.IsEmpty)
                {
                    // Expected during shutdown when queue is empty
                    break;
                }
            }
            
            _logger.LogInformation($"[SHUTDOWN] FeederTask completed. Final queue count: {_statusQueue.Count}");
        }

        static readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            Converters = { new CaseInsensitiveEnumConverter<RequestAPIStatusEnum>() }
        };

        private async Task SendBatchWithRetry(List<RequestAPIDocument> items, CancellationToken token, int maxRetries = 3)
        {
            if (items.Count == 0) return;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    await SendBatch(items, token).ConfigureAwait(false);
                    
                    // Track successful sends
                    RecordSuccessfulSend(items.Count);
                    
                    // Success - log only if we had to retry
                    if (attempt > 1)
                    {
                        _logger.LogInformation($"[RETRY] Successfully sent {items.Count} items on attempt {attempt}");
                    }
                    return; // Success!
                }
                catch (Exception ex) when (attempt < maxRetries)
                {
                    // Not the last attempt - retry with backoff
                    var delayMs = attempt * 1000; // 1s, 2s, 3s...
                    _logger.LogWarning(ex, $"[RETRY] Failed to send batch (attempt {attempt}/{maxRetries}), retrying in {delayMs}ms...");
                    await Task.Delay(delayMs, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Last attempt failed - track error and give up
                    RecordError(items.Count);
                    _logger.LogError(ex, $"[ERROR] Failed to send batch of {items.Count} items after {maxRetries} attempts. Data loss occurred.");
                    return; // Give up
                }
            }
        }

        private void RecordSuccessfulSend(int count)
        {
            RotateMinuteIfNeeded();
            _currentMinuteCount += count;
        }

        private void RecordError(int count)
        {
            RotateMinuteIfNeeded();
            _currentMinuteErrors += count;
        }

        private void RotateMinuteIfNeeded()
        {
            var now = DateTime.UtcNow;
            var minutesSinceStart = (now - _currentMinuteStart).TotalMinutes;

            if (minutesSinceStart >= 1.0)
            {
                // Save current minute stats if non-zero
                if (_currentMinuteCount > 0)
                {
                    _minuteStats.Add((_currentMinuteStart, _currentMinuteCount));
                }

                // Save current minute errors if non-zero
                if (_currentMinuteErrors > 0)
                {
                    _minuteErrors.Add((_currentMinuteStart, _currentMinuteErrors));
                }

                // Remove stats older than 10 minutes (simple linear scan - only 10 items max)
                _minuteStats.RemoveAll(s => (now - s.Timestamp).TotalMinutes >= 10);
                _minuteErrors.RemoveAll(e => (now - e.Timestamp).TotalMinutes >= 10);

                // Start new minute
                _currentMinuteStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
                _currentMinuteCount = 0;
                _currentMinuteErrors = 0;
            }
        }


        private async Task SendBatch(List<RequestAPIDocument> items, CancellationToken token)
        {
            var sender = _senderFactory.GetQueueSender(_options.AsyncSBQueue);

            ServiceBusMessageBatch? currentBatch = null;
            try
            {
                currentBatch = await sender.CreateMessageBatchAsync(token).ConfigureAwait(false);

                foreach (var item in items)
                {
                    var message = new ServiceBusMessage(JsonSerializer.Serialize(item, jsonOptions));

                    _logger.LogDebug("BackupAPI: Sending status update for UserId: {userid}, Status: {status}", item.userID, item.status);

                    if (!currentBatch.TryAddMessage(message))
                    {
                        // Send the full batch and start a new one
                        await sender.SendMessagesAsync(currentBatch, token).ConfigureAwait(false);
                        currentBatch.Dispose();
                        currentBatch = await sender.CreateMessageBatchAsync(token).ConfigureAwait(false);

                        if (!currentBatch.TryAddMessage(message))
                        {
                            // Single message too large for an empty batch
                            _logger.LogError("Message too large to add to batch for topic {TopicName}. Dropping.", "backupapi");
                        }
                    }
                }

                if (currentBatch.Count > 0)
                {
                    await sender.SendMessagesAsync(currentBatch, token).ConfigureAwait(false);
                }
            }
            finally
            {
                currentBatch?.Dispose();
            }
        }
    }
}