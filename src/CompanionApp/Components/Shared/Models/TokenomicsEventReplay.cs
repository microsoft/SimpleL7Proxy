using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using CompanionApp.Components.Shared.EventHub;

namespace CompanionApp.Components.Shared;

/// <summary>Validated local replay input and arguments removed from ASP.NET configuration.</summary>
public sealed record TokenomicsReplayOptions(string[] ApplicationArgs, string? FileName, ImmutableArray<ImmutableDictionary<string, string>> Events) {
    /// <summary>Validates the entire NDJSON file before any replay or application startup.</summary>
    public static TokenomicsReplayOptions Parse(string[] args) {
        var applicationArgs = new List<string>();
        string? fileName = null;
        for (var index = 0; index < args.Length; index++) {
            if (!args[index].Equals("--run", StringComparison.OrdinalIgnoreCase)) {
                if (args[index].StartsWith("--run=", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Use --run events <filename>.");
                applicationArgs.Add(args[index]);
                continue;
            }
            if (fileName is not null || index + 2 >= args.Length
                || !args[index + 1].Equals("events", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(args[index + 2]) || args[index + 2].StartsWith("--", StringComparison.Ordinal)) {
                throw new ArgumentException("Replay requires exactly one --run events <filename>.");
            }
            fileName = args[index + 2];
            index += 2;
        }
        if (fileName is null) return new(applicationArgs.ToArray(), null, []);

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
    private readonly List<ImmutableDictionary<string, string>> _received = [];
    private int _position;
    private readonly object _ingestionLock = new();
    private const int LiveRecordLimit = 10_000;
    private bool _liveDirty;

    /// <summary>Disables synthetic data before file replay; live reading initializes it when the reader starts.</summary>
    public TokenomicsEventReplay(TokenomicsReplayOptions options, TokenomicsDashboardStore store,
        ILogger<TokenomicsEventReplay> logger, TimeProvider? timeProvider = null) {
        _options = options;
        _store = store;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (_options.FileName is not null) _store.Update(CreateSnapshot());
    }

    /// <summary>Replaces sample data with an empty live snapshot before Event Hub reading or local import.</summary>
    public void BeginLive() {
        if (_options.FileName is not null) return;
        lock (_ingestionLock) {
            _store.Update(CreateSnapshot());
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
                        if (!_liveDirty) continue;
                        _store.Update(CreateSnapshot());
                        _liveDirty = false;
                    }
                }
                return;
            }
            while (_position < _options.Events.Length && await timer.WaitForNextTickAsync(stoppingToken)) {
                var count = Math.Min(10, _options.Events.Length - _position);
                _received.AddRange(_options.Events.Skip(_position).Take(count)
                    .Where(fields => fields["Type"] == "S7P-Tokenomics" && fields.ContainsKey("RecordKind")));
                _position += count;
                _store.Update(CreateSnapshot());
                _logger.LogInformation("Tokenomics replay: {Processed}/{Total} local events", _position, _options.Events.Length);
            }
            if (!stoppingToken.IsCancellationRequested)
                _logger.LogInformation("Tokenomics replay reached EOF; retaining the final snapshot");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private TokenomicsDashboardSnapshot CreateSnapshot() {
        var culture = CultureInfo.InvariantCulture;
        string Identity(IReadOnlyDictionary<string, string> fields, string key) =>
            string.IsNullOrWhiteSpace(fields.GetValueOrDefault(key)) ? "(unknown)" : fields[key];
        string Model(IReadOnlyDictionary<string, string> fields) =>
            !string.IsNullOrWhiteSpace(fields.GetValueOrDefault("EffectiveModel")) ? fields["EffectiveModel"] : Identity(fields, "RequestedModel");
        string Number(double value) => value.ToString("N0", culture);
        var outcomes = _received.Where(fields => fields["RecordKind"] == "RequestOutcome")
            .GroupBy(fields => fields["MID"], StringComparer.Ordinal).Select(group => group.Last()).ToArray();
        var outcomesByModel = outcomes.ToLookup(Model, StringComparer.Ordinal);
        var outcomesByUser = outcomes.ToLookup(fields => (Name: Identity(fields, "UserId"), Tenant: Identity(fields, "Tenant")));
        var decisions = _received.Where(fields => fields["RecordKind"] == "PolicyDecision")
            .DistinctBy(fields => fields.GetValueOrDefault("DecisionId") is { Length: > 0 } id
                ? id : $"{fields["MID"]}:{fields.GetValueOrDefault("EvaluationSequence")}:{fields["PolicyAction"]}").ToArray();
        double Tokens(IReadOnlyDictionary<string, string> fields, string key) => TokenomicsReplayOptions.GetTokens(fields, key);
        double Total(IReadOnlyDictionary<string, string> fields) => Tokens(fields, "InputTokens") + Tokens(fields, "OutputTokens");
        var total = outcomes.Sum(Total);
        var requests = _received.Select(fields => fields["MID"]).Distinct(StringComparer.Ordinal).Count();
        var throttles = outcomes.Where(fields => TokenomicsReplayOptions.GetStatus(fields) == 429).Select(fields => fields["MID"])
            .Concat(decisions.Where(fields => fields.GetValueOrDefault("LocalStatusCode") == "429" || fields["PolicyAction"] == "Throttle")
                .Select(fields => fields["MID"])).Distinct(StringComparer.Ordinal).Count();
        var trend = outcomes.GroupBy(fields => DateTimeOffset.TryParse(fields.GetValueOrDefault("TimestampUtc"), culture,
                DateTimeStyles.AssumeUniversal, out var timestamp) ? timestamp.UtcDateTime.ToString("yyyy-MM-dd", culture) : "Undated")
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => (Date: group.Key,
                InputNet: group.Sum(fields => Tokens(fields, "InputTokens") - Tokens(fields, "CachedTokens")),
                Cached: group.Sum(fields => Tokens(fields, "CachedTokens")), Output: group.Sum(fields => Tokens(fields, "OutputTokens"))))
            .ToImmutableArray();
        var maximum = Math.Max(1, trend.Select(point => point.InputNet + point.Cached + point.Output).DefaultIfEmpty(0).Max());
        string[] colors = ["#287cf0", "#31c1df", "#48c58a", "#9652ef", "#ffa21b", "#9ba9c1"];
        string[] modelColors = ["model-blue", "model-cyan", "model-green", "model-purple", "model-orange", "model-gray"];
        var modelNames = _received.SelectMany(fields => {
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
        var users = _received.Select(fields => (Name: Identity(fields, "UserId"), Tenant: Identity(fields, "Tenant")))
            .Distinct().Select(user => {
                var usage = outcomesByUser[user].ToArray();
                return (user.Name, user.Tenant, Total: usage.Sum(Total), Input: usage.Sum(fields => Tokens(fields, "InputTokens")),
                    Output: usage.Sum(fields => Tokens(fields, "OutputTokens")), Cached: usage.Sum(fields => Tokens(fields, "CachedTokens")));
            }).OrderByDescending(user => user.Total).ThenBy(user => user.Name, StringComparer.Ordinal).ThenBy(user => user.Tenant, StringComparer.Ordinal)
            .Select((user, index) => (Rank: index + 1, user.Name, user.Tenant, Total: Number(user.Total), Input: Number(user.Input),
                Output: Number(user.Output), Cached: Number(user.Cached), Spend: "Unavailable", Quota: "Unavailable",
                QuotaColor: "", Color: "user-blue", Sparkline: "")).ToImmutableArray();
        return new TokenomicsDashboardSnapshot {
            SnapshotLabel = _options.FileName is null
                ? FormattableString.Invariant($"Event Hub · {_received.Count:N0} summaries · latest {LiveRecordLimit:N0} retained")
                : $"Local replay · {_position}/{_options.Events.Length} events" + (_position == _options.Events.Length ? " · EOF" : ""),
            TimePeriods = [_options.FileName is null ? "Retained events" : "Replay to date"],
            Tenants = _received.Select(fields => Identity(fields, "Tenant")).Distinct().Order(StringComparer.Ordinal).Prepend("All tenants").ToImmutableArray(),
            UserFilters = users.Select(user => user.Name).Distinct().Prepend("All users").ToImmutableArray(),
            ModelFilters = modelNames.Prepend("All models").ToImmutableArray(),
            Metrics = [
                ("Total Tokens", Number(total), "", "▤", "blue", ""),
                ("Total Spend (USD)", "Unavailable", "", "$", "blue", ""),
                ("Total Requests", Number(requests), "", "▣", "blue", ""),
                ("Success Rate", outcomes.Length == 0 ? "Unavailable" : (outcomes.Count(fields => TokenomicsReplayOptions.GetStatus(fields) is >= 200 and < 300) * 100.0 / outcomes.Length).ToString("F1", culture) + "%", "", "✓", "green", ""),
                ("Policy Actions", Number(decisions.Length), "", "⬟", "red", ""),
                ("429 Throttles", Number(throttles), "", "!", "red", "")
            ],
            Trend = trend, TokenAxisMaximum = maximum,
            TokenAxisTicks = Enumerable.Range(0, 5).Select(index => Number(maximum * (4 - index) / 4)).ToImmutableArray(),
            TrendDescription = "Recorded daily input (excluding cached), cached input and output tokens; no price data",
            TotalTokens = Number(total), Models = models,
            ModelDescription = total == 0 ? "No recorded token usage" : string.Join(", ", models.Select(model => $"{model.Name} {model.Share}")),
            ModelGradient = stops.Count == 0 ? "conic-gradient(#9ba9c1 0% 100%)" : $"conic-gradient({string.Join(", ", stops)})",
            Users = users
        };
    }
}
