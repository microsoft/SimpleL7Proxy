using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SimpleL7Proxy.Rules;

namespace SharedParser.Profiles;

/// <summary>
/// Reads and writes the shared profile-file format.
/// </summary>
public class UserProfiles
{
    private const string SharedRulesKey = "S7PRules";
    private const string InlineRulesKey = "rules";

    public string? Error { get; private set; }

    public Dictionary<string, Dictionary<string, string>> Profiles { get; } = new();

    public Dictionary<string, RuleConfig> Rules { get; } = new();

    /// <summary>
    /// Reads a profile array, including a trailing shared-rule library.
    /// </summary>
    public bool TryParse(string input, string userIdField, ILogger logger)
    {
        Profiles.Clear();
        Rules.Clear();
        Error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            Error = "No user config provided to parse.";
            return false;
        }

        var userConfig = JsonSerializer.Deserialize<JsonElement>(input);
        if (userConfig.ValueKind != JsonValueKind.Array)
        {
            Error = "User config is not an array.";
            return false;
        }

        var sharedRules = ReadSharedRuleLibrary(userConfig, userIdField, logger);
        foreach (var profile in userConfig.EnumerateArray())
        {
            if (!profile.TryGetProperty(userIdField, out JsonElement entityElement))
            {
                continue;
            }

            var entityId = entityElement.GetString() ?? string.Empty;
            if (string.IsNullOrEmpty(entityId))
            {
                logger.LogWarning($"Profile field is missing {userIdField}. Skipping...");
                continue;
            }

            var fields = new Dictionary<string, string>();
            var hasSharedAssignments = false;
            JsonElement sharedAssignments = default;
            var hasInlineRules = false;
            JsonElement inlineRules = default;

            foreach (var property in profile.EnumerateObject())
            {
                if (property.Name.Equals(userIdField, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Name.Equals(SharedRulesKey, StringComparison.OrdinalIgnoreCase))
                {
                    hasSharedAssignments = true;
                    sharedAssignments = property.Value;
                    continue;
                }

                if (property.Name.Equals(InlineRulesKey, StringComparison.OrdinalIgnoreCase))
                {
                    hasInlineRules = true;
                    inlineRules = property.Value;
                    continue;
                }

                fields[property.Name] = property.Value.ToString();
            }

            if (hasSharedAssignments)
            {
                TryAddSharedProfileRules(entityId, sharedAssignments, sharedRules, logger);
            }
            else if (hasInlineRules)
            {
                try
                {
                    Rules[entityId] = RuleConfigParser.ParseRules(inlineRules.GetRawText());
                }
                catch (ArgumentException ex)
                {
                    logger.LogWarning($"Profile {entityId} has invalid rules definition - skipping rules: {ex.Message}");
                }
            }

            Profiles[entityId] = fields;
        }

        return true;
    }

    /// <summary>
    /// Creates one profile object for the shared profile blob.
    /// </summary>
    public string ToProfileString(
        string userIdField,
        string id,
        IEnumerable<KeyValuePair<string, string>> fields,
        IEnumerable<string> ruleNames)
    {
        var item = new JsonObject { [userIdField] = id };
        foreach (var field in fields) {
            item.Add(field.Key, JsonValue.Create(field.Value));
        }

        item["S7PRules"] = new JsonArray(ruleNames.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
        return item.ToJsonString();
    }

    /// <summary>
    /// Creates the final shared-rule library object for the profile blob.
    /// </summary>
    public string ToRuleString(RuleConfig rules)
    {
        var definitions = JsonNode.Parse(rules.ToString())
            ?? throw new ArgumentException("Rule configuration did not produce JSON.", nameof(rules));
        return new JsonObject { ["S7PRules"] = definitions }.ToJsonString();
    }

    private static Dictionary<string, string> ReadSharedRuleLibrary(JsonElement userConfig, string userIdField, ILogger logger)
    {
        var library = new Dictionary<string, string>(StringComparer.Ordinal);
        var foundLibrary = false;

        foreach (var entry in userConfig.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || entry.TryGetProperty(userIdField, out _))
            {
                continue;
            }

            JsonElement definitions = default;
            var propertyCount = 0;
            var isLibrary = false;
            foreach (var property in entry.EnumerateObject())
            {
                propertyCount++;
                if (property.Name.Equals(SharedRulesKey, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Array)
                {
                    definitions = property.Value;
                    isLibrary = true;
                }
            }

            if (propertyCount != 1 || !isLibrary)
            {
                continue;
            }

            if (foundLibrary)
            {
                logger.LogWarning("Config contains more than one shared S7PRules entry. Keeping the first.");
                continue;
            }

            foundLibrary = true;
            foreach (var rule in definitions.EnumerateArray())
            {
                if (rule.ValueKind != JsonValueKind.Object
                    || !rule.TryGetProperty("name", out var nameElement)
                    || nameElement.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(nameElement.GetString()))
                {
                    logger.LogWarning("Shared S7PRules entry is missing a rule name. Skipping rule.");
                    continue;
                }

                var ruleName = nameElement.GetString()!;
                if (!library.TryAdd(ruleName, rule.GetRawText()))
                {
                    logger.LogWarning($"Shared rule '{ruleName}' is duplicated. Keeping the first definition.");
                }
            }
        }

        return library;
    }

    private void TryAddSharedProfileRules(
        string entityId,
        JsonElement assignments,
        Dictionary<string, string> sharedRules,
        ILogger logger)
    {
        if (assignments.ValueKind != JsonValueKind.Array)
        {
            logger.LogWarning($"Profile {entityId} has invalid S7PRules definition - skipping rules.");
            return;
        }

        var selectedRules = new List<string>();
        foreach (var assignment in assignments.EnumerateArray())
        {
            var ruleName = assignment.ValueKind == JsonValueKind.String ? assignment.GetString() : null;
            if (string.IsNullOrEmpty(ruleName) || !sharedRules.TryGetValue(ruleName, out var ruleJson))
            {
                logger.LogWarning($"Profile {entityId} references unknown rule '{ruleName}'. Skipping rule.");
                continue;
            }

            selectedRules.Add(ruleJson);
        }

        if (selectedRules.Count == 0)
        {
            return;
        }

        try
        {
            Rules[entityId] = RuleConfigParser.ParseRules("[" + string.Join(',', selectedRules) + "]");
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning($"Profile {entityId} has invalid S7PRules definition - skipping rules: {ex.Message}");
        }
    }
}