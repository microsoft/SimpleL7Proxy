using System.Collections.Concurrent;
using SimpleL7Proxy.Tokenomics;

namespace MetricsServer;

/// <summary>
/// Atomically admits, deduplicates, and processes tokenomics rollup uploads. One upload carries a
/// manifest of batch ids plus the raw body; admission registers each batch, and the rollup
/// iterator parses the CSV sections in a single forward pass for only the admitted batches.
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

    /// <summary>
    /// Walks the upload body once: for each <c>BatchId:</c> section that this upload admitted,
    /// validates the header and records each CSV row parsed straight from the character span.
    /// </summary>
    private void Process(QueueItem item)
    {
        var history = GetHistory(item.ReplicaId);

        var claims = new Dictionary<string, BatchEntry>(item.Batches.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (batchId, entry) in item.Batches)
        {
            claims[batchId] = entry;
        }

        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ReadOnlySpan<char> body = item.Body;
        string? sectionId = null;
        BatchEntry? sectionEntry = null;
        var headerSeen = false;
        var headerValid = false;
        var sectionFailed = false;

        var pos = 0;
        while (pos < body.Length)
        {
            var rel = body.Slice(pos).IndexOf('\n');
            var lineEnd = rel < 0 ? body.Length : pos + rel;
            var line = body.Slice(pos, lineEnd - pos).TrimEnd('\r');
            pos = rel < 0 ? body.Length : lineEnd + 1;

            if (line.StartsWith("BatchId:", StringComparison.OrdinalIgnoreCase))
            {
                CloseSection(item, history, sectionId, sectionEntry, sectionFailed, handled);

                sectionId = line["BatchId:".Length..].Trim().ToString();
                sectionEntry = ClaimSection(item.ReplicaId, sectionId, claims);
                headerSeen = false;
                headerValid = false;
                sectionFailed = false;
                continue;
            }

            if (sectionEntry is null || sectionFailed)
            {
                continue;
            }

            if (!headerSeen)
            {
                headerSeen = true;
                headerValid = line.TrimEnd().SequenceEqual(PendingMetric.CsvHeader);
                continue;
            }

            if (!headerValid)
            {
                continue;
            }

            var row = line.TrimEnd();
            if (row.IsWhiteSpace())
            {
                continue;
            }

            try
            {
                if (PendingMetric.TryParse(row, out var metric))
                {
                    _store.Record(metric);
                }
                // Malformed rows are skipped.
            }
            catch (Exception ex)
            {
                sectionFailed = true;
                Console.WriteLine($"Tokenomics batch {sectionId} failed: {ex.Message}");
            }
        }

        CloseSection(item, history, sectionId, sectionEntry, sectionFailed, handled);

        // Admitted batches with no section in the body: record nothing, mark processed.
        foreach (var (batchId, entry) in item.Batches)
        {
            if (handled.Contains(batchId))
            {
                continue;
            }

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

            history.MarkProcessed(batchId);
            _activeBatches.TryRemove(new KeyValuePair<BatchKey, BatchEntry>(key, entry));
        }
    }

    /// <summary>Claims a section for processing if this upload admitted it and it is still pending.</summary>
    private BatchEntry? ClaimSection(string replicaId, string batchId, Dictionary<string, BatchEntry> claims)
    {
        if (!claims.TryGetValue(batchId, out var entry))
        {
            return null;
        }

        var key = new BatchKey(replicaId, batchId);
        if (!_activeBatches.TryGetValue(key, out var current) || !ReferenceEquals(current, entry))
        {
            return null;
        }

        if (Interlocked.CompareExchange(
                ref entry.State,
                (int)BatchState.Processing,
                (int)BatchState.Pending) != (int)BatchState.Pending)
        {
            return null;
        }

        return entry;
    }

    /// <summary>Finalizes a claimed section: re-enqueue only this batch on failure, else mark processed.</summary>
    private void CloseSection(
        QueueItem item,
        ProcessedHistory history,
        string? sectionId,
        BatchEntry? sectionEntry,
        bool sectionFailed,
        HashSet<string> handled)
    {
        if (sectionEntry is null || sectionId is null)
        {
            return;
        }

        handled.Add(sectionId);
        var key = new BatchKey(item.ReplicaId, sectionId);

        if (sectionFailed)
        {
            // Re-enqueue only this batch so siblings are not applied twice.
            Volatile.Write(ref sectionEntry.State, (int)BatchState.Pending);
            _queue.Enqueue(new QueueItem(item.ReplicaId, new[] { (sectionId, sectionEntry) }, item.Body));
            return;
        }

        // Publish processed state before clearing pending so the batch is never unknown.
        history.MarkProcessed(sectionId);
        _activeBatches.TryRemove(new KeyValuePair<BatchKey, BatchEntry>(key, sectionEntry));
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
}