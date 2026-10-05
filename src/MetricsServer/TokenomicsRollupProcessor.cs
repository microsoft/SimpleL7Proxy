using System.Collections.Concurrent;
using SimpleL7Proxy.Tokenomics;

namespace MetricsServer;

/// <summary>
/// Atomically admits, deduplicates, and processes tokenomics rollup batches.
/// </summary>
public sealed class TokenomicsRollupProcessor : BackgroundService
{
    private enum BatchState
    {
        Pending,
        Processing
    }

    private readonly record struct BatchKey(
        string ReplicaId,
        string BatchId);

    private sealed record QueueItem(
        BatchKey Key,
        BatchEntry Entry,
        string Body);

    private sealed record RecentItem(
        string BatchId,
        long Order);

    private sealed class BatchEntry
    {
        public BatchEntry(long receivedOrder)
        {
            ReceivedOrder = receivedOrder;
        }

        public long ReceivedOrder { get; }

        public int State = (int)BatchState.Pending;
    }

    private sealed class BatchKeyComparer : IEqualityComparer<BatchKey>
    {
        public static BatchKeyComparer Instance { get; } = new();

        public bool Equals(BatchKey left, BatchKey right) =>
            StringComparer.OrdinalIgnoreCase.Equals(
                left.ReplicaId,
                right.ReplicaId)
            && StringComparer.OrdinalIgnoreCase.Equals(
                left.BatchId,
                right.BatchId);

        public int GetHashCode(BatchKey key) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.ReplicaId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.BatchId));
    }

    private sealed class ProcessedHistory
    {
        private const int DeduplicationCapacity = 65_536;
        private const int RecentSignalCapacity = 64;
        private const int ResponseCapacity = 10;

        private readonly ConcurrentDictionary<string, byte> _processed =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<string> _processedOrder = new();
        private readonly RecentItem?[] _recent =
            new RecentItem?[RecentSignalCapacity];

        private int _processedCount;
        private long _recentOrder;

        public bool TryReportProcessed(string batchId)
        {
            if (!_processed.ContainsKey(batchId))
            {
                return false;
            }

            PublishRecent(batchId);
            return true;
        }

        public void MarkProcessed(string batchId)
        {
            if (_processed.TryAdd(batchId, 0))
            {
                _processedOrder.Enqueue(batchId);
                Interlocked.Increment(ref _processedCount);
                Trim();
            }

            PublishRecent(batchId);
        }

        public List<string> GetRecent()
        {
            var items = new List<RecentItem>(RecentSignalCapacity);

            for (var index = 0; index < _recent.Length; index++)
            {
                var item = Volatile.Read(ref _recent[index]);
                if (item is not null)
                {
                    items.Add(item);
                }
            }

            return items
                .OrderByDescending(item => item.Order)
                .Select(item => item.BatchId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(ResponseCapacity)
                .ToList();
        }

        private void PublishRecent(string batchId)
        {
            var order = Interlocked.Increment(ref _recentOrder);
            var index = (int)((order - 1) % _recent.Length);

            Volatile.Write(
                ref _recent[index],
                new RecentItem(batchId, order));
        }

        private void Trim()
        {
            while (Volatile.Read(ref _processedCount)
                > DeduplicationCapacity
                && _processedOrder.TryDequeue(out var expiredBatchId))
            {
                if (_processed.TryRemove(expiredBatchId, out _))
                {
                    Interlocked.Decrement(ref _processedCount);
                }
            }
        }
    }

    private static readonly TimeSpan s_processInterval =
        TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_diagnosticsInterval =
        TimeSpan.FromSeconds(60);

    private readonly ConcurrentQueue<QueueItem> _queue = new();
    private readonly ConcurrentDictionary<BatchKey, BatchEntry>
        _activeBatches = new(BatchKeyComparer.Instance);
    private readonly ConcurrentDictionary<string, ProcessedHistory>
        _processedByReplica =
            new(StringComparer.OrdinalIgnoreCase);
    private readonly TokenomicsMetricsStore _store;

    private long _receivedOrder;
    private (
        long TotalRecordsProcessed,
        int UniqueUserModelCombinations,
        int UniqueUsers,
        int UniqueModels,
        long TotalTokens) _previousDiagnostics;

    public TokenomicsRollupProcessor(TokenomicsMetricsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Atomically registers and enqueues a batch unless it is pending or processed.
    /// </summary>
    public void Enqueue(
        string? replicaId,
        string batchId,
        string body)
    {
        if (string.IsNullOrWhiteSpace(batchId))
        {
            return;
        }

        var replicaKey = NormalizeReplica(replicaId);
        var normalizedBatchId = batchId.Trim();
        var key = new BatchKey(replicaKey, normalizedBatchId);
        var history = GetHistory(replicaKey);

        while (true)
        {
            if (history.TryReportProcessed(normalizedBatchId))
            {
                return;
            }

            if (_activeBatches.ContainsKey(key))
            {
                return;
            }

            var entry = new BatchEntry(
                Interlocked.Increment(ref _receivedOrder));

            if (!_activeBatches.TryAdd(key, entry))
            {
                continue;
            }

            // Close the race with processing completion of an older admission.
            if (history.TryReportProcessed(normalizedBatchId))
            {
                _activeBatches.TryRemove(
                    new KeyValuePair<BatchKey, BatchEntry>(key, entry));
                return;
            }

            _queue.Enqueue(new QueueItem(key, entry, body));
            return;
        }
    }

    /// <summary>
    /// Returns pending and currently processing IDs, oldest first.
    /// </summary>
    public List<string> GetPendingBatches(string? replicaId)
    {
        var replicaKey = NormalizeReplica(replicaId);

        return _activeBatches
            .Where(pair => StringComparer.OrdinalIgnoreCase.Equals(
                pair.Key.ReplicaId,
                replicaKey))
            .OrderBy(pair => pair.Value.ReceivedOrder)
            .Select(pair => pair.Key.BatchId)
            .ToList();
    }

    /// <summary>
    /// Returns recently processed IDs, newest first.
    /// </summary>
    public List<string> PeekRecentBatches(string? replicaId)
    {
        var replicaKey = NormalizeReplica(replicaId);

        return _processedByReplica.TryGetValue(
            replicaKey,
            out var history)
            ? history.GetRecent()
            : new List<string>();
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        using var processTimer =
            new PeriodicTimer(s_processInterval);
        using var diagnosticsTimer =
            new PeriodicTimer(s_diagnosticsInterval);

        try
        {
            await Task.WhenAll(
                ProcessQueueAsync(processTimer, stoppingToken),
                LogDiagnosticsAsync(diagnosticsTimer, stoppingToken))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessQueueAsync(
        PeriodicTimer timer,
        CancellationToken cancellationToken)
    {
        while (await timer.WaitForNextTickAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            while (_queue.TryDequeue(out var item))
            {
                Process(item);
            }
        }
    }

    private void Process(QueueItem item)
    {
        if (!_activeBatches.TryGetValue(
                item.Key,
                out var currentEntry)
            || !ReferenceEquals(currentEntry, item.Entry))
        {
            return;
        }

        if (Interlocked.CompareExchange(
                ref item.Entry.State,
                (int)BatchState.Processing,
                (int)BatchState.Pending)
            != (int)BatchState.Pending)
        {
            return;
        }

        try
        {
            var entries = ParseCsvEntries(item.Body);

            foreach (var entry in entries)
            {
                _store.Record(entry);
            }

            // Publish processed state before removing pending state. An upload
            // can therefore never observe the batch as completely unknown.
            GetHistory(item.Key.ReplicaId)
                .MarkProcessed(item.Key.BatchId);

            _activeBatches.TryRemove(
                new KeyValuePair<BatchKey, BatchEntry>(
                    item.Key,
                    item.Entry));
        }
        catch (Exception ex)
        {
            Volatile.Write(
                ref item.Entry.State,
                (int)BatchState.Pending);
            _queue.Enqueue(item);

            Console.WriteLine(
                $"Tokenomics batch {item.Key.BatchId} failed: {ex.Message}");
        }
    }

    private async Task LogDiagnosticsAsync(
        PeriodicTimer timer,
        CancellationToken cancellationToken)
    {
        while (await timer.WaitForNextTickAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            var current = _store.GetDiagnostics();
            if (current == _previousDiagnostics)
            {
                continue;
            }

            Console.WriteLine(
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Metrics Store: "
                + $"{current.TotalRecordsProcessed} records | "
                + $"{current.UniqueUserModelCombinations} unique combos | "
                + $"{current.UniqueUsers} users | "
                + $"{current.UniqueModels} models | "
                + $"{current.TotalTokens:N0} total tokens");

            _previousDiagnostics = current;
        }
    }

    private ProcessedHistory GetHistory(string replicaId) =>
        _processedByReplica.GetOrAdd(
            replicaId,
            static _ => new ProcessedHistory());

    private static string NormalizeReplica(string? replicaId) =>
        string.IsNullOrWhiteSpace(replicaId)
            ? "unknown"
            : replicaId.Trim();

    private static List<PendingMetric> ParseCsvEntries(string csv)
    {
        var entries = new List<PendingMetric>();
        var lines = csv.Split('\n');

        if (lines.Length == 0
            || lines[0].TrimEnd() != PendingMetric.CsvHeader)
        {
            return entries;
        }

        foreach (var line in lines.Skip(1))
        {
            var value = line.TrimEnd();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            try
            {
                entries.Add(new PendingMetric(value));
            }
            catch
            {
                // Preserve existing behavior by skipping malformed records.
            }
        }

        return entries;
    }
}