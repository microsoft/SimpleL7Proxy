using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SimpleL7Proxy.Config;

namespace SimpleL7Proxy.Tokenomics;

public class TokenMetricsCache : IConfigChangeSubscriber, IHostedService, IDisposable
{
    private static readonly TimeSpan CollapseInterval = TimeSpan.FromSeconds(1);

    // Push collapsed up metrics here every second
    private readonly TokenRollupCollector _rollupCollector;
    private readonly ConcurrentDictionary<(string UserId, DateOnly Day), long> _dailyJailbreakCount = new();
    private readonly ConcurrentDictionary<(string UserId, DateOnly Month), long> _monthlyJailbreakCount = new();
    private readonly ConcurrentDictionary<(string UserId, DateOnly Day), long> _dailyContentFilteredCount = new();
    private readonly ConcurrentDictionary<(string UserId, DateOnly Month), long> _monthlyContentFilteredCount = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly ProxyConfig _options;

    private Task? _collapseTask;
    private volatile bool _isRunning;
    private bool _disposed;
    private ConcurrentQueue<PendingMetric> _pendingMetrics = new();

    private readonly TokenomicsSettings _tokenomicsSettings;
    private Uri _metricsServerUri = null!;


    /// <summary>Initializes a metrics cache with proxy configuration and model token pricing.</summary>
    public TokenMetricsCache(ProxyConfig options,
        TokenomicsSettings tokenomicsSettings,
        TokenRollupCollector rollupCollector,
        ConfigChangeNotifier configChangeNotifier)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tokenomicsSettings = tokenomicsSettings ?? throw new ArgumentNullException(nameof(tokenomicsSettings));
        _rollupCollector = rollupCollector ?? throw new ArgumentNullException(nameof(rollupCollector));

            InitVars();

        configChangeNotifier.Subscribe(this,
           [options => options.TokenomicsEnable,
            options => options.TokenomicsMetricsServer]);
    }

    public Task OnConfigChangedAsync(IReadOnlyList<ConfigChange> changes, ProxyConfig backendOptions, CancellationToken cancellationToken)
    {
        InitVars();
        return Task.CompletedTask;
    }

    public void InitVars()
    {
        if (string.IsNullOrWhiteSpace(_options.TokenomicsMetricsServer))
        {
            _options.TokenomicsEnable = false;
            return;
        }
        _metricsServerUri = new Uri(_options.TokenomicsMetricsServer.TrimEnd('/') + "/tokenomics/metrics/lookup");
    }



    /// <summary>Records the minimal fact-set needed by the token rollup and optional request-outcome trend analysis.</summary>
    /// <param name="CachedTokens">Cached input tokens (a subset of <paramref name="InputTokens"/>), billed at a different rate than non-cached input. Pricing is applied by the MetricsServer, not this cache; not billed locally. Must not exceed <paramref name="InputTokens"/>.</param>
    /// <param name="IsJailbreakDetected">Whether the response's prompt filter results detected a jailbreak (prompt injection) attempt.</param>
    /// <param name="IsContentFiltered">Whether any prompt or completion content filter category was flagged as filtered.</param>
    public void AddMetric(
        string UserId,
        string Model,
        int InputTokens,
        int OutputTokens,
        int CachedTokens = 0,
        bool IsJailbreakDetected = false,
        bool IsContentFiltered = false,
        int? statusCode = null,
        double? latencyMs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(UserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Model);

        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        _pendingMetrics.Enqueue(new PendingMetric(
            UserId,
            Model,
            InputTokens,
            OutputTokens,
            CachedTokens,
            IsJailbreakDetected,
            IsContentFiltered,
            day,
            statusCode,
            latencyMs,
            DateTime.UtcNow));
    }


    /// <summary>Records the final status code and total latency for a request for rate and trend analysis.</summary>
    public void RecordRequestOutcome(string userId, string model, int statusCode, double latencyMs)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            userId = "unknown";
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            model = "unknown";
        }

        AddMetric(userId, model, 0, 0, statusCode: statusCode, latencyMs: Math.Max(0d, latencyMs));
    }

    /// <summary>Drains the collector's current cycle and rolls each metric into jailbreak and content-filter counts plus request-outcome samples.</summary>
    /// <remarks>Per-instance token and USD-spend rollups are intentionally not computed here: a
    /// single instance only sees its own share of a user's traffic, so daily/monthly totals or
    /// cost derived locally would be incomplete. That data is pushed to the MetricsServer, which
    /// holds the consolidated cross-instance view, instead of being aggregated in this cache.
    /// Every 5 collapse cycles (~5 seconds), <see cref="TokenRollupCollector.TryDetachMetrics"/>
    /// detaches the pending token deltas into a batchId-indexed CSV payload, which
    /// <see cref="TokenRollupCollector.PushPendingBatchesAsync"/> then transmits to the
    /// MetricsServer configured via <see cref="ProxyConfig.TokenomicsMetricsServer"/>.</remarks>
    private void CollapseMetrics(CancellationToken cancellationToken)
    {
        var toCollapseMetrics = Interlocked.Exchange(ref _pendingMetrics, new ConcurrentQueue<PendingMetric>());

        StringBuilder csvBuilder = new StringBuilder();
        foreach (var metric in toCollapseMetrics)
        {
            try
            {
                csvBuilder.Append(metric.ToCSV()).AppendLine();
            }
            catch
            {
                // Handle or log the exception if needed
            }
        }
        string csvCollapsedMetrics = csvBuilder.ToString();

        _rollupCollector.AddMetric(csvCollapsedMetrics);
    }

    /// <summary>Runs the periodic collapse loop until cancellation is requested.</summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(CollapseInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                CollapseMetrics(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            // TODO: Shutdown only drains one buffer; pending metrics in the other may not be rolled up.
            CollapseMetrics(CancellationToken.None);
            _isRunning = false;
        }
    }

    /// <summary>Starts periodic metric collapsing.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_isRunning)
        {
            return Task.CompletedTask;
        }

        _isRunning = true;
        _collapseTask = Task.Run(() => RunAsync(_cancellationTokenSource.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Stops periodic metric collapsing after draining queued metrics.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_collapseTask == null)
        {
            return;
        }

        _cancellationTokenSource.Cancel();
        await _collapseTask.WaitAsync(cancellationToken);
        _isRunning = false;
    }

    /// <summary>Releases resources used by the metrics cache.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    internal int GetDailyTokenBalance(string userID, string model)
    {
        throw new NotImplementedException();
    }

    internal int GetMonthlyTokenBalance(string userID, string model)
    {
        throw new NotImplementedException();
    }

    internal decimal GetMonthlyBudgetUsage(string userID, string model)
    {
        throw new NotImplementedException();
    }

    internal decimal GetDailyBudgetUsage(string userID, string model)
    {
        throw new NotImplementedException();
    }
}