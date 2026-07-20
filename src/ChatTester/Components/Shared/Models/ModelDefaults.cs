using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace chat_tester.Components.Shared;

/// <summary>
/// A single <c>modeldefaults</c> rule: a set of model name patterns and the field
/// defaults that apply to any model matching one of those patterns.
/// </summary>
public sealed class ModelDefaultRule
{
    /// <summary>
    /// Model name patterns this rule applies to. Supports an exact model id,
    /// the wildcard <c>*</c> (all models), or a prefix glob such as <c>gpt-*</c>.
    /// </summary>
    public string[] AppliesTo { get; set; } = Array.Empty<string>();

    /// <summary>
    /// API id patterns this rule applies to. Supports an exact api id, the wildcard
    /// <c>*</c> (all APIs), or a prefix glob. When empty, the rule applies to every API.
    /// </summary>
    public string[] AppliesToAPI { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Field defaults, each as a single-key object (<c>{ "temperature": "0.7" }</c>),
    /// matching the shape of the <c>modeldefaults</c> section in configuration.
    /// </summary>
    public List<Dictionary<string, string>> Fields { get; set; } = new();

    /// <summary>
    /// Optional endpoint prefix default for model/API combinations matched by this rule.
    /// Later matching rules override earlier prefix defaults.
    /// </summary>
    public string? PathPrefix { get; set; }
}

/// <summary>
/// A selectable API entry from the <c>apis</c> configuration section.
/// </summary>
public sealed class ApiInfo
{
    /// <summary>Stable api id referenced by models and default rules.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Friendly name shown in the dropdown.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Endpoint path for the api. May contain a <c>{model}</c> placeholder.</summary>
    public string Endpoint { get; set; } = string.Empty;
}

/// <summary>
/// A selectable model entry from the <c>models</c> configuration section.
/// </summary>
public sealed class ModelInfo
{
    /// <summary>The model id sent in the request body.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Provider/group name, used as an optgroup label.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Friendly name shown in the dropdown.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The ids of the APIs this model supports.</summary>
    public string[] Apis { get; set; } = Array.Empty<string>();
}

/// <summary>Inputs for building a chat request body from a configured template.</summary>
public sealed class ChatRequestBuildContext
{
    public string ApiId { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public string Prompt { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Singleton that resolves per-model default field values from the <c>modeldefaults</c>
/// configuration section. Rules are evaluated in order; a later matching rule overrides
/// an earlier one for the same field, so shared (<c>*</c>) defaults can be specialized
/// by more specific patterns.
/// </summary>
public sealed class ModelDefaults
{
    /// <summary>Configuration section these defaults bind from.</summary>
    public const string SectionName = "modeldefaults";

    /// <summary>Configuration section listing the available models.</summary>
    public const string ModelsSectionName = "models";

    /// <summary>Configuration section listing the available APIs.</summary>
    public const string ApisSectionName = "apis";

    private readonly IReadOnlyList<ModelDefaultRule> _rules;
    private readonly IReadOnlyList<ModelInfo> _models;
    private readonly IReadOnlyList<ApiInfo> _apis;
    private readonly IReadOnlyList<RequestTemplate> _templates;

    public ModelDefaults(IConfiguration configuration)
    {
        _rules = configuration.GetSection(SectionName).Get<List<ModelDefaultRule>>()
            ?? new List<ModelDefaultRule>();
        _models = configuration.GetSection(ModelsSectionName).Get<List<ModelInfo>>()
            ?? new List<ModelInfo>();
        _apis = configuration.GetSection(ApisSectionName).Get<List<ApiInfo>>()
            ?? new List<ApiInfo>();
        _templates = RequestTemplateEngine.LoadTemplates(configuration.GetSection("requestTemplates"));
    }

    /// <summary>Returns the list of all configured models, including provider and display name.</summary>
    public IReadOnlyList<ModelInfo> ListModels() => _models;

    /// <summary>Finds a model by its id, or null if not found.</summary>
    public ModelInfo? GetModel(string? id) =>
        _models.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds an API by its id, or null if not found.</summary>
    public ApiInfo? GetApi(string? id) =>
        _apis.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns the APIs supported by the given model, in configured order.</summary>
    public IReadOnlyList<ApiInfo> ApisForModel(string? modelId)
    {
        var model = GetModel(modelId);
        if (model is null)
        {
            return Array.Empty<ApiInfo>();
        }

        return model.Apis
            .Select(GetApi)
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList();
    }

    /// <summary>Builds the request body for the given chat model/API from the configured template.</summary>
    public string BuildRequestBody(ChatRequestBuildContext context)
    {
        var template = RequestTemplateEngine.FindTemplate(_templates, context.ApiId);
        var body = template?.Body?.DeepClone() ?? BuildFallbackBody(context);
        RequestTemplateEngine.ReplaceTokens(body, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["model"] = context.ModelId,
            ["prompt"] = context.Prompt
        });
        RequestTemplateEngine.ApplyFields(body, context.Fields, ResolveFieldPlacement(context.ApiId));
        return RequestTemplateEngine.Serialize(body);
    }

    /// <summary>Resolves the endpoint path for a chat model/API, substituting any <c>{model}</c> placeholder.</summary>
    public string ResolveEndpointPath(string? modelId, string? apiId)
    {
        var api = GetApi(apiId);
        return api is null
            ? string.Empty
            : api.Endpoint.Replace("{model}", modelId ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static FieldPlacement ResolveFieldPlacement(string apiId) =>
        apiId.Contains("generate-content", StringComparison.OrdinalIgnoreCase)
            ? FieldPlacement.GeminiGenerationConfig
            : FieldPlacement.TopLevel;

    private static JsonNode BuildFallbackBody(ChatRequestBuildContext context) => new JsonObject
    {
        ["model"] = context.ModelId,
        ["messages"] = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = context.Prompt }
        }
    };


    /// <summary>
    /// Returns the merged field defaults for the given model name, optionally scoped to an
    /// API. A rule applies when its model patterns match and its API patterns match (an empty
    /// <c>appliesToAPI</c> matches every API). Field names map to their default values as
    /// strings. Returns an empty dictionary when nothing matches.
    /// </summary>
    public Dictionary<string, string> GetDefaults(string? modelName, string? api = null)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return result;
        }

        foreach (var rule in _rules)
        {
            if (!Matches(rule.AppliesTo, modelName))
            {
                continue;
            }

            if (!ApiMatches(rule.AppliesToAPI, api))
            {
                continue;
            }

            foreach (var field in rule.Fields)
            {
                foreach (var (name, value) in field)
                {
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        result.Remove(name);
                    }
                    else
                    {
                        result[name] = value;
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Returns the configured endpoint prefix default for the given model/API combination.
    /// Rules are evaluated in order, so later matching rules override earlier values.
    /// </summary>
    public string GetPathPrefix(string? modelName, string? api = null)
    {
        var result = string.Empty;
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return result;
        }

        foreach (var rule in _rules)
        {
            if (!Matches(rule.AppliesTo, modelName))
            {
                continue;
            }

            if (!ApiMatches(rule.AppliesToAPI, api))
            {
                continue;
            }

            if (rule.PathPrefix is not null)
            {
                result = rule.PathPrefix;
            }
        }

        return result;
    }

    private static bool ApiMatches(IReadOnlyCollection<string> patterns, string? api)
    {
        // An empty appliesToAPI means the rule is not scoped to any particular API.
        if (patterns is null || patterns.Count == 0)
        {
            return true;
        }

        // The rule is API-scoped but no API was requested, so it cannot match.
        return !string.IsNullOrWhiteSpace(api) && Matches(patterns, api);
    }

    private static bool Matches(IEnumerable<string> patterns, string modelName)
    {
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                continue;
            }

            if (pattern == "*")
            {
                return true;
            }

            if (pattern.EndsWith('*'))
            {
                var prefix = pattern[..^1];
                if (modelName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (string.Equals(pattern, modelName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
