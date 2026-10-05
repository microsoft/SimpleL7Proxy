using System.Collections.Concurrent;
using System.Text;
using SimpleL7Proxy.Tokenomics;

namespace MetricsServer;

/// <summary>
/// Atomically admits, deduplicates, and processes tokenomics rollup uploads. One upload carries a
/// manifest of batch ids plus the raw body; admission registers each batch, and the rollup
/// iterator parses the CSV sections for only the batches this upload admitted.
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
        string ReplicaId,
        IReadOnlyList<(string BatchId, BatchEntry Entry)> Batches,
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
            StringComparer.OrdinalIgnoreCase.Equals(left.ReplicaId, right.ReplicaId)
            && StringComparer.OrdinalIgnoreCase.Equals(left.BatchId, right.BatchId);

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
        private readonly RecentItem?[] _recent = new RecentItem?[RecentSignalCapacity];

        private int _processedCount;
        private long _recentOrder;

        /// <summary>True when the batch was already processed; refreshes its recent signal.</summary>
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
            Volatile.Write(ref _recent[index], new RecentItem(batchId, order));
        }

        private void Trim()
        {
            while (Volatile.Read(ref _processedCount) > DeduplicationCapacity
                && _processedOrder.TryDequeue(out var expiredBatchId))
            {
                if (_processed.TryRemove(expiredBatchId, out _))
                {
                    Interlocked.Decrement(ref _processedCount);
                }
            }
        }
    }

    private static readonly TimeSpan s_processInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_diagnosticsInterval = TimeSpan.FromSeconds(60);

    private readonly ConcurrentQueue<QueueItem> _queue = new();
    private readonly ConcurrentDictionary<BatchKey, BatchEntry> _activeBatches = new(BatchKeyComparer.Instance);
    private readonly ConcurrentDictionary<string, ProcessedHistory> _processedByReplica =
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
    /// Atomically admits each manifest batch id unless it is already pending or processed, then
    /// enqueues one work item carrying the admitted batches and the shared raw body.
    /// </summary>
    public void Enqueue(string? replicaId, IReadOnlyList<string> batchIds, string body)
    {
        if (batchIds is null || batchIds.Count == 0)
        {
            return;
        }

        var replicaKey = NormalizeReplica(replicaId);
        var history = GetHistory(replicaKey);
        List<(string BatchId, BatchEntry Entry)>? admitted = null;

        foreach (var rawId in batchIds)
        {
            if (string.IsNullOrWhiteSpace(rawId))
            {
                continue;
            }

            var batchId = rawId.Trim();

            // Already processed: report it, do not re-admit.
            if (history.TryReportProcessed(batchId))
            {
                continue;
            }

            var key = new BatchKey(replicaKey, batchId);
            var entry = new BatchEntry(Interlocked.Increment(ref _receivedOrder));

            // Already pending/processing: do not admit a second copy.
            if (!_activeBatches.TryAdd(key, entry))
            {
                continue;
            }

            // Close the race with an older admission that just finished.
            if (history.TryReportProcessed(batchId))
            {
                _activeBatches.TryRemove(new KeyValuePair<BatchKey, BatchEntry>(key, entry));
                continue;
            }

            (admitted ??= new List<(string, BatchEntry)>()).Add((batchId, entry));
        }

        if (admitted is { Count: > 0 })
        {
            _queue.Enqueue(new QueueItem(replicaKey, admitted, body));
        }
    }

    /// <summary>Returns pending and currently processing ids for a replica, oldest first.</summary>
    public List<string> GetPendingBatches(string? replicaId)
    {
        var replicaKey = NormalizeReplica(replicaId);

        return _activeBatches
            .Where(pair => StringComparer.OrdinalIgnoreCase.Equals(pair.Key.ReplicaId, replicaKey))
            .OrderBy(pair => pair.Value.ReceivedOrder)
            .Select(pair => pair.Key.BatchId)
            .ToList();
    }

    /// <summary>Returns recently processed ids for a replica, newest first.</summary>
    public List<string> PeekRecentBatches(string? replicaId)
    {
        var replicaKey = NormalizeReplica(replicaId);

        return _processedByReplica.TryGetValue(replicaKey, out var history)
            ? history.GetRecent()
            : new List<string>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var processTimer = new PeriodicTimer(s_processInterval);
        using var diagnosticsTimer = new PeriodicTimer(s_diagnosticsInterval);

        try
        {
            await Task.WhenAll(
                ProcessQueueAsync(processTimer, stoppingToken),
                LogDiagnosticsAsync(diagnosticsTimer, stoppingToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessQueueAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_queue.TryDequeue(out var item))
            {
                Process(item);
            }
        }
    }

    private void Process(QueueItem item)
    {
        Dictionary<string, string>? sections = null;
        var history = GetHistory(item.ReplicaId);

        foreach (var (batchId, entry) in item.Batches)
        {
            var key = new BatchKey(item.ReplicaId, batchId);

            if (!_activeBatches.TryGetValue(key, out var current) || !ReferenceEquals(current, entry))
            {
                continue;
            }

            if (Interlocked.CompareExchange(
                    ref entry.State,
                    (int)BatchState.Processing,
                    (int)BatchState.Pending) != (int)BatchState.Pending)
            {
                continue;
            }

            try
            {
                // Split the shared body once, on first use.
                sections ??= SplitSections(item.Body);

                if (sections.TryGetValue(batchId, out var csv))
                {
                    foreach (var metric in ParseCsvEntries(csv))
                    {
                        _store.Record(metric);
                    }
                }

                // Publish processed state before clearing pending so the batch is never unknown.
                history.MarkProcessed(batchId);
                _activeBatches.TryRemove(new KeyValuePair<BatchKey, BatchEntry>(key, entry));
            }
            catch (Exception ex)
            {
                // Re-enqueue only this batch so siblings are not applied twice.
                Volatile.Write(ref entry.State, (int)BatchState.Pending);
                _queue.Enqueue(new QueueItem(item.ReplicaId, new[] { (batchId, entry) }, item.Body));
                Console.WriteLine($"Tokenomics batch {batchId} failed: {ex.Message}");
            }
        }
    }

    private async Task LogDiagnosticsAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
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
        _processedByReplica.GetOrAdd(replicaId, static _ => new ProcessedHistory());

    private static string NormalizeReplica(string? replicaId) =>
        string.IsNullOrWhiteSpace(replicaId) ? "unknown" : replicaId.Trim();

    /// <summary>
    /// Splits a full upload body into batchId → CSV section. Lines before the first
    /// <c>BatchId:</c> (version and manifest) are ignored.
    /// </summary>
    private static Dictionary<string, string> SplitSections(string body)
    {
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = body.Split('\n');
        string? currentId = null;
        StringBuilder? current = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith("BatchId:", StringComparison.OrdinalIgnoreCase))
            {
                if (currentId is not null)
                {
                    sections[currentId] = current!.ToString();
                }

                currentId = line["BatchId:".Length..].Trim();
                current = new StringBuilder();
                continue;
            }

            if (currentId is null)
            {
                continue;
            }

            current!.Append(line).Append('\n');
        }

        if (currentId is not null)
        {
            sections[currentId] = current!.ToString();
        }

        return sections;
    }

    private static List<PendingMetric> ParseCsvEntries(string csv)
    {
        var entries = new List<PendingMetric>();
        var lines = csv.Split('\n');

        if (lines.Length == 0 || lines[0].TrimEnd() != PendingMetric.CsvHeader)
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
                // Skip malformed records.
            }
        }

        return entries;
    }
}