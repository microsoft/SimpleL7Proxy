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
    private sealed class Totals
    {
        public long Tokens;
    }

    private readonly ConcurrentDictionary<string, Totals> _dailyByUserModel = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Totals> _monthlyByUserModel = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Merges one normalized rollup delta into the user/model daily and monthly totals. Empty
    /// identifiers are recorded as "unknown", matching the convention used elsewhere.
    /// </summary>
    public void Record(string? userId, string? model, DateOnly day, long tokens)
    {
        var user = Normalize(userId);
        var normalizedModel = Normalize(model);

        Accumulate(
            _dailyByUserModel.GetOrAdd(DailyUserModelKey(user, normalizedModel, day), _ => new Totals()),
            tokens);
        Accumulate(
            _monthlyByUserModel.GetOrAdd(MonthlyUserModelKey(user, normalizedModel, day.Year, day.Month), _ => new Totals()),
            tokens);
    }

    /// <summary>Gets all available metrics for a user and model combination.</summary>
    public MetricsLookupResponse GetMetrics(string? userId, string? model)
    {
        var user = Normalize(userId);
        var normalizedModel = Normalize(model);
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);

        return new MetricsLookupResponse
        {
            UserId = user,
            Model = normalizedModel,
            DailyTokenBalance = GetTokens(
                _dailyByUserModel,
                DailyUserModelKey(user, normalizedModel, today)),
            MonthlyTokenBalance = GetTokens(
                _monthlyByUserModel,
                MonthlyUserModelKey(user, normalizedModel, now.Year, now.Month)),
            DailyBudgetUsage = 0m,
            MonthlyBudgetUsage = 0m,
            IsAbuseDetected = false,
            HasApprovedException = false,
            HasAdministratorOverride = false
        };
    }

    private static void Accumulate(Totals totals, long tokens)
    {
        lock (totals)
        {
            totals.Tokens += tokens;
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
