using System.Collections.Immutable;
using System.Globalization;

namespace CompanionApp.Tokenomics;

/// <summary>Rolling time windows available across the Tokenomics dashboard.</summary>
public enum TokenomicsPeriod {
    Hourly,
    Daily,
    Monthly
}

/// <summary>Immutable dashboard data published as one consistent snapshot.</summary>
public sealed record TokenomicsDashboardSnapshot {
    public bool IsSampleData { get; init; }
    public string SnapshotLabel { get; init; } = string.Empty;
    public ImmutableArray<string> TimePeriods { get; init; } = [];
    public ImmutableArray<string> Tenants { get; init; } = [];
    public ImmutableArray<string> UserFilters { get; init; } = [];
    public ImmutableArray<string> ModelFilters { get; init; } = [];
    public ImmutableArray<(string Name, string Value, string Change, string Icon, string Tone, string ChangeTone)> Metrics { get; init; } = [];
    public ImmutableArray<(string Date, double InputNet, double Cached, double Output)> Trend { get; init; } = [];
    public ImmutableArray<(string Date, decimal Spend)> SpendTrend { get; init; } = [];
    public double TokenAxisMaximum { get; init; } = 8;
    public ImmutableArray<string> TokenAxisTicks { get; init; } = [];
    public ImmutableArray<string> SpendAxisTicks { get; init; } = [];
    public bool HasPriceData { get; init; }
    public bool HasQuotaData { get; init; }
    public string TrendDescription { get; init; } = string.Empty;
    public ImmutableArray<(int X, int Y)> SpendLine { get; init; } = [];
    public string TotalTokens { get; init; } = string.Empty;
    public string ModelDescription { get; init; } = string.Empty;
    public string ModelGradient { get; init; } = string.Empty;
    public ImmutableArray<(string Name, string Tokens, string Share, string Color)> Models { get; init; } = [];
    public ImmutableArray<(string Name, string Amount, string Width, string Color)> TenantSpend { get; init; } = [];
    public ImmutableArray<(string Name, string Amount, string Width, string Color)> Quotas { get; init; } = [];
    public ImmutableArray<(int Rank, string Name, string Tenant, string Total, string Input, string Output, string Cached, string Spend, string Quota, string QuotaColor, string Color, string Sparkline)> Users { get; init; } = [];
    public ImmutableArray<(int Rank, string User, string Model, string Reason, string Timestamp, string Status)> AtRiskRequests { get; init; } = [];
    public ImmutableArray<(string Name, string Description, string Icon)> Reports { get; init; } = [];

    public static TokenomicsDashboardSnapshot Sample { get; } = new() {
        IsSampleData = true,
        SnapshotLabel = "Sample snapshot · Nov 10, 2024",
        TimePeriods = ["Last 7 days", "Last 30 days"],
        Tenants = ["All tenants", "Contoso", "Fabrikam"],
        UserFilters = ["All users", "sam", "alex"],
        ModelFilters = ["All models", "gpt-4o", "claude-3-5"],
        Metrics = [
            ("Total Tokens", "48.3M", "↑ 12%", "▤", "blue", "positive"),
            ("Total Spend (USD)", "$1,924", "↑ 8%", "$", "blue", "positive"),
            ("Total Requests", "52,389", "↑ 15%", "▣", "blue", "positive"),
            ("Success Rate", "98.7%", "↑ 0.6%", "✓", "green", "positive"),
            ("Policy Actions", "1,243", "↑ 22%", "⬟", "red", "negative"),
            ("429 Throttles", "186", "↑ 34%", "!", "red", "negative")
        ],
        Trend = [
            ("Nov 3", 2.2, 0.7, 0.9), ("Nov 4", 2.1, 0.8, 0.9), ("Nov 5", 2.4, 0.9, 1.0), ("Nov 6", 2.8, 0.8, 1.1),
            ("Nov 7", 2.0, 0.7, 0.9), ("Nov 8", 2.9, 0.9, 1.0), ("Nov 9", 3.6, 1.0, 1.2), ("Nov 10", 1.8, 0.6, 0.8)
        ],
        SpendTrend = [
            ("Nov 3", 168m), ("Nov 4", 175m), ("Nov 5", 232m), ("Nov 6", 248m),
            ("Nov 7", 184m), ("Nov 8", 256m), ("Nov 9", 312m), ("Nov 10", 145m)
        ],
        TokenAxisTicks = ["8M", "6M", "4M", "2M", "0"],
        SpendAxisTicks = ["$400", "$300", "$200", "$100", "$0"],
        HasPriceData = true,
        HasQuotaData = true,
        TrendDescription = "Sample chart showing daily input, output, cached tokens and spend from November 3 through November 10",
        SpendLine = [(54, 75), (153, 83), (251, 61), (349, 81), (447, 92), (545, 68), (643, 45), (742, 79)],
        TotalTokens = "48.3M",
        ModelDescription = "gpt-4o 58.8%, gpt-4o-mini 16.8%, claude-3-5 10.8%, gpt-4.1 7%, o3-mini 3.9%, other 2.7%",
        ModelGradient = "conic-gradient(#287cf0 0 58.8%, #31c1df 58.8% 75.6%, #48c58a 75.6% 86.4%, #9652ef 86.4% 93.4%, #ffa21b 93.4% 97.3%, #9ba9c1 97.3% 100%)",
        Models = [
            ("gpt-4o", "28.4M", "58.8%", "model-blue"), ("gpt-4o-mini", "8.1M", "16.8%", "model-cyan"),
            ("claude-3-5", "5.2M", "10.8%", "model-green"), ("gpt-4.1", "3.4M", "7.0%", "model-purple"),
            ("o3-mini", "1.9M", "3.9%", "model-orange"), ("Other", "1.3M", "2.7%", "model-gray")
        ],
        TenantSpend = [
            ("Contoso", "$642", "100%", "bar-blue"), ("Fabrikam", "$412", "64%", "bar-cyan"),
            ("Northwind", "$318", "50%", "bar-green"), ("Tailspin", "$286", "45%", "bar-purple"),
            ("AdventureWorks", "$196", "31%", "bar-orange"), ("Other", "$70", "11%", "bar-gray")
        ],
        Quotas = [
            ("Contoso", "78%", "78%", "bar-red"), ("Fabrikam", "62%", "62%", "bar-orange"),
            ("Northwind", "41%", "41%", "bar-green"), ("Tailspin", "38%", "38%", "bar-green"),
            ("AdventureWorks", "22%", "22%", "bar-green"), ("Other", "18%", "18%", "bar-green")
        ],
        Users = [
            (1, "sam", "Contoso", "8,421,332", "5,221,443", "2,814,221", "385,668", "$336.88", "84%", "quota-red", "user-blue", "2,16 12,13 20,15 29,8 39,11 48,5 57,8 66,2"),
            (2, "alex", "Fabrikam", "4,993,221", "3,104,552", "1,553,219", "335,450", "$201.44", "62%", "quota-orange", "user-blue", "2,15 11,11 19,14 29,7 38,10 47,4 56,7 66,2"),
            (3, "maria", "Northwind", "3,881,002", "2,450,883", "1,101,334", "328,785", "$158.13", "48%", "quota-green", "user-green", "2,16 12,13 21,14 30,7 38,10 48,5 57,7 66,2"),
            (4, "david", "Tailspin", "2,995,441", "1,882,119", "827,221", "286,101", "$121.77", "34%", "quota-green", "user-purple", "2,15 11,12 20,14 29,8 38,10 47,5 56,7 66,2"),
            (5, "chen", "Contoso", "2,114,993", "1,221,004", "664,332", "229,657", "$86.21", "29%", "quota-green", "user-blue", "2,16 12,13 21,14 29,9 38,11 47,5 56,7 66,2")
        ],
        AtRiskRequests = [
            (1, "alex", "gpt-4o", "Token limit exceeded", "14:32 UTC", "Blocked"),
            (2, "sam", "claude-3-5", "Jailbreak attempt detected", "14:28 UTC", "Rejected"),
            (3, "maria", "gpt-4o-mini", "Prompt injection pattern", "14:15 UTC", "Sanitized")
        ],
        Reports = [
            ("Quota and Budget Utilization", "Usage, spend, limits and at-risk entities", "▤"),
            ("Policy Decisions and Outcomes", "Actions taken and their results", "⬟"),
            ("Model Routing and Substitution", "Requested vs selected models and cost impact", "⌘"),
            ("Capacity and Queue Pressure", "Utilization, queue depth and delay/requeue", "☷"),
            ("Latency, Errors and Throttling", "Performance, 429s and error rates", "◷"),
            ("Large-Context Workload", "High token requests and model behavior", "▣"),
            ("Governance, Overrides and Exceptions", "Admin overrides, approvals and compliance", "♟"),
            ("Abuse and Content-Filter Activity", "Jailbreaks, filtered responses and trends", "⚠")
        ]
    };

    public static TokenomicsDashboardSnapshot Empty { get; } = new() {
        IsSampleData = false,
        SnapshotLabel = "Waiting for data...",
        TimePeriods = [],
        Tenants = ["All tenants"],
        UserFilters = ["All users"],
        ModelFilters = ["All models"],
        Metrics = [
            ("Total Tokens", "0", "—", "▤", "gray", "neutral"),
            ("Total Spend (USD)", "$0.00", "—", "$", "gray", "neutral"),
            ("Total Requests", "0", "—", "▣", "gray", "neutral"),
            ("Success Rate", "Unavailable", "—", "✓", "gray", "neutral"),
            ("Policy Actions", "0", "—", "⬟", "gray", "neutral"),
            ("429 Throttles", "0", "—", "!", "gray", "neutral")
        ],
        Trend = [("—", 0, 0, 0)],  // One placeholder data point to prevent rendering errors
        SpendTrend = [("—", 0m)],  // One placeholder spend point
        TokenAxisMaximum = 1,
        TokenAxisTicks = ["1", "0"],
        SpendAxisTicks = [],
        HasPriceData = false,
        HasQuotaData = false,
        TrendDescription = "No data available",
        SpendLine = [],
        TotalTokens = "0",
        ModelDescription = "(no data)",
        ModelGradient = "linear-gradient(to right, #cccccc, #cccccc)",
        Models = [],
        TenantSpend = [],
        Quotas = [],
        Users = [],
        AtRiskRequests = [],
        Reports = [
            ("Quota and Budget Utilization", "Usage, spend, limits and at-risk entities", "▤"),
            ("Policy Decisions and Outcomes", "Actions taken and their results", "⬟"),
            ("Model Routing and Substitution", "Requested vs selected models and cost impact", "⌘"),
            ("Capacity and Queue Pressure", "Utilization, queue depth and delay/requeue", "☷"),
            ("Latency, Errors and Throttling", "Performance, 429s and error rates", "◷"),
            ("Large-Context Workload", "High token requests and model behavior", "▣"),
            ("Governance, Overrides and Exceptions", "Admin overrides, approvals and compliance", "♟"),
            ("Abuse and Content-Filter Activity", "Jailbreaks, filtered responses and trends", "⚠")
        ]
    };
}

/// <summary>Shared server-side state; publishers control the update cadence independently of the UI.</summary>
public sealed class TokenomicsDashboardStore : IDisposable {
    private TokenomicsDashboardSnapshot _snapshot = TokenomicsDashboardSnapshot.Sample;
    private ImmutableDictionary<TokenomicsPeriod, TokenomicsDashboardSnapshot> _periodSnapshots =
        Enum.GetValues<TokenomicsPeriod>().ToImmutableDictionary(period => period, _ => TokenomicsDashboardSnapshot.Sample);
    private readonly object _updateLock = new();
    private readonly Timer _sampleTimer;
    private readonly ILogger<TokenomicsDashboardStore>? _logger;
    private bool _disposed;
    private bool _sampleUpdatesStopped;

    /// <summary>Starts one-second sample updates until real data is published or the store is disposed.</summary>
    public TokenomicsDashboardStore(ILogger<TokenomicsDashboardStore>? logger = null) {
        _logger = logger;
        _sampleTimer = new Timer(RefreshSample, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _sampleTimer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Signals that subscribers can read a new snapshot.</summary>
    public event Action? Changed;

    /// <summary>Returns the latest immutable dashboard snapshot.</summary>
    public TokenomicsDashboardSnapshot GetSnapshot() => Volatile.Read(ref _snapshot);

    /// <summary>Returns the latest immutable snapshot for one rolling dashboard period.</summary>
    public TokenomicsDashboardSnapshot GetSnapshot(TokenomicsPeriod period) {
        var snapshots = Volatile.Read(ref _periodSnapshots);
        if (snapshots.TryGetValue(period, out var snapshot)) {
            return snapshot;
        }
        return GetSnapshot();
    }

    /// <summary>Returns the latest immutable snapshot for a period with optional tenant/user/model filtering.</summary>
    public TokenomicsDashboardSnapshot GetSnapshot(TokenomicsPeriod period, string? tenantFilter = null, string? userFilter = null, string? modelFilter = null) {
        var baseSnapshot = GetSnapshot(period);
        
        // If no filters or all filters are "All", return unfiltered snapshot
        if (string.IsNullOrEmpty(tenantFilter) || tenantFilter == "All tenants") {
            tenantFilter = null;
        }
        if (string.IsNullOrEmpty(userFilter) || userFilter == "All users") {
            userFilter = null;
        }
        if (string.IsNullOrEmpty(modelFilter) || modelFilter == "All models") {
            modelFilter = null;
        }

        if (tenantFilter == null && userFilter == null && modelFilter == null) {
            return baseSnapshot;
        }

        // Apply filters to Users data
        var filteredUsers = baseSnapshot.Users;
        if (tenantFilter != null) {
            filteredUsers = filteredUsers.Where(u => u.Tenant == tenantFilter).ToImmutableArray();
        }
        if (userFilter != null) {
            filteredUsers = filteredUsers.Where(u => u.Name == userFilter).ToImmutableArray();
        }

        // Apply filters to AtRiskRequests data
        var filteredAtRisk = baseSnapshot.AtRiskRequests;
        if (userFilter != null) {
            filteredAtRisk = filteredAtRisk.Where(r => r.User == userFilter).ToImmutableArray();
        }
        if (modelFilter != null) {
            filteredAtRisk = filteredAtRisk.Where(r => r.Model == modelFilter).ToImmutableArray();
        }

        // Apply filters to Models data
        var filteredModels = baseSnapshot.Models;
        if (modelFilter != null) {
            filteredModels = filteredModels.Where(m => m.Name == modelFilter).ToImmutableArray();
        }

        // Create filtered snapshot by replacing the filtered collections
        return baseSnapshot with {
            Users = filteredUsers,
            AtRiskRequests = filteredAtRisk,
            Models = filteredModels
        };
    }

    /// <summary>Atomically replaces dashboard data and notifies subscribed components.</summary>
    public void Update(
        TokenomicsDashboardSnapshot snapshot,
        IReadOnlyDictionary<TokenomicsPeriod, TokenomicsDashboardSnapshot>? periodSnapshots = null) {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!double.IsFinite(snapshot.TokenAxisMaximum) || snapshot.TokenAxisMaximum <= 0) {
            throw new ArgumentOutOfRangeException(nameof(snapshot), "The token axis maximum must be finite and positive.");
        }
        var resolvedPeriodSnapshots = Enum.GetValues<TokenomicsPeriod>().ToImmutableDictionary(
            period => period,
            period => periodSnapshots is not null && periodSnapshots.TryGetValue(period, out var periodSnapshot)
                ? periodSnapshot
                : snapshot);
        foreach (var periodSnapshot in resolvedPeriodSnapshots.Values) {
            ArgumentNullException.ThrowIfNull(periodSnapshot);
            if (!double.IsFinite(periodSnapshot.TokenAxisMaximum) || periodSnapshot.TokenAxisMaximum <= 0) {
                throw new ArgumentOutOfRangeException(nameof(periodSnapshots), "Every period token axis maximum must be finite and positive.");
            }
        }
        lock (_updateLock) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!snapshot.IsSampleData) {
                _sampleUpdatesStopped = true;
                _sampleTimer.Dispose();
            }
            Interlocked.Exchange(ref _snapshot, snapshot);
            Interlocked.Exchange(ref _periodSnapshots, resolvedPeriodSnapshots);
            Changed?.Invoke();
        }
    }

    private void RefreshSample(object? state) {
        try {
            lock (_updateLock) {
                if (_disposed || _sampleUpdatesStopped) return;
                Update(CreateSampleSnapshot());
            }
        }
        catch (Exception exception) {
            _logger?.LogError(exception, "Failed to refresh the Tokenomics sample dashboard.");
        }
    }

    private static TokenomicsDashboardSnapshot CreateSampleSnapshot() {
        var sample = TokenomicsDashboardSnapshot.Sample;
        var culture = CultureInfo.InvariantCulture;
        
        // Calculate spend from original sample data (stable axis)
        var originalDailySpend = sample.Trend.Select(point => (point.InputNet + point.Cached + point.Output) * 40).ToImmutableArray();
        
        // Randomize tokens for display variation
        var trend = sample.Trend.Select(point => (
            point.Date,
            InputNet: point.InputNet * (0.8 + Random.Shared.NextDouble() * 0.4),
            Cached: point.Cached * (0.8 + Random.Shared.NextDouble() * 0.4),
            Output: point.Output * (0.8 + Random.Shared.NextDouble() * 0.4))).ToImmutableArray();
        var total = trend.Sum(point => point.InputNet + point.Cached + point.Output);
        var totalTokens = total.ToString("F1", culture) + "M";
        var spend = originalDailySpend.Sum();
        var metrics = sample.Metrics.Select((metric, index) => {
            var value = index switch {
                0 => totalTokens,
                1 => spend.ToString("$#,##0", culture),
                2 => Random.Shared.Next(45000, 60000).ToString("N0", culture),
                3 => (97 + Random.Shared.NextDouble() * 2.9).ToString("F1", culture) + "%",
                4 => Random.Shared.Next(1000, 1500).ToString("N0", culture),
                _ => Random.Shared.Next(100, 300).ToString("N0", culture)
            };
            return (metric.Name, Value: value, Change: $"↑ {Random.Shared.Next(1, 35)}%",
                metric.Icon, metric.Tone, metric.ChangeTone);
        }).ToImmutableArray();

        var weights = sample.Models.Select(model =>
            double.Parse(model.Share.TrimEnd('%'), culture) * (0.8 + Random.Shared.NextDouble() * 0.4)).ToArray();
        var weightTotal = weights.Sum();
        var shares = weights.Select(weight => weight / weightTotal * 100).ToArray();
        var models = sample.Models.Select((model, index) => (
            model.Name,
            Tokens: (total * shares[index] / 100).ToString("F1", culture) + "M",
            Share: shares[index].ToString("F1", culture) + "%",
            model.Color)).ToImmutableArray();
        string[] colors = ["#287cf0", "#31c1df", "#48c58a", "#9652ef", "#ffa21b", "#9ba9c1"];
        var start = 0.0;
        var gradientStops = shares.Select((share, index) => {
            var end = index == shares.Length - 1 ? 100 : start + share;
            var stop = FormattableString.Invariant($"{colors[index]} {start:F2}% {end:F2}%");
            start = end;
            return stop;
        }).ToArray();

        var tenantWeights = sample.TenantSpend.Select(tenant =>
            double.Parse(tenant.Amount.TrimStart('$'), culture) * (0.8 + Random.Shared.NextDouble() * 0.4)).ToArray();
        var tenantWeightTotal = tenantWeights.Sum();
        var tenantWeightMaximum = tenantWeights.Max();
        var tenantSpend = sample.TenantSpend.Select((tenant, index) => (
            tenant.Name,
            Amount: (spend * tenantWeights[index] / tenantWeightTotal).ToString("$#,##0", culture),
            Width: (tenantWeights[index] / tenantWeightMaximum * 100).ToString("F1", culture) + "%",
            tenant.Color)).ToImmutableArray();
        var quotas_placeholder = sample.Quotas;
        var users = sample.Users.Select(user => {
            var factor = 0.8 + Random.Shared.NextDouble() * 0.4;
            var input = (long)(long.Parse(user.Input, NumberStyles.AllowThousands, culture) * factor);
            var output = (long)(long.Parse(user.Output, NumberStyles.AllowThousands, culture) * factor);
            var cached = (long)(long.Parse(user.Cached, NumberStyles.AllowThousands, culture) * factor);
            var userTotal = input + output + cached;
            var quotaLimitPerUser = 100L;
            var quotaPercent = (userTotal * 100L) / quotaLimitPerUser;
            var quotaPercent_Capped = Math.Min(quotaPercent, 200L);
            var sparkline = string.Join(" ", Enumerable.Range(0, 8).Select(index =>
                $"{2 + index * 9},{Math.Max(1, 17 - index * 2 + Random.Shared.Next(-2, 3))}"));
            return (user.Rank, user.Name, user.Tenant,
                Total: userTotal.ToString("N0", culture), Input: input.ToString("N0", culture),
                Output: output.ToString("N0", culture), Cached: cached.ToString("N0", culture),
                Spend: (userTotal / 1_000_000.0 * 40).ToString("$0.00", culture),
                Quota: $"{quotaPercent_Capped}%", QuotaColor: quotaPercent_Capped >= 75 ? "quota-red" : quotaPercent_Capped >= 50 ? "quota-orange" : "quota-green",
                user.Color, Sparkline: sparkline);
        }).OrderByDescending(user => long.Parse(user.Total, NumberStyles.AllowThousands, culture))
            .Select((user, index) => user with { Rank = index + 1 }).ToImmutableArray();

        var quotas = users
            .GroupBy(u => u.Tenant, StringComparer.Ordinal)
            .Select(group => {
                var totalTokensForTenant = group.Sum(u => long.Parse(u.Total, NumberStyles.AllowThousands, culture));
                var quotaLimitPerTenant = 100L;
                var quotaPercent = (totalTokensForTenant * 100L) / quotaLimitPerTenant;
                var quotaPercent_Capped = Math.Min(quotaPercent, 200L);
                return (group.Key, Amount: $"{quotaPercent_Capped}%", Width: $"{Math.Min(100, quotaPercent_Capped)}%",
                    Color: quotaPercent_Capped >= 75 ? "bar-red" : quotaPercent_Capped >= 50 ? "bar-orange" : "bar-green");
            })
            .OrderByDescending(q => long.Parse(q.Amount.TrimEnd('%'), culture))
            .ToImmutableArray();

        return sample with {
            SnapshotLabel = $"Sample snapshot · {DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", culture)}",
            Metrics = metrics,
            Trend = trend,
            SpendTrend = trend.Select((point, index) => 
                (point.Date, Spend: (decimal)originalDailySpend[index])).ToImmutableArray(),
            TrendDescription = "Simulated daily input, output, cached tokens and spend; refreshed every second",
            SpendLine = sample.SpendLine.Select((point, index) =>
                (point.X, Y: (int)Math.Round(160 * (1 - originalDailySpend[index] / 400)))).ToImmutableArray(),
            TotalTokens = totalTokens,
            Models = models,
            ModelDescription = string.Join(", ", models.Select(model => $"{model.Name} {model.Share}")),
            ModelGradient = $"conic-gradient({string.Join(", ", gradientStops)})",
            TenantSpend = tenantSpend,
            Quotas = quotas,
            Users = users
        };
    }

    /// <summary>Stops sample updates when the singleton store is released by the host.</summary>
    public void Dispose() {
        lock (_updateLock) {
            if (_disposed) return;
            _disposed = true;
            _sampleTimer.Dispose();
        }
    }
}
