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

    private int _activeQueueIndex;
    private readonly ProxyConfig _options;
    private readonly ILogger<TokenRollupCollector> _logger;
    private readonly TokenomicsSettings _settings;
    private TokenomicsTransmitter? _transmitter;
    private PeriodicTimer? _collapseTimer;

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
            _transmitter = null;
            return;
        }

        var endpoint = configuredValue.TrimEnd('/') + "/tokenomics/metrics/upload";

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var metricsServerUri))
        {
            _options.TokenomicsEnable = false;
            _transmitter = null;
            _logger.LogError("[TOKN-IX] Invalid tokenomics metrics server URI: {MetricsServer}", configuredValue);
            return;
        }

        if (!_settings.TryParse(_options.TokenomicsOptions))
        {
            _options.TokenomicsEnable = false;
            _transmitter = null;
            _logger.LogError("[TOKN-IX] Invalid tokenomics options: {Options}", _options.TokenomicsOptions);
            return;
        }

        var replicaId = string.IsNullOrWhiteSpace(_options.ReplicaName) ? "DEV" : _options.ReplicaName!;
        var httpClient = new HttpClient() { Timeout = TimeSpan.FromSeconds(10) };
        _transmitter = new TokenomicsTransmitter(metricsServerUri, replicaId, httpClient);
    }


    const string NL = "\n";

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

    /// <summary>
    /// Starts this collector's periodic rollup loop and the transmitter background service.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.TokenomicsEnable == false || _transmitter is null)
        {
            _logger.LogWarning("[TOKN-IX] Tokenomics service disabled.");
            this.RegisterReady();
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
            return;
        }

        // Start the transmitter
        _transmitter.Start();

        _logger.LogWarning("[TOKN-IX] Tokenomics service server Enabled - Responding - OK.");

        // Start the collapse timer (periodically drain metrics and submit to transmitter)
        _collapseTimer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var collapseTask = RunCollapseLoopAsync(stoppingToken);

        this.RegisterReady();

        // Wait for cancellation
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        // Stop the collapse timer
        if (_collapseTimer is not null)
        {
            _collapseTimer.Dispose();
        }

        try
        {
            await collapseTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        // Stop the transmitter
        await _transmitter.StopAsync(stoppingToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the periodic collapse loop that drains metrics and submits to the transmitter.
    /// </summary>
    private async Task RunCollapseLoopAsync(CancellationToken cancellationToken)
    {
        while (_collapseTimer is not null)
        {
            try
            {
                if (!await _collapseTimer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                    break;

                if (CollapseCurrentMetrics() is string csvBatch && _transmitter is not null)
                {
                    var batchId = Guid.NewGuid().ToString("N");
                    _transmitter.SubmitBatch(batchId, csvBatch);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Exception in collapse loop: {ex.Message}");
            }
        }
    }
}
