using System.Collections.Concurrent;
using System.Globalization;
using SimpleL7Proxy.Tokenomics;

namespace MetricsServer;

/// <summary>
/// Aggregates tokenomics rollup deltas into queryable per-user/model totals.
/// Populated by <see cref="TokenomicsRollupProcessor"/> as it drains the rollups queue; queried
/// by the tokenomics lookup route. Per the upload contract, senders upload deltas only (never
/// cumulative totals); this store derives the running totals.
/// </summary>
public sealed class TokenomicsMetricsStore
{
    private sealed class InternalMetric
    {
        public long InputTokens;
        public long OutputTokens;
        public long CachedTokens;
        public bool IsJailbreakDetected;
        public bool IsContentFiltered;
        public long StatusSamples;
        public long Status429;
        public double LatencyMsTotal;
        public long LatencySamples;
    }

    private readonly ConcurrentDictionary<string, InternalMetric> _dailyByUserModel = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, InternalMetric> _dailyByModel = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, InternalMetric> _monthlyByUserModel = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, InternalMetric> _monthlyByModel = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _rollupLock = new();

    private DateOnly _currentDay;
    private int _currentMonth;

    /// <summary>
    /// Updates the current-day and current-month user/model and model aggregates.
    /// </summary>
    public void Record(in PendingMetric metric)
    {
        var user = Normalize(metric.UserId);
        var model = Normalize(metric.Model);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var month = (today.Year * 100) + today.Month;

        lock (_rollupLock)
        {
            if (_currentDay != today)
            {
                _dailyByUserModel.Clear();
                _dailyByModel.Clear();
                _currentDay = today;
            }

            if (_currentMonth != month)
            {
                _monthlyByUserModel.Clear();
                _monthlyByModel.Clear();
                _currentMonth = month;
            }

            if (metric.Day != today)
            {
                return;
            }

            Accumulate(
                _dailyByUserModel.GetOrAdd(
                    DailyUserModelKey(user, model, today),
                    _ => new InternalMetric()),
                metric);
            Accumulate(_dailyByModel.GetOrAdd(model, _ => new InternalMetric()), metric);
            Accumulate(
                _monthlyByUserModel.GetOrAdd(
                    MonthlyUserModelKey(user, model, today.Year, today.Month),
                    _ => new InternalMetric()),
                metric);
            Accumulate(_monthlyByModel.GetOrAdd(model, _ => new InternalMetric()), metric);
        }
    }

    /// <summary>Gets all available metrics for a user and model combination.</summary>
    public ResponseMetric GetMetrics(string? userId, string? model)
    {
        var user = Normalize(userId);
        var normalizedModel = Normalize(model);
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        var month = (today.Year * 100) + today.Month;

        lock (_rollupLock)
        {
            if (_currentDay != today)
            {
                _dailyByUserModel.Clear();
                _dailyByModel.Clear();
                _currentDay = today;
            }

            if (_currentMonth != month)
            {
                _monthlyByUserModel.Clear();
                _monthlyByModel.Clear();
                _currentMonth = month;
            }

            _dailyByUserModel.TryGetValue(
                DailyUserModelKey(user, normalizedModel, today),
                out var daily);
            _monthlyByUserModel.TryGetValue(
                MonthlyUserModelKey(user, normalizedModel, today.Year, today.Month),
                out var monthly);
            _dailyByModel.TryGetValue(normalizedModel, out var dailyModel);
            _monthlyByModel.TryGetValue(normalizedModel, out var monthlyModel);

            return new ResponseMetric(
                user,
                normalizedModel,
                checked((int)(daily?.InputTokens ?? 0)),
                checked((int)(daily?.OutputTokens ?? 0)),
                checked((int)(daily?.CachedTokens ?? 0)),
                daily?.IsJailbreakDetected ?? false,
                daily?.IsContentFiltered ?? false,
                dailyModel is { StatusSamples: > 0 } ? checked((int)dailyModel.Status429) : 0,
                daily is { StatusSamples: > 0 } ? checked((int)daily.Status429) : 0,
                checked((int)(monthly?.InputTokens ?? 0)),
                checked((int)(monthly?.OutputTokens ?? 0)),
                checked((int)(monthly?.CachedTokens ?? 0)),
                monthly?.IsJailbreakDetected ?? false,
                monthly?.IsContentFiltered ?? false,
                monthlyModel is { StatusSamples: > 0 } ? checked((int)monthlyModel.Status429) : 0,
                monthly is { StatusSamples: > 0 } ? checked((int)monthly.Status429) : 0,
                daily is { LatencySamples: > 0 }
                    ? daily.LatencyMsTotal / daily.LatencySamples
                    : 0,
                monthly is { LatencySamples: > 0 }
                    ? monthly.LatencyMsTotal / monthly.LatencySamples
                    : 0,
                now);
        }
    }

    private static void Accumulate(InternalMetric totals, in PendingMetric metric)
    {
        totals.InputTokens += metric.InputTokens;
        totals.OutputTokens += metric.OutputTokens;
        totals.CachedTokens += metric.CachedTokens;
        totals.IsJailbreakDetected |= metric.IsJailbreakDetected;
        totals.IsContentFiltered |= metric.IsContentFiltered;

        if (metric.StatusCode.HasValue)
        {
            totals.StatusSamples++;
            if (metric.StatusCode == 429)
            {
                totals.Status429++;
            }
        }

        if (metric.LatencyMs.HasValue)
        {
            totals.LatencyMsTotal += metric.LatencyMs.Value;
            totals.LatencySamples++;
        }
    }

    private static string Normalize(string? userId) =>
        string.IsNullOrWhiteSpace(userId) ? "unknown" : userId.Trim();

    private static string DailyUserModelKey(string userId, string model, DateOnly day) =>
        string.Concat(
            userId,
            "\u0000",
            model,
            "\u0000",
            day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    private static string MonthlyUserModelKey(string userId, string model, int year, int month) =>
        string.Concat(
            userId,
            "\u0000",
            model,
            "\u0000",
            year.ToString("D4", CultureInfo.InvariantCulture),
            "-",
            month.ToString("D2", CultureInfo.InvariantCulture));
}
