using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using SimpleL7Proxy.Tokenomics;

namespace MetricsServer;

/// <summary>
/// Background processor for the tokenomics rollups queue. The HTTP server enqueues each POST's
/// raw CSV body as-is; this class dequeues, parses, and records batch history on its own tick,
/// independent of the HTTP request/response cycle.
/// </summary>
public sealed class TokenomicsRollupProcessor
{
    /// <summary>
    /// A tokenomics rollups batch queued for later processing. The raw body is kept as-is;
    /// parsing happens on the dequeue side, inside <see cref="RunAsync"/>.
    /// </summary>
    private sealed record QueueItem(string? ReplicaId, string? BatchId, string Body);

    private const int RecentBatchCapacity = 10;
    private static readonly TimeSpan s_processInterval = TimeSpan.FromSeconds(1);

    private readonly ConcurrentQueue<QueueItem> _queue = new();
    private readonly TokenomicsMetricsStore _store;

    /// <summary>Last processed batch ids per ACA replica, newest first, capped at 10 per replica.</summary>
    private readonly ConcurrentDictionary<string, List<string>> _recentBatchesByReplica =
        new(StringComparer.OrdinalIgnoreCase);

    public TokenomicsRollupProcessor(TokenomicsMetricsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>Enqueues a raw CSV batch body for later parsing and processing.</summary>
    public void Enqueue(string? replicaId, string batchId, string body)
    {
        _queue.Enqueue(new QueueItem(replicaId, batchId, body));
    }

    /// <summary>
    /// Gets a snapshot of batch ids for the given replica that have been received but not yet
    /// processed, in FIFO order. Enumerating a <see cref="ConcurrentQueue{T}"/> is thread-safe and
    /// does not dequeue, so this reflects the queue's current contents without disturbing
    /// <see cref="RunAsync"/>. Batches with no batch id are skipped, since they can't be listed by id.
    /// </summary>
    public List<string> GetPendingBatches(string? replicaId)
    {
        var replicaKey = string.IsNullOrEmpty(replicaId) ? "unknown" : replicaId;
        var pending = new List<string>();

        foreach (var item in _queue)
        {
            var itemReplicaKey = string.IsNullOrEmpty(item.ReplicaId) ? "unknown" : item.ReplicaId;
            if (!string.Equals(itemReplicaKey, replicaKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(item.BatchId))
            {
                pending.Add(item.BatchId);
            }
        }

        return pending;
    }

    /// <summary>
    /// Returns a snapshot of the requesting replica's last processed batch ids, newest first,
    /// without modifying history. Used by the HTTP handler before processing has run.
    /// </summary>
    public List<string> PeekRecentBatches(string? replicaId)
    {
        var replicaKey = string.IsNullOrEmpty(replicaId) ? "unknown" : replicaId;
        if (!_recentBatchesByReplica.TryGetValue(replicaKey, out var history))
        {
            Console.WriteLine($"No recent batches found for replica {replicaId ?? "unknown"}");
            return new List<string>();
        }

        return new List<string>(history);
    }

    /// <summary>
    /// Periodically drains the queue: parses each queued CSV batch and records it in the
    /// sending replica's batch history. Wakes on every tick, processes whatever is currently
    /// queued, and goes back to waiting when the queue is empty.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(s_processInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_queue.TryDequeue(out var item))
                {
                    var entries = ParseCsvEntries(item.Body);
                    foreach (var entry in entries)
                    {
                        _store.Record(
                            entry.UserId,
                            entry.Model,
                            entry.Day,
                            entry.InputTokens + entry.OutputTokens);
                    }

                    RecordBatch(item.ReplicaId, item.BatchId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
    }

    /// <summary>
    /// Records a processed batch id in the sending replica's history, capped at 10 entries.
    /// Called only from <see cref="RunAsync"/> after parsing.
    /// </summary>
    private void RecordBatch(string? replicaId, string? batchId)
    {
        if (string.IsNullOrEmpty(batchId))
        {
            return;
        }

        var replicaKey = string.IsNullOrEmpty(replicaId) ? "unknown" : replicaId;
        var history = _recentBatchesByReplica.GetOrAdd(replicaKey, _ => new List<string>(RecentBatchCapacity));

        lock (history)
        {
            history.Insert(0, batchId);
            if (history.Count > RecentBatchCapacity)
            {
                history.RemoveAt(history.Count - 1);
            }
        }
    }


    /// <summary>
    /// Parses CSV text with a header row into tokenomics rollup entries. The header names the
    /// columns (case-insensitive, order independent); malformed or short rows are skipped.
    /// </summary>
    private static List<PendingMetric> ParseCsvEntries(string csv)
    {
        var entries = new List<PendingMetric>();
        var lines = csv.Split('\n');
        if (lines.Length == 0)
        {
            return entries;
        }

        if (lines[0] != PendingMetric.CsvHeader)
        {
            return entries;
        }

        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                entries.Add(new PendingMetric(line));
            }
            catch
            {
                // Skip malformed lines
            }
        }

        return entries;
    }
}
