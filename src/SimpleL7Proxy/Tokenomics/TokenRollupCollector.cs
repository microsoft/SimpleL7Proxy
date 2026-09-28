using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using SimpleL7Proxy.Config;

namespace SimpleL7Proxy.Tokenomics;



/// <summary>
/// A CSV payload detached from the collector's pending rollups and identified by a batch id, so
/// a failed upload can be retried with the same batchId and entries per the upload contract.
/// </summary>
internal sealed record RollupBatch(
    [property: JsonPropertyName("batchId")] string BatchId,
    [property: JsonPropertyName("csv")] string Csv);



/// <summary>
/// Collects per-request token metrics into a double-buffered queue and periodically rolls them
/// up into a live per-(user, model) token balance, plus a pending set of per-(user, model, day)
/// deltas awaiting the next CSV payload.
/// </summary>
/// <remarks>
/// This class owns the full token-rollup lifecycle: enqueue, live balance lookup, draining a
/// cycle's queued metrics into the aggregate balance, detaching accumulated deltas into a CSV
/// payload every <see cref="CollapseCyclesPerPayload"/> collapse cycles (see
/// <see cref="TryGetBatch"/>), and transmitting those payloads to the MetricsServer on its own
/// periodic cycle (see <see cref="RunTransmissionLoopAsync"/>), retrying any batch until the
/// MetricsServer acknowledges it. Callers only need to feed metrics in via
/// <see cref="AddMetric"/> and drive <see cref="Collapse"/>; content-safety counters and
/// request-outcome sampling remain the caller's responsibility.
/// </remarks>
public sealed class TokenRollupCollector : BackgroundService, IConfigChangeSubscriber, IHostedService, IReadinessParticipant
{
    public ReadinessParticipantEnum Participant => ReadinessParticipantEnum.Tokenomics;
    public ReadinessRegistry Readiness { get; }

    private const int InputTokensIndex = 0;
    private const int OutputTokensIndex = 1;

    /// <summary>Number of <see cref="Collapse"/> calls per detached batch. With the 1-second collapse cadence, this yields a batch roughly every 5 seconds.</summary>
    private const int CollapseCyclesPerPayload = 5;

    /// <summary>Cadence of this collector's own detach-and-transmit loop, independent of the caller's <see cref="Collapse"/> cadence.</summary>
    private static readonly TimeSpan TransmissionInterval = TimeSpan.FromSeconds(5);

    private readonly ConcurrentQueue<string>[] _csvmetrics = [new(), new()];
    private readonly ConcurrentDictionary<(string UserId, string Model), long[]> _aggregateBalance = new();
    private Dictionary<(string UserId, string Model, DateOnly Day), (long InputTokens, long OutputTokens, long CachedTokens)> _pendingRollups = new();

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private int _activeQueueIndex;
    private readonly CancellationTokenSource _transmissionLoopCts = new();
    private Task? _transmissionLoopTask;
    private Uri _metricsServerUri = null!;
    private readonly ProxyConfig _options;
    private readonly ILogger<TokenRollupCollector> _logger;
    private readonly TokenomicsSettings _settings;

    public TokenRollupCollector(
        TokenomicsSettings settings,
        ReadinessRegistry readiness,
        ILogger<TokenRollupCollector> logger,
        ProxyConfig options,
        ConfigChangeNotifier configChangeNotifier)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));

        InitVars();

        configChangeNotifier.Subscribe(this,
           [options => options.TokenomicsEnable,
            options => options.TokenomicsMetricsServer,
            options => options.TokenomicsOptions]);
    }

    public Task OnConfigChangedAsync(IReadOnlyList<ConfigChange> changes, ProxyConfig backendOptions, CancellationToken cancellationToken)
    {
        InitVars();
        return Task.CompletedTask;
    }

    public void InitVars()
    {

        var configuredValue = _options.TokenomicsMetricsServer;

        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            _options.TokenomicsEnable = false;
            return;
        }

        var endpoint = configuredValue.TrimEnd('/') + "/tokenomics/metrics/upload";

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var metricsServerUri))
        {
            _options.TokenomicsEnable = false;
            _logger.LogError("[TOKN-IX] Invalid tokenomics metrics server URI: {MetricsServer}", configuredValue);
            return;
        }

        _metricsServerUri = metricsServerUri;
        if (!_settings.TryParse(_options.TokenomicsOptions))
        {
            _options.TokenomicsEnable = false;
            _logger.LogError("[TOKN-IX] Invalid tokenomics options: {Options}", _options.TokenomicsOptions);
            return;
        }
    }


    const string NL = "\n";

    /// <summary>Runs the detach-and-transmit cycle on <see cref="TransmissionInterval"/> until cancellation is requested.</summary>
    private async Task RunTransmissionLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TransmissionInterval);
        var replicaId = string.IsNullOrWhiteSpace(_options.ReplicaName) ? "DEV" : _options.ReplicaName!;

        HashSet<string> needsProcessing = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> ServerIsProcessing = new(StringComparer.OrdinalIgnoreCase);

        // List<string> processingBatches = new();
        List<string> processedBatches = new();
        Dictionary<string, string> batchPayloads = new();

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {

                    // Skip this cycle if tokenomics is disabled.
                    if (_options.TokenomicsEnable == false) continue;

                    // Stage 1: determine batches to transmit.
                    if (CollapseCurrentMetrics() is not string csv_batch)
                        continue;

                    List<KeyValuePair<string, string>> batches = new();

                    var newBatchId = Guid.NewGuid().ToString("N");

                    if (!string.IsNullOrWhiteSpace(csv_batch))
                    {
                        batchPayloads[newBatchId] = csv_batch;

                        batches.Add(new KeyValuePair<string, string>(newBatchId, csv_batch));
                        needsProcessing.Add(newBatchId);
                    }

                    // Stage 2: package payload. Every batch still tracked here is, by construction,
                    // not yet acknowledged (acknowledged ones were pruned above), so all of them are
                    // retransmitted as-is.
                    var payload = new StringBuilder("ReplicaId: " + replicaId).Append(NL);

                    foreach (var bpid in needsProcessing)
                    {
                        if (bpid != newBatchId && !ServerIsProcessing.Contains(bpid))
                            batches.Add(new KeyValuePair<string, string>(bpid, batchPayloads[bpid]));
                    }

                    // Stage 3: transmit.
                    var strcontent = new StringContent(ReplicaPayloadMaker.Make(replicaId, batches), Encoding.UTF8, "text/csv");

                    var response = await TransmitAsync(strcontent).ConfigureAwait(false);
                    if (response == null)
                    {
                        Console.WriteLine("[ERROR] Transmission returned null response");
                        continue;
                    }

                    // Stage 4: update payloads processed.
                    var responseAck = await ReadResponseAsync(response).ConfigureAwait(false);

                    if (responseAck is null)
                        continue;

                    // remove acknowledged batches from batchPayloads; once pruned, nothing further needs to remember them.
                    foreach (var bid in responseAck.ProcessedBatches!)
                    {
                        batchPayloads.Remove(bid);
                        needsProcessing.Remove(bid);
                    }

                    ServerIsProcessing.Clear();
                    foreach (var bid in responseAck.PendingBatches!)
                    {
                        ServerIsProcessing.Add(bid);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Exception occurred: {ex.Message}");
                }

            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Enqueues a metric for the next collapse cycle.</summary>
    public void AddMetric(string csvmetric)
    {
        var queueIndex = Volatile.Read(ref _activeQueueIndex);

        _csvmetrics[queueIndex].Enqueue(csvmetric);
    }

    /// <summary>
    /// Swaps the active queue and drains the previous queue, rolling each metric's tokens into
    /// the per-(user, model) live balance and the pending per-(user, model, day) deltas, and
    /// returning the drained batch for further processing.
    /// </summary>
    public string? CollapseCurrentMetrics()
    {
        var queueToCollapseIndex = Volatile.Read(ref _activeQueueIndex);

        // flip the binary index to get the next queue index
        var nextQueueIndex = queueToCollapseIndex ^ 1;
        Interlocked.Exchange(ref _activeQueueIndex, nextQueueIndex);

        if (_csvmetrics[queueToCollapseIndex].IsEmpty)
            return null;

        StringBuilder CollapsedCSV = new StringBuilder();
        CollapsedCSV.Append(PendingMetric.CsvHeader).Append(NL);
        foreach (string l in _csvmetrics[queueToCollapseIndex])
        {
            CollapsedCSV.Append(l).Append(NL);
        }
        _csvmetrics[queueToCollapseIndex].Clear();
        return CollapsedCSV.ToString();
    }

    /// <summary>Stage 3: transmits one packaged payload, returning the response, or null if the request could not be completed (network failure, timeout, etc.), in which case the caller retries the same payload on the next transmission cycle.</summary>
    private async Task<HttpResponseMessage?> TransmitAsync(StringContent payload)
    {
        using (payload)
        {
            try
            {
                var response = await _httpClient.PostAsync(_metricsServerUri, payload).ConfigureAwait(false);
                return response;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Transmission failed: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>Stage 4: reads and deserializes a transmitted payload's response, if any. 
    /// Returns null when there is no response, the request was unsuccessful, 
    /// or the response is not the expected shape; 
    /// acknowledging (removing) any reported batch ids is the caller's responsibility.</summary>
    private async Task<MetricsServerResponse?> ReadResponseAsync(HttpResponseMessage? response)
    {
        if (response == null)
        {
            return null;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            return await response.Content.ReadFromJsonAsync<MetricsServerResponse>()
                .ConfigureAwait(false);

        }
    }

    private async Task<bool> TestTokenomicsMetricsServer()
    {
        if (!_options.TokenomicsEnable) return false;
        if (string.IsNullOrWhiteSpace(_options.TokenomicsMetricsServer)) return false;

        // Attempt to send a test request to the metrics server to verify connectivity.
        try
        {
            var testPayload = new StringContent("{}");
            var response = await TransmitAsync(testPayload).ConfigureAwait(false);
            var result = await ReadResponseAsync(response).ConfigureAwait(false);
            return result != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Starts this collector's own periodic detach-and-transmit loop, independent of the
    /// caller's <see cref="Collapse"/> cadence.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    //public Task StartAsync(CancellationToken cancellationToken)
    {

        if (_transmissionLoopTask != null)
        {
            return;
        }

        _transmissionLoopTask = Task.Run(() => RunTransmissionLoopAsync(_transmissionLoopCts.Token));

        // Wait for metrics server to ack health probe for up to 10 seconds
        var response = await TestTokenomicsMetricsServer().ConfigureAwait(false);
        var status = response ? " Responding - OK." : "--UNREACHABLE--";

        _logger.LogWarning("[TOKN-IX] Tokenomics service server {Status}.  {Reachability}  ",
            _options.TokenomicsEnable ? "Enabled" : "Disabled", status);

        this.RegisterReady();

        // wait for the cancellation token
        await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);

        await _transmissionLoopCts.CancelAsync().ConfigureAwait(false);

        try
        {
            await _transmissionLoopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        return;
    }
}
