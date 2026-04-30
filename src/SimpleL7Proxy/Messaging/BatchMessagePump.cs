using System.Collections.Concurrent;

namespace SimpleL7Proxy.Messaging;

internal sealed class BatchMessagePumpOptions
{
    public int MaxBatchItems { get; init; } = 99;
    public int FlushCountThreshold { get; init; } = 1;
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    public int WaitThreshold { get; init; } = int.MaxValue;
    public TimeSpan ShutdownDrainTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

internal sealed class BatchMessagePump<TBatch> : IDisposable
    where TBatch : class
{
    private static readonly TimeSpan NormalWaitInterval = TimeSpan.FromMilliseconds(500);

    private readonly string _destination;
    private readonly IBatchMessageTransport<TBatch> _transport;
    private readonly Func<CancellationToken, ValueTask<TBatch>> _createBatchAsync;
    private readonly Func<CancellationToken, ValueTask<TBatch>> _recoverBatchAsync;
    private readonly BatchMessagePumpOptions _options;
    private readonly ConcurrentQueue<BatchMessageEnvelope> _queue = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);

    private Task? _writerTask;
    private TBatch? _batch;
    private volatile bool _isRunning;
    private volatile bool _isShuttingDown;
    private volatile bool _beginShutdown;
    private int _entryCount;
    private int _flushedThisMinute;
    private int _flushedLastMinute;
    private long _currentMinuteTicks;
    private bool _disposed;

    public BatchMessagePump(
        string destination,
        IBatchMessageTransport<TBatch> transport,
        Func<CancellationToken, ValueTask<TBatch>> createBatchAsync,
        Func<CancellationToken, ValueTask<TBatch>> recoverBatchAsync,
        BatchMessagePumpOptions? options = null)
    {
        _destination = destination ?? throw new ArgumentNullException(nameof(destination));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _createBatchAsync = createBatchAsync ?? throw new ArgumentNullException(nameof(createBatchAsync));
        _recoverBatchAsync = recoverBatchAsync ?? throw new ArgumentNullException(nameof(recoverBatchAsync));
        _options = options ?? new BatchMessagePumpOptions();
    }

    public int Count => _queue.Count;

    public int EntryCount => Volatile.Read(ref _entryCount);

    public int FlushedLastMinute => Volatile.Read(ref _flushedLastMinute);

    public bool IsRunning => _isRunning;

    public bool IsShuttingDown => _isShuttingDown;

    public void BeginShutdown()
    {
        _beginShutdown = true;
    }

    public void Enqueue(string? value)
    {
        if (value == null || !_isRunning || _isShuttingDown)
        {
            return;
        }

        if (value.StartsWith("\n\n", StringComparison.Ordinal))
        {
            value = value.Substring(2);
        }

        Interlocked.Increment(ref _entryCount);
        _queue.Enqueue(new BatchMessageEnvelope(_destination, value));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_isRunning)
        {
            return;
        }

        await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isRunning)
            {
                return;
            }

            _batch = await _recoverBatchAsync(cancellationToken).ConfigureAwait(false);
            _isRunning = true;
            _writerTask = Task.Run(() => RunAsync(_cancellationTokenSource.Token), _cancellationTokenSource.Token);
        }
        finally
        {
            _startLock.Release();
        }
    }

    public async Task StopAsync()
    {
        if (_writerTask == null)
        {
            return;
        }

        _isShuttingDown = true;

        var drainDeadline = DateTime.UtcNow.Add(_options.ShutdownDrainTimeout);
        while (_isRunning && Count > 0 && DateTime.UtcNow < drainDeadline)
        {
            await Task.Delay(100).ConfigureAwait(false);
        }

        _cancellationTokenSource.Cancel();
        await _writerTask.ConfigureAwait(false);
        _isRunning = false;
    }

    private async Task RunAsync(CancellationToken lifetimeToken)
    {
        var pendingTasks = new List<(Task Task, List<BatchMessageEnvelope> Items, int Count, TBatch Batch)>();
        var pendingItems = new List<BatchMessageEnvelope>();
        using var timer = new PeriodicTimer(NormalWaitInterval);
        var lastSendTime = DateTime.UtcNow;

        try
        {
            while (!lifetimeToken.IsCancellationRequested)
            {
                HarvestCompletedSends(pendingTasks);
                FillCurrentBatch(_options.MaxBatchItems, pendingItems);

                var currentBatch = _batch;
                if (currentBatch is not null)
                {
                    var batchCount = _transport.GetCount(currentBatch);
                    if (ShouldFlush(batchCount, DateTime.UtcNow - lastSendTime))
                    {
                        var (success, newItems) = await FlushBatchAsync(pendingTasks, pendingItems).ConfigureAwait(false);
                        pendingItems = newItems;
                        lastSendTime = DateTime.UtcNow;
                        if (!success)
                        {
                            _batch = await _recoverBatchAsync(CancellationToken.None).ConfigureAwait(false);
                            continue;
                        }
                    }
                }

                if (!_beginShutdown && EntryCount <= _options.WaitThreshold)
                {
                    try
                    {
                        await timer.WaitForNextTickAsync(lifetimeToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        catch (TaskCanceledException)
        {
        }
        finally
        {
            await DrainAndCloseAsync(pendingTasks, pendingItems).ConfigureAwait(false);
            _isRunning = false;
        }
    }

    private async Task DrainAndCloseAsync(
        List<(Task Task, List<BatchMessageEnvelope> Items, int Count, TBatch Batch)> pendingTasks,
        List<BatchMessageEnvelope> pendingItems)
    {
        while (true)
        {
            HarvestCompletedSends(pendingTasks);
            FillCurrentBatch(_options.MaxBatchItems, pendingItems);

            var currentBatch = _batch;
            if (currentBatch is not null && _transport.GetCount(currentBatch) > 0)
            {
                var (success, newItems) = await FlushBatchAsync(pendingTasks, pendingItems).ConfigureAwait(false);
                pendingItems = newItems;
                if (!success)
                {
                    _batch = await _recoverBatchAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            else if (pendingTasks.Count > 0)
            {
                foreach (var (task, items, _, batch) in pendingTasks)
                {
                    try
                    {
                        await task.ConfigureAwait(false);
                    }
                    catch
                    {
                        ReEnqueueItems(items);
                    }
                    finally
                    {
                        _transport.DisposeBatch(batch);
                    }
                }

                pendingTasks.Clear();
            }
            else
            {
                break;
            }
        }

        if (_batch is not null)
        {
            _transport.DisposeBatch(_batch);
            _batch = null;
        }

        try
        {
            await _transport.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task<(bool Success, List<BatchMessageEnvelope> NewPendingItems)> FlushBatchAsync(
        List<(Task Task, List<BatchMessageEnvelope> Items, int Count, TBatch Batch)> pendingTasks,
        List<BatchMessageEnvelope> pendingItems)
    {
        if (_batch is not { } sentBatch)
        {
            return (true, pendingItems);
        }

        var flushedCount = _transport.GetCount(sentBatch);
        _batch = null;

        try
        {
            var sendTask = _transport.SendAsync(_destination, sentBatch, CancellationToken.None);
            pendingTasks.Add((sendTask, pendingItems, flushedCount, sentBatch));
        }
        catch
        {
            _transport.DisposeBatch(sentBatch);
            ReEnqueueItems(pendingItems);
            return (false, new List<BatchMessageEnvelope>());
        }

        _batch = await _createBatchAsync(CancellationToken.None).ConfigureAwait(false);
        return (true, new List<BatchMessageEnvelope>());
    }

    private void HarvestCompletedSends(List<(Task Task, List<BatchMessageEnvelope> Items, int Count, TBatch Batch)> pendingTasks)
    {
        for (int i = pendingTasks.Count - 1; i >= 0; i--)
        {
            var (task, items, count, batch) = pendingTasks[i];
            if (!task.IsCompleted)
            {
                continue;
            }

            pendingTasks.RemoveAt(i);
            _transport.DisposeBatch(batch);
            if (task.IsCompletedSuccessfully)
            {
                UpdateFlushMetrics(count);
            }
            else
            {
                ReEnqueueItems(items);
            }
        }
    }

    private void FillCurrentBatch(int count, List<BatchMessageEnvelope> pendingItems)
    {
        if (_batch is not { } currentBatch)
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            if (!_queue.TryDequeue(out var message))
            {
                break;
            }

            if (_transport.TryAdd(currentBatch, message))
            {
                Interlocked.Decrement(ref _entryCount);
                pendingItems.Add(message);
                continue;
            }

            if (_transport.GetCount(currentBatch) == 0)
            {
                Interlocked.Decrement(ref _entryCount);
            }
            else
            {
                _queue.Enqueue(message);
            }

            break;
        }
    }

    private void ReEnqueueItems(List<BatchMessageEnvelope> items)
    {
        foreach (var item in items)
        {
            _queue.Enqueue(item);
            Interlocked.Increment(ref _entryCount);
        }
    }

    private void UpdateFlushMetrics(int count)
    {
        var nowMinute = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute;
        if (nowMinute != _currentMinuteTicks)
        {
            _flushedLastMinute = _flushedThisMinute;
            _flushedThisMinute = count;
            _currentMinuteTicks = nowMinute;
        }
        else
        {
            _flushedThisMinute += count;
        }
    }

    private bool ShouldFlush(int batchCount, TimeSpan elapsed)
    {
        if (batchCount == 0)
        {
            return false;
        }

        if (batchCount >= _options.FlushCountThreshold)
        {
            return true;
        }

        return elapsed >= _options.FlushInterval;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _cancellationTokenSource.Dispose();
        _startLock.Dispose();
        _disposed = true;
    }
}