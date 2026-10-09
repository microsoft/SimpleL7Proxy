using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using CompanionApp.Components.Shared.EventHub;
using SimpleL7Proxy.Tokenomics;

namespace CompanionApp.Tokenomics;

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
