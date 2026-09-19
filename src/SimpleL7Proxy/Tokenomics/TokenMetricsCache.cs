using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SimpleL7Proxy.Config;

namespace SimpleL7Proxy.Tokenomics;

public class TokenMetricsCache : IHostedService, IDisposable
{
    private static readonly TimeSpan CollapseInterval = TimeSpan.FromSeconds(1);
    private const int InputTokensIndex = 0;
    private const int OutputTokensIndex = 1;

    private readonly ConcurrentQueue<(string UserId, string Model, int InputTokens, int OutputTokens, DateOnly Day)>[] _metrics =
        [new(), new()];
    private readonly ConcurrentDictionary<(string UserId, string Model), long[]> _aggregateBalance = new();
    private readonly ConcurrentDictionary<(string UserId, DateOnly Day), long> _dailyTokenBalance = new();
    private readonly ConcurrentDictionary<(string UserId, DateOnly Month), long> _monthlyTokenBalance = new();
    private readonly ConcurrentDictionary<(string UserId, DateOnly Day), decimal> _dailyBudgetUsage = new();
    private readonly ConcurrentDictionary<(string UserId, DateOnly Month), decimal> _monthlyBudgetUsage = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly IOptions<ProxyConfig> _options;

    private Task? _collapseTask;
    private int _activeQueueIndex;
    private volatile bool _isRunning;
    private bool _disposed;

    private readonly TokenomicsSettings _tokenomicsSettings;

    /// <summary>Initializes a metrics cache with proxy configuration and model token pricing.</summary>
    public TokenMetricsCache(IOptions<ProxyConfig> options, TokenomicsSettings tokenomicsSettings)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tokenomicsSettings = tokenomicsSettings ?? throw new ArgumentNullException(nameof(tokenomicsSettings));
    }

    /// <summary>Adds input and output token counts for a user and model to the active queue.</summary>
    public void AddMetric(string UserId, string Model, int InputTokens, int OutputTokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(UserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Model);

        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        var queueIndex = Volatile.Read(ref _activeQueueIndex);
        _metrics[queueIndex].Enqueue((UserId, Model, InputTokens, OutputTokens, day));
    }

    /// <summary>Gets the current token balance for a user and model including the live and rolled-up totals.</summary>
    public long GetTokenBalance(string UserId, string Model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(UserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Model);

        var rollupKey = (UserId, Model);
        var tokenBalance = 0L;

        if (_aggregateBalance.TryGetValue(rollupKey, out var aggregate))
        {
            tokenBalance += Interlocked.Read(ref aggregate[InputTokensIndex]);
            tokenBalance += Interlocked.Read(ref aggregate[OutputTokensIndex]);
        }

        for (var i = 0; i < _metrics.Length; i++)
        {
            foreach (var metric in _metrics[i])
            {
                if (metric.UserId == UserId && metric.Model == Model)
                {
                    tokenBalance += (long)metric.InputTokens + metric.OutputTokens;
                }
            }
        }

        return tokenBalance;
    }

    /// <summary>Gets a user's rolled-up input and output token total across all models for the current UTC day, excluding queued metrics.</summary>
    public long GetDailyTokenBalance(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var dailyKey = (userId, DateOnly.FromDateTime(DateTime.UtcNow));
        return _dailyTokenBalance.TryGetValue(dailyKey, out var tokenBalance) ? tokenBalance : 0L;
    }

    /// <summary>Gets a user's rolled-up input and output token total across all models for the current UTC month, excluding queued metrics.</summary>
    public long GetMonthlyTokenBalance(string userId) {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monthlyKey = (userId, new DateOnly(today.Year, today.Month, 1));
        return _monthlyTokenBalance.TryGetValue(monthlyKey, out var tokenBalance) ? tokenBalance : 0L;
    }

    /// <summary>Gets a user's rolled-up USD spend across priced models for the current UTC day, excluding queued metrics.</summary>
    public decimal GetDailyBudgetUsage(string userId) {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var dailyKey = (userId, DateOnly.FromDateTime(DateTime.UtcNow));
        return _dailyBudgetUsage.TryGetValue(dailyKey, out var budgetUsage) ? budgetUsage : 0m;
    }

    /// <summary>Gets a user's rolled-up USD spend across priced models for the current UTC month, excluding queued metrics.</summary>
    public decimal GetMonthlyBudgetUsage(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monthlyKey = (userId, new DateOnly(today.Year, today.Month, 1));
        return _monthlyBudgetUsage.TryGetValue(monthlyKey, out var budgetUsage) ? budgetUsage : 0m;
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


    /// <summary>Swaps the active queue and rolls the previous queue into per-model totals plus daily and monthly token usage and USD spend.</summary>
    private void CollapseMetrics()
    {
        var queueToCollapseIndex = Volatile.Read(ref _activeQueueIndex);

        // flip the binary index to get the next queue index
        var nextQueueIndex = queueToCollapseIndex ^ 1;

        Interlocked.Exchange(ref _activeQueueIndex, nextQueueIndex);

        while (_metrics[queueToCollapseIndex].TryDequeue(out var metric))
        {
            var rollupKey = (metric.UserId, metric.Model);
            var aggregate = _aggregateBalance.GetOrAdd(rollupKey, static _ => new long[2]);

            Interlocked.Add(ref aggregate[InputTokensIndex], metric.InputTokens);
            Interlocked.Add(ref aggregate[OutputTokensIndex], metric.OutputTokens);

            var dailyKey = (metric.UserId, metric.Day);
            var tokenCount = (long)metric.InputTokens + metric.OutputTokens;
            _dailyTokenBalance.AddOrUpdate(dailyKey,
                static (_, tokens) => tokens,
                static (_, balance, tokens) => balance + tokens,
                tokenCount);

            var monthlyKey = (metric.UserId, new DateOnly(metric.Day.Year, metric.Day.Month, 1));
            _monthlyTokenBalance.AddOrUpdate(monthlyKey,
                static (_, tokens) => tokens,
                static (_, balance, tokens) => balance + tokens,
                tokenCount);

            if (_tokenomicsSettings.ModelCostPerToken.TryGetValue(metric.Model, out var costPerToken))
            {
                var metricCost = tokenCount * costPerToken;
                _dailyBudgetUsage.AddOrUpdate(dailyKey,
                    static (_, cost) => cost,
                    static (_, usage, cost) => usage + cost,
                    metricCost);
                _monthlyBudgetUsage.AddOrUpdate(monthlyKey,
                    static (_, cost) => cost,
                    static (_, usage, cost) => usage + cost,
                    metricCost);
            }
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var dailyBalance in _dailyTokenBalance)
        {
            if (dailyBalance.Key.Day < today)
            {
                _dailyTokenBalance.TryRemove(dailyBalance.Key, out _);
            }
        }

        foreach (var budgetUsage in _dailyBudgetUsage) {
            if (budgetUsage.Key.Day < today) {
                _dailyBudgetUsage.TryRemove(budgetUsage.Key, out _);
            }
        }

        var currentMonth = new DateOnly(today.Year, today.Month, 1);
        foreach (var monthlyBalance in _monthlyTokenBalance) {
            if (monthlyBalance.Key.Month < currentMonth) {
                _monthlyTokenBalance.TryRemove(monthlyBalance.Key, out _);
            }
        }

        foreach (var budgetUsage in _monthlyBudgetUsage)
        {
            if (budgetUsage.Key.Month < currentMonth)
            {
                _monthlyBudgetUsage.TryRemove(budgetUsage.Key, out _);
            }
        }
    }

    /// <summary>Runs the periodic collapse loop until cancellation is requested.</summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(CollapseInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                CollapseMetrics();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            // TODO: Shutdown only drains one buffer; pending metrics in the other may not be rolled up.
            CollapseMetrics();
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