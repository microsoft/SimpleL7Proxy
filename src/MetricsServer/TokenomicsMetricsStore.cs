using System.Runtime.InteropServices;
using SimpleL7Proxy.Tokenomics;

namespace MetricsServer;

/// <summary>
/// Aggregates tokenomics rollup deltas into queryable per-user/model totals for the current hour,
/// day, and month. One <see cref="Aggregate"/> per key holds all three period slices, each rolling
/// itself forward lazily when its stamp changes. Populated by <see cref="TokenomicsRollupProcessor"/>;
/// queried by the tokenomics lookup route. Senders upload deltas only; this store derives the totals.
/// </summary>
public sealed class TokenomicsMetricsStore
{
    private readonly record struct UserModelKey(string User, string Model);

    private sealed class UserModelKeyComparer : IEqualityComparer<UserModelKey>
    {
        public static readonly UserModelKeyComparer Instance = new();

        public bool Equals(UserModelKey left, UserModelKey right) =>
            StringComparer.OrdinalIgnoreCase.Equals(left.User, right.User)
            && StringComparer.OrdinalIgnoreCase.Equals(left.Model, right.Model);

        public int GetHashCode(UserModelKey key) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.User),
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.Model));
    }

    /// <summary>Running totals for one period (hour, day, or month), reset when its stamp advances.</summary>
    private sealed class Period
    {
        public long Stamp;
        public long InputTokens;
        public long OutputTokens;
        public long CachedTokens;
        public bool IsJailbreakDetected;
        public bool IsContentFiltered;
        public long StatusSamples;
        public long Status429;
        public double LatencyMsTotal;
        public long LatencySamples;

        public void RollTo(long stamp)
        {
            if (Stamp == stamp)
            {
                return;
            }

            InputTokens = 0;
            OutputTokens = 0;
            CachedTokens = 0;
            IsJailbreakDetected = false;
            IsContentFiltered = false;
            StatusSamples = 0;
            Status429 = 0;
            LatencyMsTotal = 0;
            LatencySamples = 0;
            Stamp = stamp;
        }
    }

    private sealed class Aggregate
    {
        public readonly Period Hour = new();
        public readonly Period Day = new();
        public readonly Period Month = new();
    }

    private readonly Dictionary<UserModelKey, Aggregate> _byUserModel = new(UserModelKeyComparer.Instance);
    private readonly Dictionary<string, Aggregate> _byModel = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _rollupLock = new();

    private long _totalRecordsProcessed; // Total records ever recorded (including duplicates).

    /// <summary>Updates the current-hour, current-day, and current-month user/model and model aggregates.</summary>
    public void Record(in PendingMetric metric)
    {
        var user = Normalize(metric.UserId);
        var model = Normalize(metric.Model);
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);

        var hour = HourStamp(now);
        var day = DayStamp(today);
        var month = MonthStamp(today);

        lock (_rollupLock)
        {
            if (metric.Day != today)
            {
                return;
            }

            _totalRecordsProcessed++;

            ref var byUserModel = ref CollectionsMarshal.GetValueRefOrAddDefault(
                _byUserModel, new UserModelKey(user, model), out _);
            byUserModel ??= new Aggregate();
            ApplyAll(byUserModel, metric, hour, day, month);

            ref var byModel = ref CollectionsMarshal.GetValueRefOrAddDefault(_byModel, model, out _);
            byModel ??= new Aggregate();
            ApplyAll(byModel, metric, hour, day, month);
        }
    }

    /// <summary>Gets all available metrics for a user and model combination.</summary>
    public ResponseMetric GetMetrics(string? userId, string? model, TokenomicsSettings? settings = null)
    {
        var user = Normalize(userId);
        var normalizedModel = Normalize(model);
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);

        var hour = HourStamp(now);
        var day = DayStamp(today);
        var month = MonthStamp(today);
        var pricing = settings?.ModelCostPerToken
            .FirstOrDefault(entry => string.Equals(entry.Key, normalizedModel, StringComparison.OrdinalIgnoreCase)).Value;

        lock (_rollupLock)
        {
            _byUserModel.TryGetValue(new UserModelKey(user, normalizedModel), out var um);
            _byModel.TryGetValue(normalizedModel, out var mm);

            // A slice whose stamp isn't current has elapsed; treat it as empty without mutating.
            var hourly = Current(um?.Hour, hour);
            var daily = Current(um?.Day, day);
            var monthly = Current(um?.Month, month);
            var hourlyModel = Current(mm?.Hour, hour);
            var dailyModel = Current(mm?.Day, day);
            var monthlyModel = Current(mm?.Month, month);

            return new ResponseMetric(
                user,
                normalizedModel,
                daily?.InputTokens ?? 0,
                daily?.OutputTokens ?? 0,
                daily?.CachedTokens ?? 0,
                daily?.IsJailbreakDetected ?? false,
                daily?.IsContentFiltered ?? false,
                dailyModel is { StatusSamples: > 0 } ? checked((int)dailyModel.Status429) : 0,
                daily is { StatusSamples: > 0 } ? checked((int)daily.Status429) : 0,
                monthly?.InputTokens ?? 0,
                monthly?.OutputTokens ?? 0,
                monthly?.CachedTokens ?? 0,
                monthly?.IsJailbreakDetected ?? false,
                monthly?.IsContentFiltered ?? false,
                monthlyModel is { StatusSamples: > 0 } ? checked((int)monthlyModel.Status429) : 0,
                monthly is { StatusSamples: > 0 } ? checked((int)monthly.Status429) : 0,
                daily is { LatencySamples: > 0 } ? daily.LatencyMsTotal / daily.LatencySamples : 0,
                monthly is { LatencySamples: > 0 } ? monthly.LatencyMsTotal / monthly.LatencySamples : 0,
                now,
                hourlyInputTokens: hourly?.InputTokens ?? 0,
                hourlyOutputTokens: hourly?.OutputTokens ?? 0,
                hourlyCachedTokens: hourly?.CachedTokens ?? 0,
                isHourlyJailbreakDetected: hourly?.IsJailbreakDetected ?? false,
                isHourlyContentFiltered: hourly?.IsContentFiltered ?? false,
                hourlyModel429: hourlyModel is { StatusSamples: > 0 } ? checked((int)hourlyModel.Status429) : 0,
                hourlyUser429: hourly is { StatusSamples: > 0 } ? checked((int)hourly.Status429) : 0,
                hourlyAvgLatencyMs: hourly is { LatencySamples: > 0 } ? hourly.LatencyMsTotal / hourly.LatencySamples : 0,
                hourlyUserBudget: Budget(hourly, pricing),
                dailyUserBudget: Budget(daily, pricing),
                monthlyUserBudget: Budget(monthly, pricing),
                hourlyModelBudget: Budget(hourlyModel, pricing),
                dailyModelBudget: Budget(dailyModel, pricing),
                monthlyModelBudget: Budget(monthlyModel, pricing));
        }
    }

    /// <summary>
    /// Prunes aggregates idle for the whole current month, then reports current-day diagnostics:
    /// total records processed, unique user/model combinations, unique users, unique models, and
    /// today's total tokens.
    /// </summary>
    public (long TotalRecordsProcessed, int UniqueUserModelCombinations, int UniqueUsers, int UniqueModels, long TotalTokens) GetDiagnostics()
    {
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        var day = DayStamp(today);
        var month = MonthStamp(today);

        lock (_rollupLock)
        {
            Prune(month);

            var uniqueUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var combos = 0;
            long totalTokens = 0;

            foreach (var pair in _byUserModel)
            {
                var d = pair.Value.Day;
                if (d.Stamp != day)
                {
                    continue;
                }

                combos++;
                uniqueUsers.Add(pair.Key.User);
                totalTokens += d.InputTokens + d.OutputTokens + d.CachedTokens;
            }

            var models = 0;
            foreach (var pair in _byModel)
            {
                if (pair.Value.Day.Stamp == day)
                {
                    models++;
                }
            }

            return (_totalRecordsProcessed, combos, uniqueUsers.Count, models, totalTokens);
        }
    }

    /// <summary>Removes aggregates with no activity in the current month. Caller holds <see cref="_rollupLock"/>.</summary>
    private void Prune(long currentMonth)
    {
        List<UserModelKey>? staleUserModel = null;
        foreach (var pair in _byUserModel)
        {
            if (pair.Value.Month.Stamp != currentMonth)
            {
                (staleUserModel ??= new List<UserModelKey>()).Add(pair.Key);
            }
        }

        if (staleUserModel is not null)
        {
            foreach (var key in staleUserModel)
            {
                _byUserModel.Remove(key);
            }
        }

        List<string>? staleModel = null;
        foreach (var pair in _byModel)
        {
            if (pair.Value.Month.Stamp != currentMonth)
            {
                (staleModel ??= new List<string>()).Add(pair.Key);
            }
        }

        if (staleModel is not null)
        {
            foreach (var key in staleModel)
            {
                _byModel.Remove(key);
            }
        }
    }

    private static void ApplyAll(Aggregate aggregate, in PendingMetric metric, long hour, long day, long month)
    {
        aggregate.Hour.RollTo(hour);
        Accumulate(aggregate.Hour, metric);
        aggregate.Day.RollTo(day);
        Accumulate(aggregate.Day, metric);
        aggregate.Month.RollTo(month);
        Accumulate(aggregate.Month, metric);
    }

    private static Period? Current(Period? period, long stamp) =>
        period is not null && period.Stamp == stamp ? period : null;

    private static decimal Budget(Period? period, ModelTokenPricing? pricing)
    {
        if (period is null || pricing is null)
        {
            return 0;
        }

        return Math.Max(0L, period.InputTokens - period.CachedTokens) * pricing.Input
            + period.CachedTokens * pricing.CachedInput
            + period.OutputTokens * pricing.Output;
    }

    private static void Accumulate(Period totals, in PendingMetric metric)
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

    private static long HourStamp(DateTime now) =>
        ((long)now.Year * 1_000_000) + (now.Month * 10_000) + (now.Day * 100) + now.Hour;

    private static long DayStamp(DateOnly day) =>
        ((long)day.Year * 10_000) + (day.Month * 100) + day.Day;

    private static long MonthStamp(DateOnly day) =>
        ((long)day.Year * 100) + day.Month;

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
}