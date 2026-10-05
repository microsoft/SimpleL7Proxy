using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;

namespace SimpleL7Proxy.Tokenomics;

/// <summary>
/// Transmits tokenomics batches and retains them until the server reports them processed.
/// </summary>
public sealed class TokenomicsTransmitter
{
    private static readonly TimeSpan TransmissionInterval = TimeSpan.FromSeconds(5);

    private readonly Uri _metricsServerUri;
    private readonly string _replicaId;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, string> _queuedBatches =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pendingAcknowledgment =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _serverProcessing =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _transmissionLoopCts = new();

    private Task? _transmissionLoopTask;
    private int _started;

    public TokenomicsTransmitter(
        Uri metricsServerUri,
        string replicaId,
        HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(metricsServerUri);

        _replicaId = string.IsNullOrWhiteSpace(replicaId)
            ? "DEV"
            : replicaId;
        _metricsServerUri = AddReplicaId(metricsServerUri, _replicaId);
        _httpClient = httpClient
            ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>Queues a batch for transmission.</summary>
    public void SubmitBatch(string batchId, string csvContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(csvContent);

        if (_queuedBatches.TryAdd(batchId, csvContent))
        {
            _pendingAcknowledgment.TryAdd(batchId, 0);
        }
    }

    /// <summary>Starts the transmission loop.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _transmissionLoopTask =
            RunTransmissionLoopAsync(_transmissionLoopCts.Token);
    }

    private async Task RunTransmissionLoopAsync(
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TransmissionInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                if (_queuedBatches.IsEmpty)
                {
                    continue;
                }

                try
                {
                    var batches = BuildTransmission();
                    var acknowledgement = await TransmitAsync(
                        batches,
                        cancellationToken).ConfigureAwait(false);

                    if (acknowledgement is not null)
                    {
                        ApplyAcknowledgement(acknowledgement);
                    }
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[ERROR] Tokenomics transmission failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private List<KeyValuePair<string, string>> BuildTransmission()
    {
        var batches = new List<KeyValuePair<string, string>>();

        foreach (var batchId in _pendingAcknowledgment.Keys)
        {
            if (_serverProcessing.ContainsKey(batchId))
            {
                continue;
            }

            if (_queuedBatches.TryGetValue(batchId, out var csv))
            {
                batches.Add(
                    new KeyValuePair<string, string>(batchId, csv));
            }
            else
            {
                _pendingAcknowledgment.TryRemove(batchId, out _);
            }
        }

        return batches;
    }

    private async Task<MetricsServerResponse?> TransmitAsync(
        List<KeyValuePair<string, string>> batches,
        CancellationToken cancellationToken)
    {
        // An empty manifest is a status-only poll for server-pending IDs.
        var payload = ReplicaPayloadMaker.Make(batches);

        using var content = new StringContent(
            payload,
            Encoding.UTF8,
            "text/csv");
        using var response = await _httpClient.PostAsync(
            _metricsServerUri,
            content,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine(
                $"[ERROR] Metrics server returned {response.StatusCode}");
            return null;
        }

        return await response.Content
            .ReadFromJsonAsync<MetricsServerResponse>(
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private void ApplyAcknowledgement(MetricsServerResponse response)
    {
        var processed = response.ProcessedBatches is { Count: > 0 }
            ? new HashSet<string>(
                response.ProcessedBatches,
                StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var pending = response.PendingBatches is { Count: > 0 }
            ? new HashSet<string>(
                response.PendingBatches,
                StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var batchId in processed)
        {
            _queuedBatches.TryRemove(batchId, out _);
            _pendingAcknowledgment.TryRemove(batchId, out _);
            _serverProcessing.TryRemove(batchId, out _);
        }

        foreach (var batchId in pending)
        {
            if (processed.Contains(batchId)
                || !_queuedBatches.ContainsKey(batchId))
            {
                continue;
            }

            _pendingAcknowledgment.TryRemove(batchId, out _);
            _serverProcessing.TryAdd(batchId, 0);
        }

        foreach (var batchId in _serverProcessing.Keys)
        {
            if (pending.Contains(batchId)
                || processed.Contains(batchId))
            {
                continue;
            }

            _serverProcessing.TryRemove(batchId, out _);

            if (_queuedBatches.ContainsKey(batchId))
            {
                _pendingAcknowledgment.TryAdd(batchId, 0);
            }
        }
    }

    /// <summary>Stops the transmission loop.</summary>
    public async Task StopAsync(
        CancellationToken cancellationToken = default)
    {
        var transmissionTask = Volatile.Read(ref _transmissionLoopTask);
        if (transmissionTask is null)
        {
            return;
        }

        await _transmissionLoopCts.CancelAsync().ConfigureAwait(false);

        try
        {
            await transmissionTask
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Returns the number of unprocessed batches retained locally.</summary>
    public int GetPendingBatchCount() => _queuedBatches.Count;

    private static Uri AddReplicaId(Uri endpoint, string replicaId)
    {
        var builder = new UriBuilder(endpoint);
        var existingQuery = builder.Query.TrimStart('?');
        var replicaQuery = $"r={Uri.EscapeDataString(replicaId)}";

        builder.Query = string.IsNullOrEmpty(existingQuery)
            ? replicaQuery
            : $"{existingQuery}&{replicaQuery}";

        return builder.Uri;
    }
}