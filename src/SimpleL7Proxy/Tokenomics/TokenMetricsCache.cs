using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SimpleL7Proxy.Config;

namespace SimpleL7Proxy.Tokenomics;

public class TokenMetricsCache : IHostedService, IDisposable
{
    private static readonly TimeSpan CollapseInterval = TimeSpan.FromSeconds(1);

    // Push collapsed up metrics here every second
    private readonly TokenRollupCollector _rollupCollector;
    private readonly ConcurrentDictionary<(string UserId, DateOnly Day), long> _dailyJailbreakCount = new();
    private readonly ConcurrentDictionary<(string UserId, DateOnly Month), long> _monthlyJailbreakCount = new();
    private readonly ConcurrentDictionary<(string UserId, DateOnly Day), long> _dailyContentFilteredCount = new();
    private readonly ConcurrentDictionary<(string UserId, DateOnly Month), long> _monthlyContentFilteredCount = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly IOptions<ProxyConfig> _options;

    private Task? _collapseTask;
    private volatile bool _isRunning;
    private bool _disposed;
    private ConcurrentQueue<PendingMetric> _pendingMetrics = new();

    private readonly TokenomicsSettings _tokenomicsSettings;

    /// <summary>Initializes a metrics cache with proxy configuration and model token pricing.</summary>
    public TokenMetricsCache(IOptions<ProxyConfig> options,
        TokenomicsSettings tokenomicsSettings,
        TokenRollupCollector rollupCollector)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tokenomicsSettings = tokenomicsSettings ?? throw new ArgumentNullException(nameof(tokenomicsSettings));
        _rollupCollector = rollupCollector ?? throw new ArgumentNullException(nameof(rollupCollector));
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

    /// <summary>Gets the current token balance for a user and model including the live and rolled-up totals.</summary>
    public long GetTokenBalance(string UserId, string Model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(UserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Model);

        return _rollupCollector.GetTokenBalance(UserId, Model);
    }

    /// <summary>Gets the count of jailbreak (prompt injection) detections for a user on the current UTC day, excluding queued metrics.</summary>
    public long GetDailyJailbreakCount(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var dailyKey = (userId, DateOnly.FromDateTime(DateTime.UtcNow));
        return _dailyJailbreakCount.TryGetValue(dailyKey, out var count) ? count : 0L;
    }

    /// <summary>Gets the count of jailbreak (prompt injection) detections for a user in the current UTC month, excluding queued metrics.</summary>
    public long GetMonthlyJailbreakCount(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monthlyKey = (userId, new DateOnly(today.Year, today.Month, 1));
        return _monthlyJailbreakCount.TryGetValue(monthlyKey, out var count) ? count : 0L;
    }

    /// <summary>Gets the count of responses with a filtered content category for a user on the current UTC day, excluding queued metrics.</summary>
    public long GetDailyContentFilteredCount(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var dailyKey = (userId, DateOnly.FromDateTime(DateTime.UtcNow));
        return _dailyContentFilteredCount.TryGetValue(dailyKey, out var count) ? count : 0L;
    }

    /// <summary>Gets the count of responses with a filtered content category for a user in the current UTC month, excluding queued metrics.</summary>
    public long GetMonthlyContentFilteredCount(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monthlyKey = (userId, new DateOnly(today.Year, today.Month, 1));
        return _monthlyContentFilteredCount.TryGetValue(monthlyKey, out var count) ? count : 0L;
    }

    private readonly ConcurrentDictionary<string, ConcurrentQueue<RequestOutcomeSample>> _requestSamplesByUser = new();
    private const int MaxRequestSamplesPerUser = 10000;

    private readonly struct RequestOutcomeSample
    {
        public RequestOutcomeSample(DateTime timestampUtc, int statusCode, double latencyMs)
        {
            TimestampUtc = timestampUtc;
            StatusCode = statusCode;
            LatencyMs = latencyMs;
        }

        public DateTime TimestampUtc { get; }
        public int StatusCode { get; }
        public double LatencyMs { get; }
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

    private ConcurrentQueue<RequestOutcomeSample> GetUserSampleQueue(string userId)
    {
        return _requestSamplesByUser.GetOrAdd(userId, static _ => new ConcurrentQueue<RequestOutcomeSample>());
    }

    private IEnumerable<RequestOutcomeSample> GetSamplesForWindow(TimeSpan window, string? userId = null)
    {
        if (window <= TimeSpan.Zero)
        {
            yield break;
        }

        var cutoff = DateTime.UtcNow - window;

        if (string.IsNullOrWhiteSpace(userId))
        {
            foreach (var sampleQueue in _requestSamplesByUser.Values)
            {
                foreach (var sample in sampleQueue)
                {
                    if (sample.TimestampUtc >= cutoff)
                    {
                        yield return sample;
                    }
                }
            }

            yield break;
        }

        if (_requestSamplesByUser.TryGetValue(userId, out var queue))
        {
            foreach (var sample in queue)
            {
                if (sample.TimestampUtc >= cutoff)
                {
                    yield return sample;
                }
            }
        }
    }

    /// <summary>Gets the average delay between 429 responses in the supplied window, measured in seconds.</summary>
    public double Get429Rate(TimeSpan window, string? userId = null)
    {
        var samples = GetSamplesForWindow(window, userId)
            .Where(s => s.StatusCode == 429)
            .OrderBy(s => s.TimestampUtc)
            .ToList();

        if (samples.Count < 2)
        {
            return 0d;
        }

        var totalGapSeconds = 0d;
        for (var i = 1; i < samples.Count; i++)
        {
            totalGapSeconds += (samples[i].TimestampUtc - samples[i - 1].TimestampUtc).TotalSeconds;
        }

        return totalGapSeconds / (samples.Count - 1);
    }

    /// <summary>Counts 429 responses observed in the supplied window.</summary>
    public int Get429Count(TimeSpan window, string? userId = null)
    {
        return GetSamplesForWindow(window, userId)
            .Count(sample => sample.StatusCode == 429);
    }

    /// <summary>Gets the average request latency for the supplied window in milliseconds.</summary>
    public double GetAverageLatencyMs(TimeSpan window, string? userId = null)
    {
        var samples = GetSamplesForWindow(window, userId).ToList();
        if (samples.Count == 0)
        {
            return 0d;
        }

        return samples.Average(sample => sample.LatencyMs);
    }

    /// <summary>Returns the percentage change between the current and baseline latency windows; positive means slower than baseline.</summary>
    public double GetLatencyDeltaPercent(TimeSpan currentWindow, TimeSpan baselineWindow, string? userId = null)
    {
        if (baselineWindow <= TimeSpan.Zero)
        {
            return 0d;
        }

        var currentAverage = GetAverageLatencyMs(currentWindow, userId);
        var baselineAverage = GetAverageLatencyMs(baselineWindow, userId);

        if (baselineAverage <= 0d)
        {
            return currentAverage > 0d ? 100d : 0d;
        }

        return ((currentAverage - baselineAverage) / baselineAverage) * 100d;
    }

    private readonly ConcurrentDictionary<string, DateTimeOffset> _modelWakeStates = new();

    public void ModelThrottled(string Model, int RetryAfterSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Model);
        if (RetryAfterSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(RetryAfterSeconds));

        var wake = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(RetryAfterSeconds);

        _modelWakeStates.AddOrUpdate(Model, wake, (_, __) => wake);
    }

    public bool IsModelAvailable(string Model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Model);

        while (_modelWakeStates.TryGetValue(Model, out var wakeUtc))
        {
            if (DateTimeOffset.UtcNow < wakeUtc)
            {
                return false;
            }

            if (_modelWakeStates.TryRemove(new KeyValuePair<string, DateTimeOffset>(Model, wakeUtc)))
            {
                return true;
            }
        }

        return true;
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

        await _cancellationTokenSource.CancelAsync();
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
}