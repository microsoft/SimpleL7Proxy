using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using CompanionApp.Components.Shared.EventHub;
using SimpleL7Proxy.Tokenomics;

namespace CompanionApp.Components.Shared;

/// <summary>Validated local replay input and arguments removed from ASP.NET configuration.</summary>
public sealed record TokenomicsReplayOptions(string[] ApplicationArgs, string? FileName, ImmutableArray<ImmutableDictionary<string, string>> Events) {
    /// <summary>Parses command-line arguments and validates any replay file.</summary>
    public static TokenomicsReplayOptions Parse(string[] args) => global::Program.ParseCommandLineArguments(args);

    /// <summary>Validates the entire NDJSON file before any replay or application startup.</summary>
    internal static TokenomicsReplayOptions Load(string[] applicationArgs, string? fileName) {
        if (fileName is null) return new(applicationArgs, null, []);

        var events = ImmutableArray.CreateBuilder<ImmutableDictionary<string, string>>();
        try {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(fileName)) {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                try {
                    using var document = JsonDocument.Parse(line);
                    events.Add(ParseRecord(document.RootElement));
                }
                catch (Exception exception) when (exception is JsonException or FormatException) {
                    throw new FormatException($"Replay file '{fileName}', line {lineNumber}: {exception.Message}", exception);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            throw new ArgumentException($"Cannot read replay file '{fileName}': {exception.Message}", exception);
        }
        return new(applicationArgs.ToArray(), fileName, events.ToImmutable());
    }

    internal static ImmutableDictionary<string, string> ParseRecord(JsonElement record) {
        if (record.ValueKind != JsonValueKind.Object)
            throw new FormatException("Each event record must be a JSON object.");
        var fields = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var property in record.EnumerateObject()) {
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                throw new FormatException($"Field '{property.Name}' must be a scalar value.");
            if (!fields.TryAdd(property.Name, property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()! : property.Value.ToString()))
                throw new FormatException($"Duplicate field '{property.Name}'.");
        }
        if (!fields.TryGetValue("Type", out var type) || string.IsNullOrWhiteSpace(type))
            throw new FormatException("Field 'Type' is required.");
        if (type == "S7P-Tokenomics" && (fields.ContainsKey("RecordKind") || fields.ContainsKey("SchemaVersion")))
            ValidateSummary(fields);
        return fields.ToImmutable();
    }

    internal static void ValidateSummary(IReadOnlyDictionary<string, string> fields) {
        if (!fields.TryGetValue("MID", out var mid) || string.IsNullOrWhiteSpace(mid))
            throw new FormatException("Tokenomics field 'MID' is required.");
        if (!fields.TryGetValue("RecordKind", out var kind) || kind is not ("RequestOutcome" or "PolicyDecision"))
            throw new FormatException("Tokenomics RecordKind must be RequestOutcome or PolicyDecision.");
        if (fields.TryGetValue("TimestampUtc", out var timestamp)
            && !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
            throw new FormatException("TimestampUtc is not a valid timestamp.");
        if (kind == "RequestOutcome") {
            var status = GetStatus(fields);
            if (status is < 100 or > 599) throw new FormatException("RequestOutcome requires a numeric StatusCode or Status (100–599).");
            foreach (var key in new[] { "InputTokens", "OutputTokens", "CachedTokens" }) {
                if (fields.TryGetValue(key, out var value)
                    && (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var tokens) || tokens < 0))
                    throw new FormatException($"{key} must be a nonnegative integer.");
            }
            if (GetTokens(fields, "CachedTokens") > GetTokens(fields, "InputTokens"))
                throw new FormatException("CachedTokens must not exceed InputTokens.");
        }
        else if (!fields.TryGetValue("PolicyAction", out var action) || string.IsNullOrWhiteSpace(action))
            throw new FormatException("PolicyDecision requires PolicyAction.");
    }

    internal static int GetStatus(IReadOnlyDictionary<string, string> fields) =>
        int.TryParse(fields.GetValueOrDefault("StatusCode"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)
            ? code : int.TryParse(fields.GetValueOrDefault("Status"), NumberStyles.Integer, CultureInfo.InvariantCulture, out code) ? code : 0;

    internal static long GetTokens(IReadOnlyDictionary<string, string> fields, string key) =>
        long.TryParse(fields.GetValueOrDefault(key), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
}

/// <summary>Aggregates live Tokenomics summaries or replays ten local events per second.</summary>
public sealed class TokenomicsEventReplay : BackgroundService {
    private readonly TokenomicsReplayOptions _options;
    private readonly TokenomicsDashboardStore _store;
    private readonly ILogger<TokenomicsEventReplay> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly AppConfigurationScaffoldService? _appConfiguration;
    private readonly List<ImmutableDictionary<string, string>> _received = [];
    private int _position;
    private readonly object _ingestionLock = new();
    private const int LiveRecordLimit = 10_000;
    private bool _liveDirty;
    private int _ticksSinceWindowRefresh;
    private string? _lastInvalidPricingValue;
    private string? _lastPricingContext;

    /// <summary>Disables synthetic data before file replay; live reading initializes it when the reader starts.</summary>
    public TokenomicsEventReplay(TokenomicsReplayOptions options, TokenomicsDashboardStore store,
        ILogger<TokenomicsEventReplay> logger, TimeProvider? timeProvider = null,
        AppConfigurationScaffoldService? appConfiguration = null) {
        _options = options;
        _store = store;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _appConfiguration = appConfiguration;
        if (_options.FileName is not null) UpdateStore();
    }

    /// <summary>Replaces sample data with an empty live snapshot before Event Hub reading or local import.</summary>
    public void BeginLive() {
        if (_options.FileName is not null) return;
        lock (_ingestionLock) {
            UpdateStore();
        }
    }

    /// <summary>Buffers validated live summaries from all partitions for the next one-second publication.</summary>
    public void PublishRecords(IReadOnlyList<ParsedEventRecord> records) {
        if (_options.FileName is not null) return;
        lock (_ingestionLock) {
            var added = false;
            foreach (var record in records) {
                var fields = record.Data;
                if (fields.GetValueOrDefault("Type") != "S7P-Tokenomics"
                    || (!fields.ContainsKey("RecordKind") && !fields.ContainsKey("SchemaVersion"))) continue;
                try {
                    var summary = fields.ToImmutableDictionary(StringComparer.Ordinal);
                    if (summary.GetValueOrDefault("Type") != "S7P-Tokenomics") continue;
                    TokenomicsReplayOptions.ValidateSummary(summary);
                    _received.Add(summary);
                    added = true;
                }
                catch (FormatException exception) {
                    _logger.LogWarning(exception, "Ignoring invalid Tokenomics summary.");
                }
            }
            if (!added) return;
            if (_received.Count > LiveRecordLimit)
                _received.RemoveRange(0, _received.Count - LiveRecordLimit);
            _liveDirty = true;
        }
    }

    /// <summary>Publishes live updates each second or replays fixed-size batches until EOF or cancellation.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _timeProvider);
            if (_options.FileName is null) {
                while (await timer.WaitForNextTickAsync(stoppingToken)) {
                    lock (_ingestionLock) {
                        var activeLabel = _appConfiguration?.CachedLabel ?? _appConfiguration?.DefaultLabel;
                        var activePricingValue = _appConfiguration?.CachedSettings?.FirstOrDefault(setting =>
                            string.Equals(setting.Label, activeLabel, StringComparison.Ordinal)
                            && string.Equals(setting.Key, "Warm:Tokenomics:Options", StringComparison.OrdinalIgnoreCase))
                            ?.LoadedValue;
                        var pricingContext = string.Concat(activeLabel, "\0", activePricingValue);
                        if (!_liveDirty
                            && ++_ticksSinceWindowRefresh < 60
                            && string.Equals(_lastPricingContext, pricingContext, StringComparison.Ordinal)) continue;
                        UpdateStore();
                        _liveDirty = false;
                        _ticksSinceWindowRefresh = 0;
                    }
                }
                return;
            }
            while (_position < _options.Events.Length && await timer.WaitForNextTickAsync(stoppingToken)) {
                var count = Math.Min(10, _options.Events.Length - _position);
                _received.AddRange(_options.Events.Skip(_position).Take(count)
                    .Where(fields => fields["Type"] == "S7P-Tokenomics" && fields.ContainsKey("RecordKind")));
                _position += count;
                UpdateStore();
                _logger.LogInformation("Tokenomics replay: {Processed}/{Total} local events", _position, _options.Events.Length);
            }
            if (!stoppingToken.IsCancellationRequested)
                _logger.LogInformation("Tokenomics replay reached EOF; retaining the final snapshot");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private void UpdateStore() {
        var rangeEnd = _timeProvider.GetUtcNow();
        if (_options.FileName is not null) {
            DateTimeOffset? latestTimestamp = null;
            foreach (var fields in _received) {
                if (DateTimeOffset.TryParse(fields.GetValueOrDefault("TimestampUtc"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp)
                    && (latestTimestamp is null || timestamp > latestTimestamp.Value)) {
                    latestTimestamp = timestamp;
                }
            }
            if (latestTimestamp is { } replayTimestamp) rangeEnd = replayTimestamp;
        }

        var modelPricing = new Dictionary<string, ModelTokenPricing>(StringComparer.OrdinalIgnoreCase);
        TokenomicsSettings? currentSettings = null;
        var currentLabel = _appConfiguration?.CachedLabel ?? _appConfiguration?.DefaultLabel;
        var pricingSetting = _appConfiguration?.CachedSettings?.FirstOrDefault(setting =>
            string.Equals(setting.Label, currentLabel, StringComparison.Ordinal)
            && string.Equals(setting.Key, "Warm:Tokenomics:Options", StringComparison.OrdinalIgnoreCase));
        _lastPricingContext = string.Concat(currentLabel, "\0", pricingSetting?.LoadedValue);
        if (pricingSetting is not null) {
            var tokenomicsSettings = new TokenomicsSettings();
            if (tokenomicsSettings.TryParse(pricingSetting.LoadedValue)) {
                currentSettings = tokenomicsSettings;
                foreach (var entry in tokenomicsSettings.ModelCostPerToken) {
                    modelPricing.TryAdd(entry.Key, entry.Value);
                }
                _lastInvalidPricingValue = null;
            } else if (!string.Equals(_lastInvalidPricingValue, pricingSetting.LoadedValue, StringComparison.Ordinal)) {
                _logger.LogWarning(
                    "Ignoring invalid Tokenomics pricing settings for App Configuration label {Label}.",
                    string.IsNullOrEmpty(currentLabel) ? "(No label)" : currentLabel);
                _lastInvalidPricingValue = pricingSetting.LoadedValue;
            }
        } else {
            _lastInvalidPricingValue = null;
        }

        var periodSnapshots = Enum.GetValues<TokenomicsPeriod>().ToImmutableDictionary(
            period => period,
            period => CreateSnapshot(period, rangeEnd, modelPricing, currentSettings));
        _store.Update(CreateSnapshot(modelPricing: modelPricing, tokenomicsSettings: currentSettings), periodSnapshots);
    }

    private TokenomicsDashboardSnapshot CreateSnapshot(
        TokenomicsPeriod? period = null,
        DateTimeOffset? rangeEnd = null,
        IReadOnlyDictionary<string, ModelTokenPricing>? modelPricing = null,
        TokenomicsSettings? tokenomicsSettings = null) {
        var culture = CultureInfo.InvariantCulture;
        var prices = modelPricing ?? ImmutableDictionary<string, ModelTokenPricing>.Empty;
        var hasPriceData = prices.Count > 0;
        var quotaLimit = period switch {
            TokenomicsPeriod.Hourly => tokenomicsSettings?.HourlyTokenLimit,
            TokenomicsPeriod.Daily => tokenomicsSettings?.DailyTokenLimit,
            TokenomicsPeriod.Monthly => tokenomicsSettings?.MonthlyTokenLimit,
            _ => null
        };
        var hasQuotaData = quotaLimit is > 0;
        var anchor = (rangeEnd ?? _timeProvider.GetUtcNow()).ToUniversalTime();
        DateTimeOffset? windowStart = period switch {
            TokenomicsPeriod.Hourly => new DateTimeOffset(
                anchor.Year, anchor.Month, anchor.Day, anchor.Hour, 0, 0, TimeSpan.Zero),
            TokenomicsPeriod.Daily => new DateTimeOffset(
                anchor.Year, anchor.Month, anchor.Day, 0, 0, 0, TimeSpan.Zero),
            TokenomicsPeriod.Monthly => new DateTimeOffset(
                anchor.Year, anchor.Month, 1, 0, 0, 0, TimeSpan.Zero),
            _ => null
        };
        DateTimeOffset? windowEnd = period switch {
            TokenomicsPeriod.Hourly => windowStart!.Value.AddHours(1),
            TokenomicsPeriod.Daily => windowStart!.Value.AddDays(1),
            TokenomicsPeriod.Monthly => windowStart!.Value.AddMonths(1),
            _ => null
        };
        IReadOnlyList<ImmutableDictionary<string, string>> summaries = _received;
        if (windowStart is { } startUtc && windowEnd is { } endUtc) {
            summaries = _received.Where(fields =>
                DateTimeOffset.TryParse(fields.GetValueOrDefault("TimestampUtc"), culture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp)
                && timestamp >= startUtc
                && timestamp < endUtc).ToArray();
        }

        string Identity(IReadOnlyDictionary<string, string> fields, string key) =>
            string.IsNullOrWhiteSpace(fields.GetValueOrDefault(key)) ? "(unknown)" : fields[key];
        string Model(IReadOnlyDictionary<string, string> fields) =>
            !string.IsNullOrWhiteSpace(fields.GetValueOrDefault("EffectiveModel")) ? fields["EffectiveModel"] : Identity(fields, "RequestedModel");
        string Number(double value) => value.ToString("N0", culture);
        DateTimeOffset? Timestamp(IReadOnlyDictionary<string, string> fields) =>
            DateTimeOffset.TryParse(fields.GetValueOrDefault("TimestampUtc"), culture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp)
                ? timestamp
                : null;
        DateTimeOffset Bucket(DateTimeOffset timestamp) {
            var utc = timestamp.UtcDateTime;
            return period switch {
                TokenomicsPeriod.Hourly => new DateTimeOffset(
                    utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero),
                TokenomicsPeriod.Daily => new DateTimeOffset(
                    utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero),
                _ => new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero)
            };
        }
        string BucketLabel(DateTimeOffset timestamp) => period switch {
            TokenomicsPeriod.Hourly => timestamp.ToString("mm", culture),
            TokenomicsPeriod.Daily => timestamp.ToString("HH", culture),
            TokenomicsPeriod.Monthly => timestamp.Day.ToString(culture),
            _ => timestamp.ToString("yyyy-MM-dd", culture)
        };
        var outcomes = summaries.Where(fields => fields["RecordKind"] == "RequestOutcome")
            .GroupBy(fields => fields["MID"], StringComparer.Ordinal).Select(group => group.Last()).ToArray();
        var outcomesByModel = outcomes.ToLookup(Model, StringComparer.Ordinal);
        var outcomesByUser = outcomes.ToLookup(fields => (Name: Identity(fields, "UserId"), Tenant: Identity(fields, "Tenant")));
        var decisions = summaries.Where(fields => fields["RecordKind"] == "PolicyDecision")
            .DistinctBy(fields => fields.GetValueOrDefault("DecisionId") is { Length: > 0 } id
                ? id : $"{fields["MID"]}:{fields.GetValueOrDefault("EvaluationSequence")}:{fields["PolicyAction"]}").ToArray();
        double Tokens(IReadOnlyDictionary<string, string> fields, string key) => TokenomicsReplayOptions.GetTokens(fields, key);
        double Total(IReadOnlyDictionary<string, string> fields) => Tokens(fields, "InputTokens") + Tokens(fields, "OutputTokens");
        Func<IReadOnlyDictionary<string, string>, decimal> spend = fields => {
            if (!prices.TryGetValue(Model(fields), out var pricing)) return 0m;
            var input = TokenomicsReplayOptions.GetTokens(fields, "InputTokens");
            var cached = TokenomicsReplayOptions.GetTokens(fields, "CachedTokens");
            var output = TokenomicsReplayOptions.GetTokens(fields, "OutputTokens");
            return Math.Max(0L, input - cached) * pricing.Input
                + cached * pricing.CachedInput
                + output * pricing.Output;
        };
        Func<decimal, string> money = value => value == 0m
            ? "$0.00"
            : value < 0.01m
                ? "$" + value.ToString("0.######", culture)
                : "$" + value.ToString("N2", culture);
        var total = outcomes.Sum(Total);
        var totalSpend = outcomes.Sum(spend);
        var requests = summaries.Select(fields => fields["MID"]).Distinct(StringComparer.Ordinal).Count();
        var throttles = outcomes.Where(fields => TokenomicsReplayOptions.GetStatus(fields) == 429).Select(fields => fields["MID"])
            .Concat(decisions.Where(fields => fields.GetValueOrDefault("LocalStatusCode") == "429" || fields["PolicyAction"] == "Throttle")
                .Select(fields => fields["MID"])).Distinct(StringComparer.Ordinal).Count();
        var trendByBucket = outcomes.Select(fields => (Fields: fields, Timestamp: Timestamp(fields)))
            .GroupBy(entry => entry.Timestamp is { } timestamp ? Bucket(timestamp) : DateTimeOffset.MinValue)
            .ToDictionary(group => group.Key, group => (
                InputNet: group.Sum(entry => Tokens(entry.Fields, "InputTokens") - Tokens(entry.Fields, "CachedTokens")),
                Cached: group.Sum(entry => Tokens(entry.Fields, "CachedTokens")),
                Output: group.Sum(entry => Tokens(entry.Fields, "OutputTokens")),
                Spend: group.Sum(entry => spend(entry.Fields))));
        IEnumerable<DateTimeOffset> trendBuckets = period switch {
            TokenomicsPeriod.Hourly => Enumerable.Range(0, 60).Select(index => windowStart!.Value.AddMinutes(index)),
            TokenomicsPeriod.Daily => Enumerable.Range(0, 24).Select(index => windowStart!.Value.AddHours(index)),
            TokenomicsPeriod.Monthly => Enumerable.Range(0, DateTime.DaysInMonth(anchor.Year, anchor.Month))
                .Select(index => windowStart!.Value.AddDays(index)),
            _ => trendByBucket.Keys.OrderBy(timestamp => timestamp)
        };
        var orderedTrendBuckets = trendBuckets.ToArray();
        var trend = orderedTrendBuckets.Select(timestamp => {
            var values = trendByBucket.GetValueOrDefault(timestamp);
            return (
                Date: timestamp == DateTimeOffset.MinValue ? "Undated" : BucketLabel(timestamp),
                values.InputNet,
                values.Cached,
                values.Output);
        })
            .ToImmutableArray();
        var maximum = Math.Max(1, trend.Select(point => point.InputNet + point.Cached + point.Output).DefaultIfEmpty(0).Max());
        var spendValues = orderedTrendBuckets
            .Select(timestamp => trendByBucket.GetValueOrDefault(timestamp).Spend)
            .ToArray();
        var spendMaximum = spendValues.DefaultIfEmpty(0m).Max();
        var spendLine = hasPriceData && spendMaximum > 0m
            ? spendValues.Select((value, index) => (
                X: (int)Math.Round((index + 0.5) * 800 / spendValues.Length),
                Y: 155 - (int)Math.Round((double)(value / spendMaximum) * 150)))
                .ToImmutableArray()
            : [];
        var spendAxisTicks = hasPriceData && spendMaximum > 0m
            ? Enumerable.Range(0, 5)
                .Select(index => money(spendMaximum * (4 - index) / 4))
                .ToImmutableArray()
            : [];
        string[] colors = ["#287cf0", "#31c1df", "#48c58a", "#9652ef", "#ffa21b", "#9ba9c1"];
        string[] modelColors = ["model-blue", "model-cyan", "model-green", "model-purple", "model-orange", "model-gray"];
        string[] barColors = ["bar-blue", "bar-cyan", "bar-green", "bar-purple", "bar-orange", "bar-gray"];
        var modelNames = summaries.SelectMany(fields => {
            var names = new[] { "RequestedModel", "EffectiveModel", "ModelBefore", "ModelAfter" }
                .Select(key => fields.GetValueOrDefault(key)).Where(name => !string.IsNullOrWhiteSpace(name)).Cast<string>().ToArray();
            return names.Length == 0 ? new[] { "(unknown)" } : names;
        }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var start = 0.0;
        var stops = new List<string>();
        var models = modelNames.Select((name, index) => {
            var tokens = outcomesByModel[name].Sum(Total);
            var share = total == 0 ? 0 : tokens / total * 100;
            var end = start + share;
            if (share > 0) stops.Add(FormattableString.Invariant($"{colors[index % colors.Length]} {start:F6}% {end:F6}%"));
            start = end;
            return (Name: name, Tokens: Number(tokens), Share: share.ToString("F1", culture) + "%", Color: modelColors[index % modelColors.Length]);
        }).ToImmutableArray();
        var users = summaries.Select(fields => (Name: Identity(fields, "UserId"), Tenant: Identity(fields, "Tenant")))
            .Distinct().Select(user => {
                var usage = outcomesByUser[user].ToArray();
                return (user.Name, user.Tenant, Total: usage.Sum(Total), Input: usage.Sum(fields => Tokens(fields, "InputTokens")),
                    Output: usage.Sum(fields => Tokens(fields, "OutputTokens")), Cached: usage.Sum(fields => Tokens(fields, "CachedTokens")),
                    Spend: usage.Sum(spend));
            }).OrderByDescending(user => user.Total).ThenBy(user => user.Name, StringComparer.Ordinal).ThenBy(user => user.Tenant, StringComparer.Ordinal)
            .Select((user, index) => {
                var quotaPercent = hasQuotaData ? user.Total / quotaLimit!.Value * 100 : 0;
                return (Rank: index + 1, user.Name, user.Tenant, Total: Number(user.Total), Input: Number(user.Input),
                    Output: Number(user.Output), Cached: Number(user.Cached), Spend: hasPriceData ? money(user.Spend) : "Unavailable",
                    Quota: hasQuotaData ? quotaPercent.ToString("F0", culture) + "%" : "Unavailable",
                    QuotaColor: hasQuotaData ? quotaPercent >= 75 ? "quota-red" : quotaPercent >= 50 ? "quota-orange" : "quota-green" : "",
                    Color: "user-blue", Sparkline: "");
            }).ToImmutableArray();
        var tenantSpendValues = outcomes
            .GroupBy(fields => Identity(fields, "Tenant"), StringComparer.Ordinal)
            .Select(group => (Name: group.Key, Amount: group.Sum(spend)))
            .OrderByDescending(tenant => tenant.Amount)
            .ThenBy(tenant => tenant.Name, StringComparer.Ordinal)
            .ToArray();
        var maximumTenantSpend = tenantSpendValues.Select(tenant => tenant.Amount).DefaultIfEmpty(0m).Max();
        var tenantSpend = hasPriceData
            ? tenantSpendValues.Select((tenant, index) => (
                tenant.Name,
                Amount: money(tenant.Amount),
                Width: maximumTenantSpend == 0m
                    ? "0%"
                    : (tenant.Amount / maximumTenantSpend * 100m).ToString("F0", culture) + "%",
                Color: barColors[index % barColors.Length]))
                .ToImmutableArray()
            : [];
        var tenantQuotaValues = outcomes
            .GroupBy(fields => Identity(fields, "Tenant"), StringComparer.Ordinal)
            .Select(group => (Name: group.Key, Tokens: group.Sum(Total)))
            .OrderByDescending(tenant => tenant.Tokens)
            .ThenBy(tenant => tenant.Name, StringComparer.Ordinal)
            .ToArray();
        var quotas = hasQuotaData
            ? tenantQuotaValues.Select(tenant => {
                var quotaPercent = tenant.Tokens / quotaLimit!.Value * 100;
                return (
                    tenant.Name,
                    Amount: quotaPercent.ToString("F0", culture) + "%",
                    Width: Math.Min(100, quotaPercent).ToString("F0", culture) + "%",
                    Color: quotaPercent >= 75 ? "bar-red" : quotaPercent >= 50 ? "bar-orange" : "bar-green");
            }).ToImmutableArray()
            : [];
        var periodLabel = period switch {
            TokenomicsPeriod.Hourly => "Current UTC hour",
            TokenomicsPeriod.Daily => "Today in UTC",
            TokenomicsPeriod.Monthly => "Current UTC month",
            _ => null
        };
        var snapshotLabel = _options.FileName is null
            ? periodLabel is null
                ? FormattableString.Invariant($"Event Hub · {_received.Count:N0} summaries · latest {LiveRecordLimit:N0} retained")
                : FormattableString.Invariant($"Event Hub · {summaries.Count:N0} summaries · {periodLabel.ToLowerInvariant()} · {_received.Count:N0} retained")
            : periodLabel is null
                ? $"Local replay · {_position}/{_options.Events.Length} events" + (_position == _options.Events.Length ? " · EOF" : "")
                : $"Local replay · {summaries.Count:N0} summaries · {periodLabel.ToLowerInvariant()}";
        var trendInterval = period switch {
            TokenomicsPeriod.Hourly => "per-minute",
            TokenomicsPeriod.Daily => "hourly",
            _ => "daily"
        };
        return new TokenomicsDashboardSnapshot {
            SnapshotLabel = snapshotLabel,
            TimePeriods = [periodLabel ?? (_options.FileName is null ? "Retained events" : "Replay to date")],
            Tenants = summaries.Select(fields => Identity(fields, "Tenant")).Distinct().Order(StringComparer.Ordinal).Prepend("All tenants").ToImmutableArray(),
            UserFilters = users.Select(user => user.Name).Distinct().Prepend("All users").ToImmutableArray(),
            ModelFilters = modelNames.Prepend("All models").ToImmutableArray(),
            Metrics = [
                ("Total Tokens", Number(total), "", "▤", "blue", ""),
                ("Total Spend (USD)", hasPriceData ? money(totalSpend) : "Unavailable", "", "$", "blue", ""),
                ("Total Requests", Number(requests), "", "▣", "blue", ""),
                ("Success Rate", outcomes.Length == 0 ? "Unavailable" : (outcomes.Count(fields => TokenomicsReplayOptions.GetStatus(fields) is >= 200 and < 300) * 100.0 / outcomes.Length).ToString("F1", culture) + "%", "", "✓", "green", ""),
                ("Policy Actions", Number(decisions.Length), "", "⬟", "red", ""),
                ("429 Throttles", Number(throttles), "", "!", "red", "")
            ],
            Trend = trend, TokenAxisMaximum = maximum,
            TokenAxisTicks = Enumerable.Range(0, 5).Select(index => Number(maximum * (4 - index) / 4)).ToImmutableArray(),
            SpendAxisTicks = spendAxisTicks,
            HasPriceData = hasPriceData,
            HasQuotaData = hasQuotaData,
            TrendDescription = hasPriceData
                ? $"Recorded {trendInterval} input (excluding cached), cached input, output tokens and calculated spend in UTC"
                : $"Recorded {trendInterval} input (excluding cached), cached input and output tokens in UTC; no price data",
            SpendLine = spendLine,
            TotalTokens = Number(total), Models = models,
            ModelDescription = total == 0 ? "No recorded token usage" : string.Join(", ", models.Select(model => $"{model.Name} {model.Share}")),
            ModelGradient = stops.Count == 0 ? "conic-gradient(#9ba9c1 0% 100%)" : $"conic-gradient({string.Join(", ", stops)})",
            TenantSpend = tenantSpend,
            Quotas = quotas,
            Users = users
        };
    }
}
