using System.Globalization;
using System.Collections.Immutable;
using CompanionApp.Components.Shared;
using static CompanionApp.Observability.ObservabilityRequestProjection;

namespace CompanionApp.Observability;

/// <summary>Cached usage and chart data for an Insights query; UI selection and popup state stay in the page.</summary>
public sealed class InsightsProjection
{
    private readonly ObservabilityRequestProjection _requests;
    private readonly string _selectedUserShareMetricKey;
    private readonly HashSet<string> _selectedTrendMetricKeys;
    private readonly DateTimeOffset _publishedUtc;
    private readonly IReadOnlyDictionary<MultiRequestStatusItem, DateTimeOffset?> _requestTimestamps;
    private readonly IReadOnlyDictionary<MultiRequestStatusItem, UsageTotals> _requestUsage;
    private MonitorSnapshot _snapshot => _requests.AnalyticsSnapshot;
    private const bool IsConnected = true;

    internal InsightsProjection(ObservabilityRequestProjection requests, DateTimeOffset publishedUtc, string shareMetric, IEnumerable<string> selectedMetrics)
    {
        _requests = requests;
        _publishedUtc = requests.Snapshot.TimestampUtc != default ? requests.Snapshot.TimestampUtc : publishedUtc;
        _selectedUserShareMetricKey = shareMetric;
        _selectedTrendMetricKeys = new(selectedMetrics, StringComparer.OrdinalIgnoreCase);
        _requestTimestamps = requests.AnalyticsRequests.ToDictionary(item => item, ResolveRequestTimestampUtc);
        _requestUsage = requests.AnalyticsRequests.ToDictionary(item => item, ReadObservedUsage);
        TrendMetricOptions = BuildUsageMetricDefinitions();
        UserShares = BuildUserShares();
        UserColors = BuildUserColors();
        UsageTrendPoints = BuildUsageTrendPoints();
        UsageTrendSlots = BuildUsageTrendSlots();
        UserTrendLines = BuildUserTrendSeries();
        SelectedTrendMetrics = BuildSelectedTrendMetrics();
        TrendWindowLabel = BuildTrendWindowLabel();
        _slotRequests = UsageTrendSlots.ToDictionary(slot => slot,
            slot => (IReadOnlyList<ObservedRequestSnapshot>)BuildTrendSlotRequests(slot)
                .Select(requests.GetSourceRequest).ToImmutableArray());
    }

    private readonly IReadOnlyDictionary<TrendSlot, IReadOnlyList<ObservedRequestSnapshot>> _slotRequests;
    public IReadOnlyList<UserShareRow> UserShares { get; }
    public IReadOnlyDictionary<string, string> UserColors { get; }
    public IReadOnlyList<UserTrendSeries> UserTrendLines { get; }
    public IReadOnlyList<UsageTrendPoint> UsageTrendPoints { get; }
    public IReadOnlyList<TrendSlot> UsageTrendSlots { get; }
    public IReadOnlyList<(string Key, string Label, string Color, bool IsSynthetic)> TrendMetricOptions { get; }
    public IReadOnlyList<TrendMetricSeries> SelectedTrendMetrics { get; }
    public string TrendWindowLabel { get; }
    public IReadOnlyList<ObservedRequestSnapshot> RequestsForSlot(TrendSlot slot) => _slotRequests.TryGetValue(slot, out var requests) ? requests : Array.Empty<ObservedRequestSnapshot>();
    private IReadOnlyList<MultiRequestStatusItem> GetFilteredRequestItems() => _requests.AnalyticsRequests;
    private UsageTotals ParseUsageFromFinalResponse(MultiRequestStatusItem item) => _requestUsage[item];

    private static UsageTotals ReadObservedUsage(MultiRequestStatusItem item)
    {
        var metrics = item.ObservedUsageMetrics ?? ObservedUsageParser.Parse(item);
        return new UsageTotals(metrics, metrics.Count > 0);
    }

    private IReadOnlyList<UserShareRow> BuildUserShares()
    {
        var filteredRequests = GetFilteredRequestItems();
        if (!IsConnected || !_snapshot.HasData || filteredRequests.Count == 0)
        {
            return Array.Empty<UserShareRow>();
        }

        var counts = new Dictionary<string, UserUsageAggregate>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in filteredRequests)
        {
            if (!string.Equals(item.EventType, "S7P-ProxyRequest", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(item.EventType, "S7P-ProxyRequestExpired", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var user = string.IsNullOrWhiteSpace(item.UserId)
                ? ExtractHeaderValue(item.RequestHeadersText, "UserID")
                : item.UserId;
            if (string.IsNullOrWhiteSpace(user))
            {
                user = "(unknown)";
            }

            if (!counts.TryGetValue(user, out var aggregate))
            {
                aggregate = new UserUsageAggregate();
                counts[user] = aggregate;
            }

            aggregate.Calls++;

            var usage = ParseUsageFromFinalResponse(item);
            if (usage.HasUsage)
            {
                foreach (var metric in usage.Metrics)
                {
                    aggregate.UsageMetrics[metric.Key] = aggregate.UsageMetrics.TryGetValue(metric.Key, out var currentValue)
                        ? currentValue + metric.Value
                        : metric.Value;
                }

                aggregate.UsageSamples++;
            }
        }

        var totalShareValue = counts.Values.Sum(GetUserShareMetricValue);
        if (totalShareValue <= 0)
        {
            return Array.Empty<UserShareRow>();
        }

        return counts
            .OrderByDescending(pair => GetUserShareMetricValue(pair.Value))
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .Select(pair => new UserShareRow(
                pair.Key,
                pair.Value.Calls,
                (GetUserShareMetricValue(pair.Value) * 100.0) / totalShareValue,
                new Dictionary<string, long>(pair.Value.UsageMetrics, StringComparer.OrdinalIgnoreCase),
                pair.Value.UsageSamples > 0))
            .ToArray();
    }

    private long GetUserShareMetricValue(UserUsageAggregate aggregate)
    {
        if (string.Equals(_selectedUserShareMetricKey, "__calls", StringComparison.OrdinalIgnoreCase))
        {
            return aggregate.Calls;
        }

        return aggregate.UsageMetrics.TryGetValue(_selectedUserShareMetricKey, out var value) ? value : 0;
    }

    private IReadOnlyList<(string Key, string Label, string Color, bool IsSynthetic)> BuildUsageMetricDefinitions()
    {
        var filteredRequests = GetFilteredRequestItems();
        var discoveredKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in filteredRequests)
        {
            if (!string.Equals(item.EventType, "S7P-ProxyRequest", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(item.EventType, "S7P-ProxyRequestExpired", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var usage = ParseUsageFromFinalResponse(item);
            foreach (var metric in usage.Metrics.Keys)
            {
                discoveredKeys.Add(metric);
            }
        }

        var palette = new[]
        {
            "#2563eb", "#0f766e", "#dc2626", "#ca8a04", "#7c3aed", "#0891b2",
            "#16a34a", "#ea580c", "#db2777", "#4f46e5", "#1d4ed8", "#9333ea"
        };

        var metrics = new List<(string Key, string Label, string Color, bool IsSynthetic)>
        {
            ("__total_queries", "Total queries", palette[0], true),
        };

        var orderedDiscoveredKeys = discoveredKeys
            .OrderBy(key => key.Equals("usage.total_tokens", StringComparison.OrdinalIgnoreCase) ? 0 :
                            key.Equals("usage.prompt_tokens", StringComparison.OrdinalIgnoreCase) ? 1 :
                            key.Equals("usage.completion_tokens", StringComparison.OrdinalIgnoreCase) ? 2 :
                            key.Equals("usage.input_tokens", StringComparison.OrdinalIgnoreCase) ? 3 :
                            key.Equals("usage.output_tokens", StringComparison.OrdinalIgnoreCase) ? 4 : 10)
            .ThenBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        for (var i = 0; i < orderedDiscoveredKeys.Length; i++)
        {
            var key = orderedDiscoveredKeys[i];
            var label = BuildUsageMetricLabel(key);
            var color = palette[(i + 1) % palette.Length];
            metrics.Add((key, label, color, false));
        }

        return metrics;
    }

    private IReadOnlyDictionary<string, string> BuildUserColors()
    {
        var palette = new[]
        {
            "#2563eb", "#dc2626", "#0891b2", "#16a34a", "#d97706", "#7c3aed",
            "#db2777", "#0f766e", "#ea580c", "#4f46e5", "#1d4ed8", "#9333ea"
        };

        var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var userShares = UserShares;
        for (var i = 0; i < userShares.Count; i++)
        {
            colors[userShares[i].User] = palette[i % palette.Length];
        }

        return colors;
    }

    private string GetUserColor(string user)
    {
        if (UserColors.TryGetValue(user, out var color))
        {
            return color;
        }

        return "#64748b";
    }

    private IReadOnlyList<TrendMetricSeries> BuildSelectedTrendMetrics()
    {
        if (UsageTrendSlots.Count == 0)
        {
            return Array.Empty<TrendMetricSeries>();
        }

        var selectedSeries = new List<TrendMetricSeries>();
        foreach (var metric in TrendMetricOptions)
        {
            if (!_selectedTrendMetricKeys.Contains(metric.Key))
            {
                continue;
            }

            if (metric.IsSynthetic)
            {
                var values = UsageTrendSlots.Select(slot => (long)slot.Calls).ToArray();
                var segments = UsageTrendSlots
                    .Select(slot => slot.UserCalls
                        .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(pair => (pair.Key, GetUserColor(pair.Key), (long)pair.Value))
                        .ToArray())
                    .ToArray();
                selectedSeries.Add(new TrendMetricSeries(metric.Key, metric.Label, metric.Color, values, segments));

                continue;
            }

            var metricValues = UsageTrendSlots
                .Select(slot => slot.UsageMetrics.TryGetValue(metric.Key, out var value) ? value : 0L)
                .ToArray();
            var metricSegments = metricValues
                .Select(value => (IReadOnlyList<(string Label, string Color, long Value)>)new[] { (metric.Label, metric.Color, value) })
                .ToArray();
            selectedSeries.Add(new TrendMetricSeries(metric.Key, metric.Label, metric.Color, metricValues, metricSegments));
        }

        return selectedSeries;
    }

    private IReadOnlyList<UserTrendSeries> BuildUserTrendSeries()
    {
        var filteredRequests = GetFilteredRequestItems();
        if (!IsConnected || !_snapshot.HasData || filteredRequests.Count == 0)
        {
            return Array.Empty<UserTrendSeries>();
        }

        var usersToShow = UserShares
            .Take(8)
            .Select(share => share.User)
            .ToArray();
        if (usersToShow.Length == 0)
        {
            return Array.Empty<UserTrendSeries>();
        }

        var bucketSize = TimeSpan.FromSeconds(5);
        var totalWindow = TimeSpan.FromHours(1);
        var endTicks = _publishedUtc.UtcTicks - (_publishedUtc.UtcTicks % bucketSize.Ticks);
        var endBucket = new DateTimeOffset(endTicks, TimeSpan.Zero);
        var bucketCount = (int)(totalWindow.Ticks / bucketSize.Ticks) + 1;
        var startBucket = endBucket - TimeSpan.FromTicks(bucketSize.Ticks * (bucketCount - 1));

        var userSet = new HashSet<string>(usersToShow, StringComparer.OrdinalIgnoreCase);
        var userSeriesValues = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var user in usersToShow)
        {
            userSeriesValues[user] = new int[bucketCount];
        }

        foreach (var item in filteredRequests)
        {
            if (!string.Equals(item.EventType, "S7P-ProxyRequest", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(item.EventType, "S7P-ProxyRequestExpired", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var user = string.IsNullOrWhiteSpace(item.UserId)
                ? ExtractHeaderValue(item.RequestHeadersText, "UserID")
                : item.UserId;
            if (string.IsNullOrWhiteSpace(user))
            {
                user = "(unknown)";
            }

            if (!userSet.Contains(user))
            {
                continue;
            }

            var timestamp = _requestTimestamps[item];
            if (timestamp is null)
            {
                continue;
            }

            var roundedTicks = timestamp.Value.UtcTicks - (timestamp.Value.UtcTicks % bucketSize.Ticks);
            var bucket = new DateTimeOffset(roundedTicks, TimeSpan.Zero);
            if (bucket < startBucket || bucket > endBucket)
            {
                continue;
            }

            var bucketOffset = bucket - startBucket;
            var bucketIndex = (int)(bucketOffset.Ticks / bucketSize.Ticks);
            if (bucketIndex < 0 || bucketIndex >= bucketCount)
            {
                continue;
            }

            userSeriesValues[user][bucketIndex]++;
        }

        var maxCalls = 1;
        foreach (var user in usersToShow)
        {
            var values = userSeriesValues[user];
            for (var i = 0; i < values.Length; i++)
            {
                if (values[i] > maxCalls)
                {
                    maxCalls = values[i];
                }
            }
        }

        return usersToShow
            .Select(user => new UserTrendSeries(
                user,
                GetUserColor(user),
                BuildTrendPolyline(userSeriesValues[user].Select(value => (double)value), maxCalls),
                userSeriesValues[user].Length == 0 ? 0 : userSeriesValues[user][^1],
                maxCalls,
                userSeriesValues[user]))
            .ToArray();
    }

    private IReadOnlyList<UsageTrendPoint> BuildUsageTrendPoints()
    {
        var filteredRequests = GetFilteredRequestItems();
        if (!IsConnected || !_snapshot.HasData || filteredRequests.Count == 0)
        {
            return Array.Empty<UsageTrendPoint>();
        }

        var bucketSize = TimeSpan.FromMinutes(1);
        var totalWindow = TimeSpan.FromHours(1);
        var endTicks = _publishedUtc.UtcTicks - (_publishedUtc.UtcTicks % bucketSize.Ticks);
        var endBucket = new DateTimeOffset(endTicks, TimeSpan.Zero);
        var bucketCount = (int)(totalWindow.Ticks / bucketSize.Ticks) + 1;
        var startBucket = endBucket - TimeSpan.FromTicks(bucketSize.Ticks * (bucketCount - 1));
        var buckets = new UsageTrendPointAccumulator[bucketCount];

        for (var i = 0; i < bucketCount; i++)
        {
            buckets[i] = new UsageTrendPointAccumulator();
        }

        foreach (var item in filteredRequests)
        {
            if (!string.Equals(item.EventType, "S7P-ProxyRequest", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(item.EventType, "S7P-ProxyRequestExpired", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var timestamp = _requestTimestamps[item];
            if (timestamp is null)
            {
                continue;
            }

            var roundedTicks = timestamp.Value.UtcTicks - (timestamp.Value.UtcTicks % bucketSize.Ticks);
            var bucket = new DateTimeOffset(roundedTicks, TimeSpan.Zero);
            if (bucket < startBucket || bucket > endBucket)
            {
                continue;
            }

            var bucketOffset = bucket - startBucket;
            var bucketIndex = (int)(bucketOffset.Ticks / bucketSize.Ticks);
            if (bucketIndex < 0 || bucketIndex >= bucketCount)
            {
                continue;
            }

            var accumulator = buckets[bucketIndex];

            accumulator.Calls++;
            var user = string.IsNullOrWhiteSpace(item.UserId)
                ? ExtractHeaderValue(item.RequestHeadersText, "UserID")
                : item.UserId;
            user = string.IsNullOrWhiteSpace(user) ? "(unknown)" : user;
            accumulator.UserCalls[user] = accumulator.UserCalls.TryGetValue(user, out var userCalls)
                ? userCalls + 1
                : 1;

            var usage = ParseUsageFromFinalResponse(item);
            if (usage.HasUsage)
            {
                foreach (var metric in usage.Metrics)
                {
                    accumulator.UsageMetrics[metric.Key] = accumulator.UsageMetrics.TryGetValue(metric.Key, out var currentValue)
                        ? currentValue + metric.Value
                        : metric.Value;
                }
            }
        }

        var trendPoints = Enumerable.Range(0, bucketCount)
            .Select(index =>
            {
                var timestamp = startBucket + TimeSpan.FromTicks(bucketSize.Ticks * index);
                var bucket = buckets[index];
                return new UsageTrendPoint(
                    timestamp,
                    bucket.Calls,
                    new Dictionary<string, long>(bucket.UsageMetrics, StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, int>(bucket.UserCalls, StringComparer.OrdinalIgnoreCase));
            })
            .ToArray();

        var oldestActivityIndex = Array.FindIndex(trendPoints, point => point.Calls > 0);
        var activityAgeInMinutes = oldestActivityIndex < 0
            ? 0
            : trendPoints.Length - 1 - oldestActivityIndex;
        var displayedWindow = activityAgeInMinutes <= 1
            ? TimeSpan.FromMinutes(1)
            : activityAgeInMinutes <= 5
                ? TimeSpan.FromMinutes(5)
                : activityAgeInMinutes <= 10
                    ? TimeSpan.FromMinutes(10)
                    : activityAgeInMinutes <= 15
                        ? TimeSpan.FromMinutes(15)
                        : activityAgeInMinutes <= 30
                            ? TimeSpan.FromMinutes(30)
                            : TimeSpan.FromHours(1);
        var displayedBucketCount = (int)(displayedWindow.Ticks / bucketSize.Ticks) + 1;
        return trendPoints[^displayedBucketCount..];
    }

    private string BuildTrendWindowLabel()
    {
        if (UsageTrendPoints.Count == 0)
        {
            return string.Empty;
        }

        return UsageTrendPoints.Count switch
        {
            <= 2 => "Window: Last minute data",
            <= 6 => "Window: Last 5 mins data",
            <= 11 => "Window: Last 10 mins data",
            <= 16 => "Window: Last 15 mins data",
            <= 31 => "Window: Last 30 mins data",
            _ => "Window: Last 1 hour data",
        };
    }

    private IReadOnlyList<TrendSlot> BuildUsageTrendSlots()
    {
        var points = UsageTrendPoints;
        if (points.Count == 0)
        {
            return Array.Empty<TrendSlot>();
        }

        const int maxSlotCount = 10;
        var slotCount = Math.Min(maxSlotCount, points.Count);
        var slots = new List<TrendSlot>(slotCount);
        for (var slotIndex = 0; slotIndex < slotCount; slotIndex++)
        {
            var startIndex = (int)Math.Floor((double)(slotIndex * points.Count) / slotCount);
            var endExclusive = (int)Math.Floor((double)((slotIndex + 1) * points.Count) / slotCount);
            if (endExclusive <= startIndex)
            {
                endExclusive = startIndex + 1;
            }

            var slotPoints = points.Skip(startIndex).Take(endExclusive - startIndex).ToArray();
            var usage = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var userCalls = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var point in slotPoints)
            {
                foreach (var metric in point.UsageMetrics)
                {
                    usage[metric.Key] = usage.TryGetValue(metric.Key, out var currentValue)
                        ? currentValue + metric.Value
                        : metric.Value;
                }

                foreach (var user in point.UserCalls)
                {
                    userCalls[user.Key] = userCalls.TryGetValue(user.Key, out var currentValue)
                        ? currentValue + user.Value
                        : user.Value;
                }
            }

            slots.Add(new TrendSlot(
                slotPoints[0].TimestampUtc,
                slotPoints[^1].TimestampUtc.AddMinutes(1),
                slotPoints.Sum(point => point.Calls),
                usage,
                userCalls));
        }

        return slots;
    }

    private IReadOnlyList<MultiRequestStatusItem> BuildTrendSlotRequests(TrendSlot slot)
    {
        return GetFilteredRequestItems()
            .Where(item => string.Equals(item.EventType, "S7P-ProxyRequest", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.EventType, "S7P-ProxyRequestExpired", StringComparison.OrdinalIgnoreCase))
            .Where(item => _requestTimestamps[item] is { } timestamp
                && timestamp >= slot.StartUtc && timestamp < slot.EndExclusiveUtc)
            .OrderBy(item => item.RequestNumber)
            .ToArray();
    }

    public static string BuildTrendPolyline(IEnumerable<double> values, double? maxScale = null)
    {
        var sequence = values.ToArray();
        if (sequence.Length == 0)
        {
            return string.Empty;
        }

        var width = 1000d;
        var height = 300d;
        var leftPadding = 56d;
        var rightPadding = 24d;
        var topPadding = 16d;
        var bottomPadding = 44d;
        var drawableWidth = width - leftPadding - rightPadding;
        var drawableHeight = height - topPadding - bottomPadding;
        var maxValue = Math.Max(1d, maxScale ?? sequence.Max());

        var points = new List<string>(sequence.Length);
        for (var i = 0; i < sequence.Length; i++)
        {
            var x = sequence.Length == 1
                ? leftPadding + (drawableWidth / 2d)
                : leftPadding + (i * drawableWidth / (sequence.Length - 1));
            var y = topPadding + ((1d - (sequence[i] / maxValue)) * drawableHeight);
            points.Add($"{x.ToString("0.##", CultureInfo.InvariantCulture)},{y.ToString("0.##", CultureInfo.InvariantCulture)}");
        }

        return string.Join(" ", points);
    }

    private static DateTimeOffset? ResolveRequestTimestampUtc(MultiRequestStatusItem item)
    {
        if (item.FinalizedAtUtc is { } finalized)
        {
            return finalized.ToUniversalTime();
        }

        if (item.EnqueuedAtUtc is { } enqueued)
        {
            return enqueued.ToUniversalTime();
        }

        var dateText = ExtractHeaderValue(item.ResponseHeadersText, "Date")
            ?? ExtractHeaderValue(item.ResponseHeadersText, "Timestamp")
            ?? ExtractHeaderValue(item.ResponseHeadersText, "Request-Date")
            ?? ExtractHeaderValue(item.RequestHeadersText, "Date")
            ?? ExtractHeaderValue(item.RequestHeadersText, "Timestamp")
            ?? ExtractHeaderValue(item.RequestHeadersText, "Request-Date");

        if (DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        return null;
    }

    public sealed record UserShareRow(
        string User,
        int Calls,
        double Percent,
        IReadOnlyDictionary<string, long> UsageMetrics,
        bool HasUsage);
    public sealed record TrendMetricSeries(
        string Key,
        string Label,
        string Color,
        IReadOnlyList<long> Values,
        IReadOnlyList<IReadOnlyList<(string Label, string Color, long Value)>> Segments);
    public sealed record UserTrendSeries(string User, string Color, string Points, int LatestCalls, int MaxCalls, IReadOnlyList<int> Values);
    public sealed record UsageTrendPoint(
        DateTimeOffset TimestampUtc,
        int Calls,
        IReadOnlyDictionary<string, long> UsageMetrics,
        IReadOnlyDictionary<string, int> UserCalls);

    public sealed record TrendSlot(
        DateTimeOffset StartUtc,
        DateTimeOffset EndExclusiveUtc,
        int Calls,
        IReadOnlyDictionary<string, long> UsageMetrics,
        IReadOnlyDictionary<string, int> UserCalls);
    public sealed record UsageTotals(IReadOnlyDictionary<string, long> Metrics, bool HasUsage);

    private sealed class UserUsageAggregate
    {
        public int Calls { get; set; }
        public Dictionary<string, long> UsageMetrics { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int UsageSamples { get; set; }
    }

    private sealed class UsageTrendPointAccumulator
    {
        public int Calls { get; set; }
        public Dictionary<string, long> UsageMetrics { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> UserCalls { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildUsageMetricLabel(string key)
    {
        if (string.Equals(key, "__total_queries", StringComparison.OrdinalIgnoreCase))
        {
            return "Total queries";
        }

        var trimmed = key.StartsWith("usage.", StringComparison.OrdinalIgnoreCase)
            ? key["usage.".Length..]
            : key;
        var segments = trimmed
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(segment => segment.Replace("_", " ", StringComparison.Ordinal))
            .Select(segment => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(segment))
            .ToArray();

        return segments.Length == 0 ? key : string.Join(" / ", segments);
    }

}
