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
public sealed class TokenomicsRollupProcessor : BackgroundService
{
    /// <summary>
    /// A tokenomics rollups batch queued for later processing. The raw body is kept as-is;
    /// parsing happens on the dequeue side, inside <see cref="RunAsync"/>.
    /// </summary>
    private sealed record QueueItem(string? ReplicaId, string? BatchId, string Body);

    private const int RecentBatchCapacity = 10;
    private static readonly TimeSpan s_processInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_diagnosticsInterval = TimeSpan.FromSeconds(60);

    private readonly ConcurrentQueue<QueueItem> _queue = new();
    private readonly TokenomicsMetricsStore _store;

    /// <summary>Last processed batch ids per ACA replica, newest first, capped at 10 per replica.</summary>
    private readonly ConcurrentDictionary<string, List<string>> _recentBatchesByReplica =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Previous diagnostics state to avoid repeated logging when nothing changed.</summary>
    private (long TotalRecordsProcessed, int UniqueUserModelCombinations, int UniqueUsers, int UniqueModels, long TotalTokens) _previousDiagnostics = (0, 0, 0, 0, 0);

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
            return new List<string>();
        }

        return new List<string>(history);
    }

    /// <summary>
    /// Periodically drains the queue: parses each queued CSV batch and records it in the
    /// sending replica's batch history. Wakes on every tick, processes whatever is currently
    /// queued, and goes back to waiting when the queue is empty.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(s_processInterval);
        using var diagnosticsTimer = new PeriodicTimer(s_diagnosticsInterval);

        try
        {
            var processingTask = ProcessQueueAsync(timer, stoppingToken);
            var diagnosticsTask = LogDiagnosticsAsync(diagnosticsTimer, stoppingToken);

            await Task.WhenAll(processingTask, diagnosticsTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
    }

    private async Task ProcessQueueAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            while (_queue.TryDequeue(out var item))
            {
                try
                {
                    var entries = ParseCsvEntries(item.Body);
                    foreach (var entry in entries)
                    {
                        _store.Record(entry);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.StackTrace);
                }

                RecordBatch(item.ReplicaId, item.BatchId);
            }
        }
    }

    private async Task LogDiagnosticsAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var currentDiagnostics = _store.GetDiagnostics();
                
                // Only log if something changed
                if (currentDiagnostics != _previousDiagnostics)
                {
                    Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] Metrics Store: {currentDiagnostics.TotalRecordsProcessed} records | {currentDiagnostics.UniqueUserModelCombinations} unique combos | {currentDiagnostics.UniqueUsers} users | {currentDiagnostics.UniqueModels} models | {currentDiagnostics.TotalTokens:N0} total tokens");
                    _previousDiagnostics = currentDiagnostics;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error logging diagnostics: {ex.Message}");
            }
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

        var headerLine = lines[0].TrimEnd();
        if (headerLine != PendingMetric.CsvHeader)
        {
            return entries;
        }

        foreach (var line in lines.Skip(1))
        {
            var trimmedLine = line.TrimEnd();
            if (string.IsNullOrWhiteSpace(trimmedLine)) continue;
            try
            {
                entries.Add(new PendingMetric(trimmedLine));
            }
            catch
            {
                // Skip malformed lines
            }
        }

        return entries;
    }


}
