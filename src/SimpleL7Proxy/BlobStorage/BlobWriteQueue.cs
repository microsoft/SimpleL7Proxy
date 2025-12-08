using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SimpleL7Proxy.BlobStorage
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
        
        public int Priority { get; init; } = 0;
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
    /// </summary>
    public class BlobWriteQueue : IHostedService, IDisposable
    {
        private readonly Channel<BlobWriteOperation> _queue;
        private readonly List<Task> _workers;
        private readonly CancellationTokenSource _shutdownCts;
        private readonly ILogger<BlobWriteQueue> _logger;
        private readonly BlobWriteQueueOptions _options;
        private readonly BlobWriter _blobWriter;

        // Metrics
        private long _operationsQueued = 0;
        private long _operationsCompleted = 0;
        private long _operationsFailed = 0;
        private long _batchesExecuted = 0;
        private long _totalQueueTimeMs = 0;
        private long _totalProcessTimeMs = 0;

        public BlobWriteQueue(
            BlobWriter blobWriter,
            BlobWriteQueueOptions options,
            ILogger<BlobWriteQueue> logger)
        {
            _blobWriter = blobWriter ?? throw new ArgumentNullException(nameof(blobWriter));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _shutdownCts = new CancellationTokenSource();
            _workers = new List<Task>();

            // Create bounded or unbounded channel
            if (_options.MaxQueueSize > 0)
            {
                _queue = Channel.CreateBounded<BlobWriteOperation>(new BoundedChannelOptions(_options.MaxQueueSize)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = false,
                    SingleWriter = false
                });
            }
            else
            {
                _queue = Channel.CreateUnbounded<BlobWriteOperation>(new UnboundedChannelOptions
                {
                    SingleReader = false,
                    SingleWriter = false
                });
            }

            _logger.LogInformation(
                "[BlobWriteQueue] Initialized - Workers: {Workers}, MaxQueue: {MaxQueue}, Batching: {Batching}, " +
                "BatchSize: {BatchSize}, BatchWait: {BatchWait}ms",
                _options.WorkerCount,
                _options.MaxQueueSize == 0 ? "Unbounded" : _options.MaxQueueSize.ToString(),
                _options.EnableBatching,
                _options.MaxBatchSize,
                _options.BatchWaitTimeMs);
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
                await _queue.Writer.WriteAsync(operation, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _operationsQueued);

                _logger.LogTrace(
                    "[BlobWriteQueue] Enqueued {OperationId} - Container: {Container}, Blob: {Blob}, Size: {Size}B",
                    operation.OperationId, operation.ContainerName, operation.BlobName, operation.Data.Length);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BlobWriteQueue] Failed to enqueue operation {OperationId}", operation.OperationId);
                operation.SetException(ex);
                return false;
            }
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("[BlobWriteQueue] Starting {WorkerCount} workers", _options.WorkerCount);

            for (int i = 0; i < _options.WorkerCount; i++)
            {
                int workerId = i;
                _workers.Add(Task.Run(() => WorkerLoop(workerId, _shutdownCts.Token), _shutdownCts.Token));
            }

            _workers.Add(Task.Run(() => MetricsLoop(_shutdownCts.Token), _shutdownCts.Token));

            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("[BlobWriteQueue] Stopping...");
            _queue.Writer.Complete();
            _shutdownCts.Cancel();

            try
            {
                await Task.WhenAll(_workers).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }

            var avgQueueTime = _operationsCompleted > 0 ? _totalQueueTimeMs / _operationsCompleted : 0;
            var avgProcessTime = _operationsCompleted > 0 ? _totalProcessTimeMs / _operationsCompleted : 0;

            _logger.LogInformation(
                "[BlobWriteQueue] Stopped - Queued: {Queued}, Completed: {Completed}, Failed: {Failed}, " +
                "Batches: {Batches}, AvgQueueTime: {AvgQueue}ms, AvgProcessTime: {AvgProcess}ms",
                _operationsQueued, _operationsCompleted, _operationsFailed, _batchesExecuted,
                avgQueueTime, avgProcessTime);
        }

        private async Task WorkerLoop(int workerId, CancellationToken cancellationToken)
        {
            _logger.LogDebug("[Worker-{WorkerId}] Started", workerId);

            try
            {
                // Each worker maintains its own batch buffer
                var batchBuffer = new List<BlobWriteOperation>(_options.MaxBatchSize);

                await foreach (var operation in _queue.Reader.ReadAllAsync(cancellationToken))
                {
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

                        Interlocked.Increment(ref _operationsFailed);
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
                // BlobWriter already caches container clients, no need to init
                var stream = await _blobWriter.CreateBlobAndGetOutputStreamAsync(
                    operation.ContainerName,
                    operation.BlobName)
                    .ConfigureAwait(false);

                await stream.WriteAsync(operation.Data, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                await stream.DisposeAsync().ConfigureAwait(false);

                sw.Stop();

                operation.SetResult(new BlobWriteResult
                {
                    Success = true,
                    Duration = sw.Elapsed,
                    QueueTime = queueTime
                });

                Interlocked.Increment(ref _operationsCompleted);
                Interlocked.Add(ref _totalQueueTimeMs, (long)queueTime.TotalMilliseconds);
                Interlocked.Add(ref _totalProcessTimeMs, sw.ElapsedMilliseconds);

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

                Interlocked.Increment(ref _operationsFailed);
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
                    if (await _queue.Reader.WaitToReadAsync(timeoutCts.Token).ConfigureAwait(false))
                    {
                        if (_queue.Reader.TryRead(out var nextOperation))
                        {
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
                // Execute all writes in parallel
                var writeTasks = batch.Select(async operation =>
                {
                    var queueTime = DateTime.UtcNow - operation.EnqueuedAt;
                    var opSw = Stopwatch.StartNew();

                    try
                    {
                        var stream = await _blobWriter.CreateBlobAndGetOutputStreamAsync(
                            operation.ContainerName,
                            operation.BlobName)
                            .ConfigureAwait(false);

                        await stream.WriteAsync(operation.Data, cancellationToken).ConfigureAwait(false);
                        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        await stream.DisposeAsync().ConfigureAwait(false);

                        opSw.Stop();

                        operation.SetResult(new BlobWriteResult
                        {
                            Success = true,
                            Duration = opSw.Elapsed,
                            QueueTime = queueTime
                        });

                        Interlocked.Increment(ref _operationsCompleted);
                        Interlocked.Add(ref _totalQueueTimeMs, (long)queueTime.TotalMilliseconds);
                        Interlocked.Add(ref _totalProcessTimeMs, opSw.ElapsedMilliseconds);
                    }
                    catch (Exception ex)
                    {
                        opSw.Stop();

                        _logger.LogError(ex, "[Worker-{WorkerId}] Batch operation {OperationId} failed",
                            workerId, operation.OperationId);

                        operation.SetResult(new BlobWriteResult
                        {
                            Success = false,
                            ErrorMessage = ex.Message,
                            Exception = ex,
                            Duration = opSw.Elapsed,
                            QueueTime = queueTime
                        });

                        Interlocked.Increment(ref _operationsFailed);
                    }
                });

                await Task.WhenAll(writeTasks).ConfigureAwait(false);

                sw.Stop();
                Interlocked.Increment(ref _batchesExecuted);

                var successCount = batch.Count(op => op.GetResultAsync().Result.Success);

                _logger.LogDebug("[Worker-{WorkerId}] Batch completed - {Success}/{Total} in {Duration}ms",
                    workerId, successCount, batch.Count, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Worker-{WorkerId}] Batch execution failed", workerId);

                foreach (var operation in batch.Where(op => !op.GetResultAsync().IsCompleted))
                {
                    operation.SetResult(new BlobWriteResult
                    {
                        Success = false,
                        ErrorMessage = $"Batch execution failed: {ex.Message}",
                        Exception = ex,
                        Duration = sw.Elapsed
                    });

                    Interlocked.Increment(ref _operationsFailed);
                }
            }
        }

        private async Task MetricsLoop(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(_options.MetricsIntervalSeconds), cancellationToken)
                        .ConfigureAwait(false);

                    var queueDepth = _queue.Reader.Count;
                    var avgQueueTime = _operationsCompleted > 0 ? _totalQueueTimeMs / _operationsCompleted : 0;
                    var avgProcessTime = _operationsCompleted > 0 ? _totalProcessTimeMs / _operationsCompleted : 0;
                    var successRate = _operationsQueued > 0 ? (double)_operationsCompleted / _operationsQueued : 0;

                    _logger.LogInformation(
                        "[BlobWriteQueue] Metrics - Queued: {Queued}, Completed: {Completed}, Failed: {Failed}, " +
                        "Batches: {Batches}, Depth: {Depth}, SuccessRate: {SuccessRate:P2}, " +
                        "AvgQueue: {AvgQueue}ms, AvgProcess: {AvgProcess}ms",
                        _operationsQueued, _operationsCompleted, _operationsFailed, _batchesExecuted,
                        queueDepth, successRate, avgQueueTime, avgProcessTime);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        public void Dispose()
        {
            _shutdownCts?.Dispose();
        }
    }
}
