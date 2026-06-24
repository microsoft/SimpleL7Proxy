using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleL7Proxy.Events;

namespace SimpleL7Proxy.Async.BlobStorage
{
    /// <summary>
    /// Configuration options for the blob write queue.
    /// </summary>
    public class BlobWriteQueueOptions
    {
        /// <summary>Number of worker threads processing blob writes.</summary>
        public int WorkerCount { get; set; } = Math.Max(2, Environment.ProcessorCount / 2);

        /// <summary>Maximum queue capacity (0 = unbounded).</summary>
        public int MaxQueueSize { get; set; } = 10000;

        /// <summary>Time to wait for batching tasks (ms).</summary>
        public int BatchWaitTimeMs { get; set; } = 50;

        /// <summary>Maximum batch size for blob writes to same container.</summary>
        public int MaxBatchSize { get; set; } = 25;

        /// <summary>Enable batching optimization for writes to same container.</summary>
        public bool EnableBatching { get; set; } = true;

        /// <summary>Enable deduplication of writes to the same blob (keeps only the last write). Set to false to write all operations.</summary>
        public bool EnableDeduplication { get; set; } = true;

        /// <summary>Metrics logging interval in seconds.</summary>
        public int MetricsIntervalSeconds { get; set; } = 30;
    }

    /// <summary>
    /// Represents a blob write operation to be queued.
    /// Uses ReadOnlyMemory to avoid defensive copies.
    /// </summary>
    public class BlobWriteOperation
    {
        public string OperationId { get; } = Guid.NewGuid().ToString();
        public required string ContainerName { get; init; }
        public required string BlobName { get; init; }
        
        /// <summary>
        /// Data to write. Uses ReadOnlyMemory to avoid copying.
        /// </summary>
        public ReadOnlyMemory<byte> Data { get; init; }

        public DateTime EnqueuedAt { get; } = DateTime.UtcNow;

        private readonly TaskCompletionSource<BlobWriteResult> _completionSource = new();

        /// <summary>
        /// Gets the result of the write operation.
        /// </summary>
        public Task<BlobWriteResult> GetResultAsync() => _completionSource.Task;

        /// <summary>
        /// Sets the result of the write operation.
        /// </summary>
        internal void SetResult(BlobWriteResult result) => _completionSource.TrySetResult(result);

        /// <summary>
        /// Sets an exception for the write operation.
        /// </summary>
        internal void SetException(Exception exception) => _completionSource.TrySetException(exception);
    }

    /// <summary>
    /// Result of a blob write operation.
    /// </summary>
    public class BlobWriteResult
    {
        public bool Success { get; init; }
        public string? ErrorMessage { get; init; }
        public Exception? Exception { get; init; }
        public TimeSpan Duration { get; init; }
        public TimeSpan QueueTime { get; init; }
    }

    /// <summary>
    /// Optimized queue-based blob write processor with per-worker batching.
    /// Each worker independently batches operations for the same container.
    /// Operations for the same blob are routed to the same worker via hashing.
    /// </summary>
    public class BlobWorkerPump : IHostedService, IDisposable, IReadinessParticipant
    {
        public ReadinessParticipantEnum Participant => ReadinessParticipantEnum.BlobWriter;
        public ReadinessRegistry Readiness { get; }
        private readonly Channel<BlobWriteOperation>[] _workerChannels;
        private readonly List<Task> _workers;
        private readonly CancellationTokenSource _shutdownCts;
        private readonly CancellationTokenSource _metricsLoopCts;
        private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
        private readonly ILogger<BlobWorkerPump> _logger;
        private readonly BlobWriteQueueOptions _options;
        private readonly IBlobWriter _blobWriter;
        private readonly BlobPumpMetrics _metrics;
        // private volatile bool _isShuttingDown = false;
        private bool _isStarted = false;
        private Task? _stopTask;

        /// <summary>
        /// Gets the total queue depth across all worker channels.
        /// Can be used by health checks to monitor queue pressure.
        /// </summary>
        public int QueueDepth => (int)_workerChannels.Sum(ch => ch.Reader.Count);

        public BlobWorkerPump(
            IBlobWriterFactory blobWriterFactory,
            BlobWriteQueueOptions options,
            ReadinessRegistry readiness,
            ILogger<BlobWorkerPump> logger)
        {
            _blobWriter = blobWriterFactory?.CreateBlobWriter() ?? throw new ArgumentNullException(nameof(blobWriterFactory));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            Readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
            _shutdownCts = new CancellationTokenSource();
            _metricsLoopCts = new CancellationTokenSource();
            _workers = new List<Task>();

            // Create per-worker channels for worker affinity
            _workerChannels = new Channel<BlobWriteOperation>[_options.WorkerCount];
            var queueSizePerWorker = _options.MaxQueueSize > 0 ? _options.MaxQueueSize / _options.WorkerCount : 0;

            for (int i = 0; i < _options.WorkerCount; i++)
            {
                if (queueSizePerWorker > 0)
                {
                    _workerChannels[i] = Channel.CreateBounded<BlobWriteOperation>(
                        new BoundedChannelOptions(queueSizePerWorker)
                        {
                            FullMode = BoundedChannelFullMode.Wait,
                            SingleReader = true,
                            SingleWriter = false
                        });
                }
                else
                {
                    _workerChannels[i] = Channel.CreateUnbounded<BlobWriteOperation>(
                        new UnboundedChannelOptions
                        {
                            SingleReader = true,
                            SingleWriter = false
                        });
                }
            }

            _metrics = new BlobPumpMetrics(_logger, () => _workerChannels.Sum(ch => ch.Reader.Count), () => _options.WorkerCount);

            _logger.LogInformation(
                "[BlobWr-Q] Initialized - Workers: {Workers}, MaxQueue: {MaxQueue}, Batching: {Batching}, " +
                "BatchSize: {BatchSize}, BatchWait: {BatchWait}ms",
                _options.WorkerCount,
                _options.MaxQueueSize == 0 ? "Unbounded" : _options.MaxQueueSize.ToString(),
                _options.EnableBatching,
                _options.MaxBatchSize,
                _options.BatchWaitTimeMs);
        }

        /// <summary>
        /// Gets the worker index for a blob using consistent hashing.
        /// This ensures operations for the same blob always go to the same worker.
        /// </summary>
        private int GetWorkerForBlob(string containerName, string blobName)
        {
            var blobKey = $"{containerName}/{blobName}";
            var hash = blobKey.GetHashCode();
            // Use absolute value and modulo to get positive index
            return Math.Abs(hash) % _options.WorkerCount;
        }

        /// <summary>
        /// Enqueues a blob write operation.
        /// </summary>
        /// <param name="operation">The write operation to enqueue.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True if enqueued successfully.</returns>
        public async Task<bool> EnqueueAsync(BlobWriteOperation operation, CancellationToken cancellationToken = default)
        {
            try
            {
                var workerId = GetWorkerForBlob(operation.ContainerName, operation.BlobName);
                await _workerChannels[workerId].Writer.WriteAsync(operation, cancellationToken).ConfigureAwait(false);
                _metrics.IncrementQueued();

                _logger.LogTrace(
                    "[BlobWr-Q] Enqueued {OperationId} to Worker-{WorkerId} - Container: {Container}, Blob: {Blob}, Size: {Size}B",
                    operation.OperationId, workerId, operation.ContainerName, operation.BlobName, operation.Data.Length);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BlobWr-Q] Failed to enqueue operation {OperationId}", operation.OperationId);
                operation.SetException(ex);
                return false;
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_isStarted || _stopTask is not null)
            {
                return;
            }

            await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_isStarted || _stopTask is not null)
                {
                    return;
                }

                _logger.LogInformation("[BlobWr-Q] Starting {WorkerCount} workers", _options.WorkerCount);

                for (int i = 0; i < _options.WorkerCount; i++)
                {
                    int workerId = i;
                    _workers.Add(Task.Run(() => WorkerLoop(workerId, _shutdownCts.Token), _shutdownCts.Token));
                }

                _workers.Add(Task.Run(() => _metrics.RunAsync(_metricsLoopCts.Token), _metricsLoopCts.Token));
                _isStarted = true;
                this.RegisterReady();
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            Task? stopTask;

            await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_stopTask is not null)
                {
                    stopTask = _stopTask;
                }
                else if (!_isStarted)
                {
                    return;
                }
                else
                {
                    _stopTask = StopCoreAsync();
                    stopTask = _stopTask;
                }
            }
            finally
            {
                _lifecycleLock.Release();
            }

            await stopTask.ConfigureAwait(false);
        }

        private async Task StopCoreAsync()
        {            
            // Signal shutdown to MetricsLoop (will increase frequency)
            //_isShuttingDown = true;
            
            // Complete the channels - no more writes will be accepted
            // Safe because CoordinatedShutdownService ensures all producers
            // (proxy workers, async workers) are done before calling this
            foreach (var channel in _workerChannels)
            {
                channel.Writer.Complete();
            }
            
            // Wait for workers to finish processing remaining items
            // DO NOT cancel _shutdownCts - let ALL blob operations complete
            var shutdownTimeout = TimeSpan.FromSeconds(60); // Allow time for blob operations to complete
            
            _logger.LogInformation("[BlobWr-Q] ⏳ Waiting for blob workers to complete (timeout: {Timeout}s)...", shutdownTimeout.TotalSeconds);
            
            try
            {
                // Get worker tasks (exclude MetricsLoop which is last)
                var workerTasks = _workers.Take(_workers.Count - 1).ToList();
                var workerTask = Task.WhenAll(workerTasks);
                
                // Don't use host's cancellationToken for timeout - it may fire earlier than our timeout
                var completedTask = await Task.WhenAny(workerTask, Task.Delay(shutdownTimeout))
                    .ConfigureAwait(false);
                
                if (completedTask != workerTask)
                {
                    _logger.LogWarning("[BlobWr-Q] ❌ Shutdown timeout reached - {Timeout}s", shutdownTimeout.TotalSeconds);
                    // DO NOT cancel _shutdownCts - we need blob operations to complete
                    // Just wait a bit more for cleanup
                    try
                    {
                        await Task.WhenAny(workerTask, Task.Delay(5000)).ConfigureAwait(false);
                    }
                    catch { }
                }
                else
                {
                    // Workers completed normally
                    await workerTask.ConfigureAwait(false);
                }
                
                // NOW stop MetricsLoop (it was last to run)
                _metricsLoopCts.Cancel();
                
                // Wait for MetricsLoop to finish
                try
                {
                    await Task.WhenAny(_workers.Last(), Task.Delay(2000)).ConfigureAwait(false);
                }
                catch { }
            }
            catch (OperationCanceledException) 
            {
                _logger.LogDebug("[BlobWr-Q] Shutdown cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[BlobWr-Q] Error during shutdown");
            }

            // Diagnostic: Collect any unflushed blob names still in channels
            var unflushedBlobs = new List<string>();
            try
            {
                foreach (var channel in _workerChannels)
                {
                    while (channel.Reader.TryRead(out var operation))
                    {
                        unflushedBlobs.Add($"{operation.ContainerName}/{operation.BlobName}");
                    }
                }
            }
            catch { /* Safely ignore any channel read errors */ }

            // Log diagnostic info about unflushed blobs
            if (unflushedBlobs.Count > 0)
            {
                _logger.LogWarning("[BlobWr-Q] ⚠️  Found {UnflushedCount} unflushed blob operations at shutdown:", unflushedBlobs.Count);
                for (int i = 0; i < unflushedBlobs.Count && i < 50; i++) // Limit to first 50 to avoid log spam
                {
                    _logger.LogWarning("[BlobWr-Q]   - {BlobName}", unflushedBlobs[i]);
                }
                if (unflushedBlobs.Count > 50)
                {
                    _logger.LogWarning("[BlobWr-Q]   ... and {RemainingCount} more", unflushedBlobs.Count - 50);
                }
            }

            var completed = _metrics.OperationsCompleted;
            var avgQueueTime = completed > 0 ? _metrics.TotalQueueTimeMs / completed : 0;
            var avgProcessTime = completed > 0 ? _metrics.TotalProcessTimeMs / completed : 0;
            var qsize = _workerChannels.Sum(ch => ch.Reader.Count);

            _logger.LogInformation(
                "[BlobWr-Q] ⏹  Stopped  blobs={Completed} batches={Batches} qsize={QSize}  ║ Σ Q={Queued} D={Dedup} Fail={Failed}  ║ avg q/p {AvgQueue}/{AvgProcess} ms",
                completed, _metrics.BatchesExecuted, qsize,
                _metrics.OperationsQueued, _metrics.OperationsDeduplicated, _metrics.OperationsFailed,
                avgQueueTime, avgProcessTime);

            _isStarted = false;
        }

        private async Task WorkerLoop(int workerId, CancellationToken cancellationToken)
        {
            _logger.LogDebug("[Worker-{WorkerId}] Started", workerId);

            try
            {
                // Each worker maintains its own batch buffer
                var batchBuffer = new List<BlobWriteOperation>(_options.MaxBatchSize);

                await foreach (var operation in _workerChannels[workerId].Reader.ReadAllAsync(cancellationToken))
                {
                    _metrics.IncrementInFlight();
                    try
                    {
                        if (_options.EnableBatching)
                        {
                            await ProcessWithBatchingAsync(operation, batchBuffer, workerId, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            await ProcessSingleOperationAsync(operation, workerId, cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[Worker-{WorkerId}] Unhandled error processing {OperationId}",
                            workerId, operation.OperationId);

                        operation.SetResult(new BlobWriteResult
                        {
                            Success = false,
                            ErrorMessage = ex.Message,
                            Exception = ex
                        });

                        _metrics.IncrementFailed();
                        _metrics.DecrementInFlight();
                    }
                }

                // Flush any remaining batched operations
                if (batchBuffer.Count > 0)
                {
                    await ExecuteBatchAsync(batchBuffer, workerId, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("[Worker-{WorkerId}] Cancelled", workerId);
            }

            _logger.LogDebug("[Worker-{WorkerId}] Stopped", workerId);
        }

        private async Task ProcessSingleOperationAsync(
            BlobWriteOperation operation,
            int workerId,
            CancellationToken cancellationToken)
        {
            var queueTime = DateTime.UtcNow - operation.EnqueuedAt;
            var sw = Stopwatch.StartNew();

            try
            {
                // Queue is small-blob-only: payload is fully materialized in memory, so use
                // BlockBlobClient.UploadAsync (1 round-trip). Large/streamed payloads bypass
                // the queue via AsyncStreamingStore → BlobClient.OpenWriteAsync.
                await _blobWriter.UploadBlobAsync(
                    operation.ContainerName,
                    operation.BlobName,
                    operation.Data,
                    cancellationToken).ConfigureAwait(false);

                sw.Stop();

                operation.SetResult(new BlobWriteResult
                {
                    Success = true,
                    Duration = sw.Elapsed,
                    QueueTime = queueTime
                });

                _metrics.RecordWriteSuccess((long)queueTime.TotalMilliseconds, sw.ElapsedMilliseconds);
                _metrics.DecrementInFlight();

                _logger.LogTrace("[Worker-{WorkerId}] {OperationId} completed - Queue: {Queue}ms, Process: {Process}ms",
                    workerId, operation.OperationId, queueTime.TotalMilliseconds, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();

                _logger.LogError(ex, "[Worker-{WorkerId}] {OperationId} failed - {Duration}ms",
                    workerId, operation.OperationId, sw.ElapsedMilliseconds);

                operation.SetResult(new BlobWriteResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    Duration = sw.Elapsed,
                    QueueTime = queueTime
                });

                _metrics.IncrementFailed();
                _metrics.DecrementInFlight();
            }
        }

        private async Task ProcessWithBatchingAsync(
            BlobWriteOperation firstOperation,
            List<BlobWriteOperation> batchBuffer,
            int workerId,
            CancellationToken cancellationToken)
        {
            batchBuffer.Clear();
            batchBuffer.Add(firstOperation);

            var containerName = firstOperation.ContainerName;
            var deadline = DateTime.UtcNow.AddMilliseconds(_options.BatchWaitTimeMs);

            // Opportunistically collect more operations for the same container
            while (batchBuffer.Count < _options.MaxBatchSize && DateTime.UtcNow < deadline)
            {
                // Use WaitToReadAsync with timeout instead of TryRead
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var remainingTime = deadline - DateTime.UtcNow;
                
                if (remainingTime <= TimeSpan.Zero)
                    break;

                timeoutCts.CancelAfter(remainingTime);

                try
                {
                    if (await _workerChannels[workerId].Reader.WaitToReadAsync(timeoutCts.Token).ConfigureAwait(false))
                    {
                        if (_workerChannels[workerId].Reader.TryRead(out var nextOperation))
                        {
                            _metrics.IncrementInFlight();
                            if (nextOperation.ContainerName == containerName)
                            {
                                batchBuffer.Add(nextOperation);
                            }
                            else
                            {
                                // Different container - process immediately
                                await ProcessSingleOperationAsync(nextOperation, workerId, cancellationToken)
                                    .ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Timeout reached
                    break;
                }
            }

            // Execute batch
            if (batchBuffer.Count > 1)
            {
                await ExecuteBatchAsync(batchBuffer, workerId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Single operation, no batching benefit
                await ProcessSingleOperationAsync(batchBuffer[0], workerId, cancellationToken).ConfigureAwait(false);
            }

            // Clear so the shutdown flush in WorkerLoop doesn't re-execute these already-completed ops
            batchBuffer.Clear();
        }

        private async Task ExecuteBatchAsync(
            List<BlobWriteOperation> batch,
            int workerId,
            CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();

            _logger.LogDebug("[Worker-{WorkerId}] Executing batch of {Count} - Container: {Container}",
                workerId, batch.Count, batch[0].ContainerName);

            try
            {
                List<BlobWriteOperation> deduplicatedOps;
                
                if (_options.EnableDeduplication)
                {
                    // Deduplicate by container+blob name - keep only the LAST (most recent) write for each unique blob
                    // Group by both container and blob name to handle same blob name in different containers
                    deduplicatedOps = batch
                        .GroupBy(op => $"{op.ContainerName}/{op.BlobName}")
                        .Select(group => group.OrderBy(op => op.EnqueuedAt).Last()) // Keep chronologically last operation
                        .ToList();

                    var duplicateCount = batch.Count - deduplicatedOps.Count;
                    if (duplicateCount > 0)
                    {
                        _logger.LogDebug("[Worker-{WorkerId}] Deduplicated {DuplicateCount} operations - Processing {UniqueCount} unique blobs",
                            workerId, duplicateCount, deduplicatedOps.Count);
                        
                        // Mark duplicate (superseded) operations as successful
                        var duplicateOps = batch
                            .GroupBy(op => $"{op.ContainerName}/{op.BlobName}")
                            .SelectMany(group => group.OrderBy(op => op.EnqueuedAt).SkipLast(1)); // All except the last
                        
                        foreach (var dupOp in duplicateOps)
                        {
                            _logger.LogTrace("[Worker-{WorkerId}] Operation {OperationId} superseded by later write to {Container}/{Blob} (enqueued at {EnqueuedAt})",
                                workerId, dupOp.OperationId, dupOp.ContainerName, dupOp.BlobName, dupOp.EnqueuedAt.ToString("HH:mm:ss.fff"));
                            
                            dupOp.SetResult(new BlobWriteResult
                            {
                                Success = true,
                                Duration = TimeSpan.Zero,
                                QueueTime = DateTime.UtcNow - dupOp.EnqueuedAt
                            });
                            
                            // Counted as Dedup only (disjoint from Completed)
                            _metrics.IncrementDeduplicated();
                            _metrics.DecrementInFlight();
                        }
                    }
                }
                else
                {
                    // No deduplication - write all operations
                    deduplicatedOps = batch;
                }

                // Execute all UNIQUE (most recent) writes in parallel
                var writeTasks = deduplicatedOps.Select(async operation =>
                {
                    var queueTime = DateTime.UtcNow - operation.EnqueuedAt;
                    var opSw = Stopwatch.StartNew();

                    try
                    {
                        // Queue is small-blob-only: 1-RT UploadAsync for fully-materialized payloads.
                        await _blobWriter.UploadBlobAsync(
                            operation.ContainerName,
                            operation.BlobName,
                            operation.Data,
                            cancellationToken).ConfigureAwait(false);

                        opSw.Stop();

                        operation.SetResult(new BlobWriteResult
                        {
                            Success = true,
                            Duration = opSw.Elapsed,
                            QueueTime = queueTime
                        });

                        _metrics.RecordWriteSuccess((long)queueTime.TotalMilliseconds, opSw.ElapsedMilliseconds);
                        _metrics.DecrementInFlight();
                    }
                    catch (Exception ex)
                    {
                        opSw.Stop();

                        _logger.LogError(ex, "[Worker-{WorkerId}] Batch operation {OperationId} failed - Container: {Container}, Blob: {Blob}, Type: {ExceptionType}",
                            workerId, operation.OperationId, operation.ContainerName, operation.BlobName, ex.GetType().FullName);
                        _logger.LogDebug("[Worker-{WorkerId}] Exception details - Message: {Message}, Stack: {Stack}",
                            workerId, ex.Message, ex.StackTrace);

                        operation.SetResult(new BlobWriteResult
                        {
                            Success = false,
                            ErrorMessage = ex.Message,
                            Exception = ex,
                            Duration = opSw.Elapsed,
                            QueueTime = queueTime
                        });

                        _metrics.IncrementFailed();
                        _metrics.DecrementInFlight();
                    }
                });

                await Task.WhenAll(writeTasks).ConfigureAwait(false);

                sw.Stop();
                _metrics.RecordBatchExecuted();

                var results = await Task.WhenAll(deduplicatedOps.Select(op => op.GetResultAsync())).ConfigureAwait(false);
                var successCount = results.Count(r => r.Success);

                _logger.LogDebug("[Worker-{WorkerId}] Batch completed - {Success}/{Total} unique blobs in {Duration}ms (original batch: {OriginalCount})",
                    workerId, successCount, deduplicatedOps.Count, sw.ElapsedMilliseconds, batch.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Worker-{WorkerId}] Batch execution failed - Type: {ExceptionType}, BatchSize: {BatchSize}", 
                    workerId, ex.GetType().FullName, batch.Count);
                _logger.LogDebug("[Worker-{WorkerId}] Batch failure details - Message: {Message}, Stack: {Stack}",
                    workerId, ex.Message, ex.StackTrace);

                foreach (var operation in batch.Where(op => !op.GetResultAsync().IsCompleted))
                {
                    _logger.LogWarning("[Worker-{WorkerId}] Marking operation {OperationId} as failed - Container: {Container}, Blob: {Blob}",
                        workerId, operation.OperationId, operation.ContainerName, operation.BlobName);
                    
                    operation.SetResult(new BlobWriteResult
                    {
                        Success = false,
                        ErrorMessage = $"Batch execution failed: {ex.Message}",
                        Exception = ex,
                        Duration = sw.Elapsed
                    });

                    _metrics.IncrementFailed();
                    _metrics.DecrementInFlight();
                }
            }
        }

        public void Dispose()
        {
            _shutdownCts?.Dispose();
            _metricsLoopCts?.Dispose();
            _lifecycleLock.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// Owns all metrics for <see cref="BlobWorkerPump"/>. Hot-path counters use Interlocked;
    /// cascading windows (1m/10m/lifetime) are owned exclusively by <see cref="RunAsync"/> so
    /// they need no synchronization. Per spec: every 10s a tick rolls the per-tick counter
    /// into the 1m bucket; every 6 ticks the 1m bucket rolls into the 10m bucket; every 60
    /// ticks the 10m bucket rolls into lifetime.
    /// </summary>
    internal sealed class BlobPumpMetrics
    {
        private readonly ILogger _logger;
        private readonly Func<int> _queueDepthProvider;
        private readonly Func<int> _workerCountProvider;

        // Reusable metric event populated and sent once per minute.
        private readonly ProxyEvent _metricEvent = new ProxyEvent
        {
            Type = EventType.Metric,
            MetricValues = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
        };

        // Aggregate counters (Interlocked)
        private long _operationsQueued;
        private long _operationsCompleted;
        private long _operationsFailed;
        private long _operationsDeduplicated;
        private long _operationsInFlight;
        private long _batchesExecuted;
        private long _totalQueueTimeMs;
        private long _totalProcessTimeMs;

        // Per-tick counters (Interlocked, hot path)
        private long _tick10sCompleted;
        private long _tick10sBatches;

        // Cascading buckets (single-thread owned by RunAsync, no Interlocked needed)
        private long _bucket1m;
        private long _bucket1mBatches;
        private long _bucket10m;
        private long _bucket10mBatches;
        private long _bucketLifetime;
        private long _bucketLifetimeBatches;

        public BlobPumpMetrics(ILogger logger, Func<int> queueDepthProvider, Func<int>? workerCountProvider = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _queueDepthProvider = queueDepthProvider ?? throw new ArgumentNullException(nameof(queueDepthProvider));
            _workerCountProvider = workerCountProvider ?? (() => 0);
        }

        public long OperationsQueued       => Interlocked.Read(ref _operationsQueued);
        public long OperationsCompleted    => Interlocked.Read(ref _operationsCompleted);
        public long OperationsFailed       => Interlocked.Read(ref _operationsFailed);
        public long OperationsDeduplicated => Interlocked.Read(ref _operationsDeduplicated);
        public long OperationsInFlight     => Interlocked.Read(ref _operationsInFlight);
        public long BatchesExecuted        => Interlocked.Read(ref _batchesExecuted);
        public long TotalQueueTimeMs       => Interlocked.Read(ref _totalQueueTimeMs);
        public long TotalProcessTimeMs     => Interlocked.Read(ref _totalProcessTimeMs);

        public void IncrementQueued()        => Interlocked.Increment(ref _operationsQueued);
        public void IncrementInFlight()      => Interlocked.Increment(ref _operationsInFlight);
        public void DecrementInFlight()      => Interlocked.Decrement(ref _operationsInFlight);
        public void IncrementFailed()        => Interlocked.Increment(ref _operationsFailed);
        public void IncrementDeduplicated()  => Interlocked.Increment(ref _operationsDeduplicated);

        public void RecordWriteSuccess(long queueTimeMs, long processTimeMs)
        {
            Interlocked.Increment(ref _operationsCompleted);
            Interlocked.Increment(ref _tick10sCompleted);
            Interlocked.Add(ref _totalQueueTimeMs, queueTimeMs);
            Interlocked.Add(ref _totalProcessTimeMs, processTimeMs);
        }

        public void RecordBatchExecuted()
        {
            Interlocked.Increment(ref _batchesExecuted);
            Interlocked.Increment(ref _tick10sBatches);
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            int tickCount = 0;
            long lastFailed = 0;
            long lastDedup = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                tickCount++;

                var blobs10s   = Interlocked.Exchange(ref _tick10sCompleted, 0);
                var batches10s = Interlocked.Exchange(ref _tick10sBatches, 0);
                _bucket1m        += blobs10s;
                _bucket1mBatches += batches10s;

                if (tickCount % 6 == 0)
                {
                    _bucket10m         += _bucket1m;
                    _bucket10mBatches  += _bucket1mBatches;
                    _bucket1m = 0;
                    _bucket1mBatches = 0;
                }

                if (tickCount % 60 == 0)
                {
                    _bucketLifetime         += _bucket10m;
                    _bucketLifetimeBatches  += _bucket10mBatches;
                    _bucket10m = 0;
                    _bucket10mBatches = 0;
                }

                var qSize    = _queueDepthProvider();
                var inFlight = Interlocked.Read(ref _operationsInFlight);
                var failed   = Interlocked.Read(ref _operationsFailed);
                var dedup    = Interlocked.Read(ref _operationsDeduplicated);

                // Suppress periodic output when nothing has changed: no activity this tick,
                // no in-flight work, empty queue, and no new failures/dedups since last log.
                bool noChange = blobs10s == 0
                             && batches10s == 0
                             && qSize == 0
                             && inFlight == 0
                             && failed == lastFailed
                             && dedup == lastDedup;

                lastFailed = failed;
                lastDedup = dedup;

                if (noChange)
                {
                    continue;
                }

                // Coarse, human-scannable summary. Lifetime totals are emitted on shutdown;
                // detailed time-series should go to App Insights metrics (TODO: metrics eventType).
                _logger.LogInformation(
                    "[BlobWr-Q] Wr ( 10s:{B10s}  1m:{B1m}  10m:{B10m} )  Bch ( 10s:{Bch10s}  1m:{Bch1m}  10m:{Bch10m} )  Q:{QSize}  InFlight:{InFlight}  Fail:{Failed}  Dup:{Dedup}",
                    blobs10s, _bucket1m, _bucket10m,
                    batches10s, _bucket1mBatches, _bucket10mBatches,
                    qSize, inFlight, failed, dedup);

                // Every minute (6 ticks @ 10s), emit a reusable metric event to App Insights / event client.
                // Numeric values land in EventTelemetry.Metrics so they are queryable & aggregatable.
                if (tickCount % 6 == 0)
                {
                    SendMetricEvent(qSize, inFlight, failed, dedup);
                }
            }
        }

        /// <summary>
        /// Populates the reusable per-minute metric event and dispatches it. Tracks deltas for
        /// counter-like fields so dashboards can sum or rate them. Values are written as both
        /// string properties (for the event-client/JSON path) and as numeric MetricValues
        /// (for App Insights EventTelemetry.Metrics).
        /// </summary>
        private long _lastMetricCompleted;
        private long _lastMetricBatches;
        private long _lastMetricFailed;
        private long _lastMetricDedup;
        private void SendMetricEvent(int qSize, long inFlight, long failed, long dedup)
        {
            try
            {
                var completed = Interlocked.Read(ref _operationsCompleted);
                var batches   = Interlocked.Read(ref _batchesExecuted);

                long dBlobs   = completed - _lastMetricCompleted;
                long dBatches = batches   - _lastMetricBatches;
                long dFailed  = failed    - _lastMetricFailed;
                long dDedup   = dedup     - _lastMetricDedup;
                _lastMetricCompleted = completed;
                _lastMetricBatches   = batches;
                _lastMetricFailed    = failed;
                _lastMetricDedup     = dedup;

                int workers = _workerCountProvider();

                _metricEvent.Reset();
                _metricEvent.Type = EventType.Metric;
                _metricEvent.MetricValues ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

                // Source identifier so multiple metric emitters can coexist.
                _metricEvent["Source"] = "BlobWr-Q";

                void Put(string key, long value)
                {
                    _metricEvent[key] = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    _metricEvent.MetricValues![key] = value;
                }

                Put("BlobsWritten",    dBlobs);
                Put("BatchesWritten",  dBatches);
                Put("QueueSize",       qSize);
                Put("BlobWriters",     workers);
                Put("InFlight",        inFlight);
                Put("Failed",          dFailed);
                Put("Dedup",           dDedup);

                _metricEvent.SendEvent();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[BlobWr-Q] Failed to emit metric event");
            }
        }
    }
}
