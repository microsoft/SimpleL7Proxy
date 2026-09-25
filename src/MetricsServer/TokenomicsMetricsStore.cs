using System.Collections.Concurrent;
using System.Globalization;

namespace MetricsServer;

/// <summary>
/// Aggregates tokenomics rollup deltas into queryable per-user daily and monthly totals.
/// Populated by <see cref="TokenomicsRollupProcessor"/> as it drains the rollups queue; queried
/// by the tokenomics daily/monthly token and budget lookup routes. Per the upload contract,
/// senders upload deltas only (never cumulative totals); this store derives the running totals.
/// </summary>
public sealed class TokenomicsMetricsStore
{
    private sealed class Totals
    {
        public long Tokens;
        public decimal CostUsd;
    }

    private readonly ConcurrentDictionary<string, Totals> _daily = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Totals> _monthly = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Merges one normalized rollup delta into the user's daily and monthly totals. Empty user
    /// ids are recorded as "unknown", matching the convention used elsewhere in this service.
    /// </summary>
    public void Record(string? userId, DateOnly day, long tokens, decimal costUsd)
    {
        var user = Normalize(userId);
        Accumulate(_daily.GetOrAdd(DailyKey(user, day), _ => new Totals()), tokens, costUsd);
        Accumulate(_monthly.GetOrAdd(MonthlyKey(user, day.Year, day.Month), _ => new Totals()), tokens, costUsd);
    }

    /// <summary>Gets the user's input + output token usage for the current UTC day.</summary>
    public long GetDailyTokenBalance(string? userId) =>
        GetTokens(_daily, DailyKey(Normalize(userId), Today()));

    /// <summary>Gets the user's input + output token usage for the current UTC month.</summary>
    public long GetMonthlyTokenBalance(string? userId)
    {
        var now = DateTime.UtcNow;
        return GetTokens(_monthly, MonthlyKey(Normalize(userId), now.Year, now.Month));
    }

    /// <summary>Gets the user's USD spend for the current UTC day.</summary>
    public decimal GetDailyBudgetUsage(string? userId) =>
        GetCost(_daily, DailyKey(Normalize(userId), Today()));

    /// <summary>Gets the user's USD spend for the current UTC month.</summary>
    public decimal GetMonthlyBudgetUsage(string? userId)
    {
        var now = DateTime.UtcNow;
        return GetCost(_monthly, MonthlyKey(Normalize(userId), now.Year, now.Month));
    }

    private static void Accumulate(Totals totals, long tokens, decimal costUsd)
    {
        lock (totals)
        {
            totals.Tokens += tokens;
            totals.CostUsd += costUsd;
        }
    }

    private static long GetTokens(ConcurrentDictionary<string, Totals> dict, string key)
    {
        if (!dict.TryGetValue(key, out var totals))
        {
            return 0;
        }

        lock (totals)
        {
            return totals.Tokens;
        }
    }

    private static decimal GetCost(ConcurrentDictionary<string, Totals> dict, string key)
    {
        if (!dict.TryGetValue(key, out var totals))
        {
            return 0m;
        }

        lock (totals)
        {
            return totals.CostUsd;
        }
    }

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow);

    private static string Normalize(string? userId) =>
        string.IsNullOrWhiteSpace(userId) ? "unknown" : userId.Trim();

    // A NUL separator keeps the composite key unambiguous regardless of characters in userId.
    private static string DailyKey(string userId, DateOnly day) =>
        string.Concat(userId, "\u0000", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    private static string MonthlyKey(string userId, int year, int month) =>
        string.Concat(
            userId,
            "\u0000",
            year.ToString("D4", CultureInfo.InvariantCulture),
            "-",
            month.ToString("D2", CultureInfo.InvariantCulture));
}
