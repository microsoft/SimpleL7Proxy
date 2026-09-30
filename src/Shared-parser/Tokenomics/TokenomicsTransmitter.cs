using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;

namespace SimpleL7Proxy.Tokenomics;

/// <summary>
/// Standalone transmitter for tokenomics CSV batches to a remote metrics server.
/// Manages batch state (pending, processing, acknowledged), packages payloads using ReplicaPayloadMaker,
/// and retries unacknowledged batches on a periodic cycle.
/// </summary>
public sealed class TokenomicsTransmitter
{
    /// <summary>Cadence of the transmission loop, independent of batch submission.</summary>
    private static readonly TimeSpan TransmissionInterval = TimeSpan.FromSeconds(5);
    private const string NL = "\n";

    private readonly Uri _metricsServerUri;
    private readonly string _replicaId;
    private readonly HttpClient _httpClient;

    /// <summary>Batches queued for transmission: batchId → csv content.</summary>
    private readonly ConcurrentDictionary<string, string> _queuedBatches = new();

    /// <summary>Batches that have been sent but not yet acknowledged by the server.</summary>
    private readonly HashSet<string> _pendingAcknowledgment = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Batches that the server reported as still processing.</summary>
    private readonly HashSet<string> _serverProcessing = new(StringComparer.OrdinalIgnoreCase);

    private readonly CancellationTokenSource _transmissionLoopCts = new();
    private Task? _transmissionLoopTask;

    public TokenomicsTransmitter(Uri metricsServerUri, string replicaId, HttpClient httpClient)
    {
        _metricsServerUri = metricsServerUri ?? throw new ArgumentNullException(nameof(metricsServerUri));
        _replicaId = string.IsNullOrWhiteSpace(replicaId) ? "DEV" : replicaId;
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>
    /// Queues a CSV batch for transmission. The batch will be sent on the next transmission cycle,
    /// retried until acknowledged by the server.
    /// </summary>
    public void SubmitBatch(string batchId, string csvContent)
    {
        if (string.IsNullOrWhiteSpace(batchId))
            throw new ArgumentException("Batch ID cannot be empty", nameof(batchId));

        if (string.IsNullOrWhiteSpace(csvContent))
            throw new ArgumentException("CSV content cannot be empty", nameof(csvContent));

        _queuedBatches.TryAdd(batchId, csvContent);
        _pendingAcknowledgment.Add(batchId);
    }

    /// <summary>
    /// Starts the transmission loop. Should be called when ready to begin transmitting batches.
    /// </summary>
    public void Start()
    {
        if (_transmissionLoopTask is not null)
            return;

        _transmissionLoopTask = RunTransmissionLoopAsync(_transmissionLoopCts.Token);
    }

    /// <summary>
    /// Runs the periodic transmission loop. Packages all queued/pending batches and sends them,
    /// retrying unacknowledged batches until the server confirms receipt.
    /// </summary>
    private async Task RunTransmissionLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TransmissionInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    // If nothing to send, skip this cycle
                    if (_queuedBatches.IsEmpty && _pendingAcknowledgment.Count == 0)
                        continue;

                    // Stage 1: Build the list of batches to transmit
                    // Include all queued batches + all pending (unacknowledged) batches
                    var batchesToSend = new List<KeyValuePair<string, string>>();

                    foreach (var batchId in _queuedBatches.Keys)
                    {
                        if (_queuedBatches.TryGetValue(batchId, out var csv))
                        {
                            batchesToSend.Add(new KeyValuePair<string, string>(batchId, csv));
                        }
                    }

                    // Retransmit any pending batches that the server hasn't started processing
                    foreach (var batchId in _pendingAcknowledgment)
                    {
                        if (!_serverProcessing.Contains(batchId) && _queuedBatches.TryGetValue(batchId, out var csv))
                        {
                            // Only add if not already in the list
                            if (!batchesToSend.Any(kv => kv.Key == batchId))
                            {
                                batchesToSend.Add(new KeyValuePair<string, string>(batchId, csv));
                            }
                        }
                    }

                    if (batchesToSend.Count == 0)
                        continue;

                    // Stage 2: Package the payload using ReplicaPayloadMaker
                    var payload = ReplicaPayloadMaker.Make(_replicaId, batchesToSend);
                    var content = new StringContent(payload, Encoding.UTF8, "text/csv");

                    // Stage 3: Transmit
                    HttpResponseMessage? response = null;
                    try
                    {
                        response = await _httpClient.PostAsync(_metricsServerUri, content).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ERROR] Transmission failed: {ex.Message}");
                        continue;
                    }
                    finally
                    {
                        content?.Dispose();
                    }

                    if (response == null)
                    {
                        Console.WriteLine("[ERROR] Transmission returned null response");
                        continue;
                    }

                    // Stage 4: Read and process acknowledgment
                    MetricsServerResponse? responseAck = null;
                    try
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            Console.WriteLine($"[ERROR] Server returned {response.StatusCode}");
                            continue;
                        }

                        responseAck = await response.Content.ReadFromJsonAsync<MetricsServerResponse>()
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ERROR] Failed to parse response: {ex.Message}");
                    }
                    finally
                    {
                        response?.Dispose();
                    }

                    if (responseAck is null)
                        continue;

                    // Stage 5: Update state based on acknowledgments
                    // Remove acknowledged batches
                    if (responseAck.ProcessedBatches != null)
                    {
                        foreach (var batchId in responseAck.ProcessedBatches)
                        {
                            _queuedBatches.TryRemove(batchId, out _);
                            _pendingAcknowledgment.Remove(batchId);
                            _serverProcessing.Remove(batchId);
                        }
                    }

                    // Update which batches the server is currently processing
                    _serverProcessing.Clear();
                    if (responseAck.PendingBatches != null)
                    {
                        foreach (var batchId in responseAck.PendingBatches)
                        {
                            _serverProcessing.Add(batchId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Exception in transmission loop: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during shutdown
        }
    }

    /// <summary>
    /// Gracefully shuts down the transmission loop.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_transmissionLoopTask is null)
            return;

        await _transmissionLoopCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _transmissionLoopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
    }

    /// <summary>
    /// Gets the count of batches currently queued or pending acknowledgment.
    /// </summary>
    public int GetPendingBatchCount() => _queuedBatches.Count + _pendingAcknowledgment.Count;
}
