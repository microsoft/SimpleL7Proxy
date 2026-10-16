using System.Text.Json;
using System.Text.Json.Nodes;
using SimpleL7Proxy.Rules;

namespace CompanionApp.Components.Shared;

/// <summary>
/// Holds a local profile-file draft independently of App Configuration settings.
/// </summary>
public sealed class ProfileConfigurationDocument {
    public string UserIdField { get; set; } = "userId";
    public List<ProfileConfigurationEntry> Profiles { get; } = [];
    public List<JsonObject> Rules { get; } = [];

    /// <summary>
    /// Imports a complete file atomically, including legacy inline rule definitions.
    /// </summary>
    public void Import(string json) {
        using var parsed = JsonDocument.Parse(json);
        RejectDuplicateProperties(parsed.RootElement);
        var array = JsonNode.Parse(json) as JsonArray
            ?? throw new ArgumentException("The configuration must be a JSON array.");
        var candidate = new ProfileConfigurationDocument { UserIdField = UserIdField };
        for (var index = 0; index < array.Count; index++) {
            var item = array[index] as JsonObject
                ?? throw new ArgumentException("Each configuration entry must be an object.");
            if (!item.ContainsKey(UserIdField)) {
                if (index != array.Count - 1 || item.Count != 1 || item["S7PRules"] is not JsonArray definitions) {
                    throw new ArgumentException($"Each profile requires '{UserIdField}'; only the final entry may contain rule definitions.");
                }
                foreach (var definition in definitions) {
                    candidate.Rules.Add(definition?.DeepClone() as JsonObject
                        ?? throw new ArgumentException("Each rule definition must be an object."));
                }
                continue;
            }

            var profile = new ProfileConfigurationEntry { Id = ReadString(item[UserIdField], UserIdField) };
            foreach (var field in item) {
                if (field.Key == UserIdField) {
                    continue;
                }
                if (field.Key == "S7PRules") {
                    if (field.Value is not JsonArray assignments) {
                        throw new ArgumentException("A profile's S7PRules must be an array of rule names.");
                    }
                    profile.RuleNames.AddRange(assignments.Select(value => ReadString(value, "Rule name")));
                } else if (field.Key.Equals("rules", StringComparison.OrdinalIgnoreCase)) {
                    if (field.Value is not JsonArray inlineRules) {
                        throw new ArgumentException("Legacy rules must be an array of rule definitions.");
                    }
                    foreach (var node in inlineRules) {
                        var rule = node?.DeepClone() as JsonObject
                            ?? throw new ArgumentException("Each inline rule must be an object.");
                        var name = ReadString(rule["name"], "Rule name");
                        var existing = candidate.Rules.FirstOrDefault(value => RuleName(value) == name);
                        if (existing is null) {
                            candidate.Rules.Add(rule);
                        } else if (!JsonNode.DeepEquals(existing, rule)) {
                            throw new ArgumentException($"Inline rule '{name}' has conflicting definitions.");
                        }
                        profile.RuleNames.Add(name);
                    }
                } else {
                    profile.Fields.Add(new ProfileConfigurationField {
                        Name = field.Key, Value = ReadString(field.Value, field.Key)
                    });
                }
            }
            candidate.Profiles.Add(profile);
        }
        candidate.Validate();
        Profiles.Clear();
        Profiles.AddRange(candidate.Profiles);
        Rules.Clear();
        Rules.AddRange(candidate.Rules);
    }

    /// <summary>
    /// Validates and exports profiles followed by exactly one shared rule-library entry.
    /// </summary>
    public string Export() {
        Validate();
        var result = new JsonArray();
        foreach (var profile in Profiles) {
            var item = new JsonObject { [UserIdField] = profile.Id };
            foreach (var field in profile.Fields) {
                item.Add(field.Name, JsonValue.Create(field.Value));
            }
            item["S7PRules"] = new JsonArray(profile.RuleNames.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
            result.Add(item);
        }
        result.Add(new JsonObject { ["S7PRules"] = new JsonArray(Rules.Select(rule => (JsonNode?)rule.DeepClone()).ToArray()) });
        return result.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Validates identities, fields, assignments, and runtime-compatible rule definitions.
    /// </summary>
    public void Validate() {
        if (string.IsNullOrWhiteSpace(UserIdField) || UserIdField.Equals("S7PRules", StringComparison.OrdinalIgnoreCase)
            || UserIdField.Equals("rules", StringComparison.OrdinalIgnoreCase)) {
            throw new ArgumentException("The profile ID field cannot be empty or use a reserved rule key.");
        }
        var ruleNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in Rules) {
            ValidateRule(rule);
            if (!ruleNames.Add(RuleName(rule))) {
                throw new ArgumentException($"Duplicate rule name '{RuleName(rule)}'.");
            }
        }
        var userIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in Profiles) {
            if (string.IsNullOrWhiteSpace(profile.Id) || !userIds.Add(profile.Id)) {
                throw new ArgumentException("Profile IDs must be non-empty and unique.");
            }
            var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { UserIdField, "S7PRules", "rules" };
            foreach (var field in profile.Fields) {
                if (string.IsNullOrWhiteSpace(field.Name) || !fields.Add(field.Name)) {
                    throw new ArgumentException($"Profile '{profile.Id}' has an empty, duplicate, or reserved field name.");
                }
                if (field.Name.Any(char.IsWhiteSpace) || field.Name.Contains(':')
                    || field.Value.Contains('\r') || field.Value.Contains('\n')) {
                    throw new ArgumentException($"Profile field '{field.Name}' must contain a header name and a single-line value.");
                }
            }
            if (profile.RuleNames.Distinct(StringComparer.Ordinal).Count() != profile.RuleNames.Count
                || profile.RuleNames.Any(name => !ruleNames.Contains(name))) {
                throw new ArgumentException($"Profile '{profile.Id}' has duplicate or unknown rule assignments.");
            }
        }
    }

    /// <summary>
    /// Validates an editable rule before it replaces a saved draft definition.
    /// </summary>
    public static void ValidateRule(JsonObject rule) {
        ValidateNode(rule, 0);
        RuleConfigParser.ParseRules(new JsonArray(rule.DeepClone()).ToJsonString());
    }

    /// <summary>
    /// Parses a rule without allowing ambiguous duplicate properties.
    /// </summary>
    public static JsonObject ParseRule(string json) {
        using var parsed = JsonDocument.Parse(json);
        RejectDuplicateProperties(parsed.RootElement);
        var rule = JsonNode.Parse(json) as JsonObject
            ?? throw new ArgumentException("A rule must be a JSON object.");
        ValidateRule(rule);
        return rule;
    }

    /// <summary>
    /// Gets a rule's case-sensitive shared-library name.
    /// </summary>
    public static string RuleName(JsonObject rule) => ReadString(rule["name"], "Rule name");

    private static void ValidateNode(JsonObject node, int depth) {
        if (depth > 16) {
            throw new ArgumentException("Rule nesting cannot exceed 16 levels.");
        }
        if (string.IsNullOrWhiteSpace(ReadString(node["name"], "Node name"))) {
            throw new ArgumentException("Every rule node requires a name.");
        }
        if (node.ContainsKey("set")) {
            if (node["set"] is not JsonObject headers) {
                throw new ArgumentException("A set action must be an object of string values.");
            }
            foreach (var header in headers) {
                var value = ReadString(header.Value, header.Key);
                if (string.IsNullOrWhiteSpace(header.Key) || header.Key.Any(char.IsWhiteSpace)
                    || header.Key.Contains(':') || value.Contains('\r') || value.Contains('\n')) {
                    throw new ArgumentException("Set actions require header names and single-line string values.");
                }
            }
            return;
        }
        if (node["if"] is not JsonObject condition || string.IsNullOrWhiteSpace(ReadString(condition["name"], "Condition name"))
            || string.IsNullOrWhiteSpace(ReadString(condition["field"], "Condition field"))) {
            throw new ArgumentException("A conditional rule requires a named if condition and field.");
        }
        foreach (var key in new[] { "value", "value2" }) {
            if (condition.ContainsKey(key)) {
                ReadString(condition[key], key);
            }
        }
        if (node["then"] is not JsonObject then) {
            throw new ArgumentException("A conditional rule requires a then branch.");
        }
        ValidateNode(then, depth + 1);
        if (node.ContainsKey("elseif")) {
            if (node["elseif"] is not JsonArray alternatives) {
                throw new ArgumentException("Elseif must be an array.");
            }
            foreach (var alternative in alternatives) {
                if (alternative is not JsonObject branch || branch.ContainsKey("set")) {
                    throw new ArgumentException("Each elseif must contain a named condition and then branch.");
                }
                ValidateNode(branch, depth + 1);
            }
        }
        if (node.ContainsKey("else")) {
            if (node["else"] is not JsonObject fallback) {
                throw new ArgumentException("Else must be an action or conditional object.");
            }
            ValidateNode(fallback, depth + 1);
        }
    }

    private static string ReadString(JsonNode? value, string field) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text)
            ? text : throw new ArgumentException($"'{field}' must be a string.");

    private static void RejectDuplicateProperties(JsonElement element) {
        if (element.ValueKind == JsonValueKind.Object) {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject()) {
                if (!names.Add(property.Name)) {
                    throw new ArgumentException($"Duplicate JSON property '{property.Name}'.");
                }
                RejectDuplicateProperties(property.Value);
            }
        } else if (element.ValueKind == JsonValueKind.Array) {
            foreach (var item in element.EnumerateArray()) {
                RejectDuplicateProperties(item);
            }
        }
    }
}

/// <summary>
/// An editable user profile and its ordered shared-rule assignments.
/// </summary>
public sealed class ProfileConfigurationEntry {
    public string Id { get; set; } = "";
    public List<ProfileConfigurationField> Fields { get; } = [];
    public List<string> RuleNames { get; } = [];
}

/// <summary>
/// An editable string field, retaining arbitrary profile headers.
/// </summary>
public sealed class ProfileConfigurationField {
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}
