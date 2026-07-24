using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SimpleL7Proxy.Rules;

/// <summary>
/// Parses a JSON rule definition into a <see cref="RuleConfig"/> instance.
/// </summary>
/// <remarks>
/// Expected JSON shape:
/// <code>
/// {
///   "rules": [
///     {
///       "name": "route-premium",
///       "if": { "name": "premium-tier", "field": "x-user-tier", "match": "equals", "value": "premium", "ignoreCase": true },
///       "then": { "name": "premium-route", "set": { "backend-pool": "premium-pool", "S7PPriorityKey": "1" } },
///       "elseif": [
///         {
///           "name": "canary-clause",
///           "if": { "name": "canary-hash", "field": "S7PHash", "match": "between", "value": "10", "value2": "20", "mode": "inClosedOpenRange" },
///           "then": { "name": "canary-route", "set": { "backend-pool": "canary-pool" } }
///         }
///       ],
///       "else": { "name": "standard-route", "set": { "backend-pool": "standard-pool" } }
///     }
///   ]
/// }
/// </code>
/// </remarks>
public static class RuleConfigParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    /// <summary>
    /// Parses the supplied JSON string into a <see cref="RuleConfig"/> instance.
    /// </summary>
    /// <exception cref="ArgumentException">The JSON is null, empty, or malformed.</exception>
    public static RuleConfig Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("Rule configuration JSON must not be null or empty.", nameof(json));
        }

        try
        {
            var config = JsonSerializer.Deserialize<RuleConfig>(json, SerializerOptions) ?? new RuleConfig();
            config.Compile();
            return config;
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Invalid rule configuration JSON: {ex.Message}", nameof(json), ex);
        }
        catch (RegexParseException ex)
        {
            throw new ArgumentException($"Invalid regex in rule configuration: {ex.Message}", nameof(json), ex);
        }
    }

    /// <summary>
    /// Parses a bare JSON array of rules (for example the value of a "rules"
    /// property on a user profile) into a <see cref="RuleConfig"/> instance.
    /// </summary>
    /// <exception cref="ArgumentException">The JSON is null, empty, or malformed.</exception>
    public static RuleConfig ParseRules(string rulesJson)
    {
        if (string.IsNullOrWhiteSpace(rulesJson))
        {
            throw new ArgumentException("Rules JSON must not be null or empty.", nameof(rulesJson));
        }

        try
        {
            var rules = JsonSerializer.Deserialize<List<Rule>>(rulesJson, SerializerOptions);
            var config = new RuleConfig { Rules = rules ?? new List<Rule>() };
            config.Compile();
            return config;
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Invalid rules JSON: {ex.Message}", nameof(rulesJson), ex);
        }
        catch (RegexParseException ex)
        {
            throw new ArgumentException($"Invalid regex in rules JSON: {ex.Message}", nameof(rulesJson), ex);
        }
    }

    /// <summary>
    /// Attempts to parse the supplied JSON string without throwing.
    /// </summary>
    public static bool TryParse(string json, out RuleConfig config, out string? error)
    {
        try
        {
            config = Parse(json);
            error = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            config = new RuleConfig();
            error = ex.Message;
            return false;
        }
    }
}
