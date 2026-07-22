using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace chat_tester.Components.Shared;

public sealed class VisionApiInfo
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Endpoint { get; set; } = string.Empty;

    public string Method { get; set; } = "POST";

    public string RequestFormat { get; set; } = string.Empty;

    public string ModelParameter { get; set; } = string.Empty;

    public string[] ImageInputs { get; set; } = Array.Empty<string>();

    public bool SupportsStreaming { get; set; }
}

public sealed class VisionModelInfo
{
    public string Id { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string[] Apis { get; set; } = Array.Empty<string>();

    public string[] InputModes { get; set; } = Array.Empty<string>();
}

public sealed class VisionRequestBuildContext
{
    public string ApiId { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public string Prompt { get; init; } = string.Empty;

    public string ImageUrl { get; init; } = string.Empty;

    public string ImageBase64 { get; init; } = string.Empty;

    public string ImageMimeType { get; init; } = "image/jpeg";

    public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();
}

public sealed class VisionModelCatalog
{
    public const string SectionName = "vision-models";

    private readonly IReadOnlyList<VisionApiInfo> _apis;
    private readonly IReadOnlyList<VisionModelInfo> _models;
    private readonly IReadOnlyList<ModelDefaultRule> _rules;
    private readonly IReadOnlyList<RequestTemplate> _templates;

    public VisionModelCatalog(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        DefaultApi = section["defaultApi"] ?? string.Empty;
        DefaultModel = section["defaultModel"] ?? string.Empty;
        DefaultPrompt = section["defaultPrompt"] ?? "Describe the image.";

        _apis = section.GetSection("apis").Get<List<VisionApiInfo>>() ?? new List<VisionApiInfo>();
        _models = section.GetSection("models").Get<List<VisionModelInfo>>() ?? new List<VisionModelInfo>();
        _rules = section.GetSection("modeldefaults").Get<List<ModelDefaultRule>>() ?? new List<ModelDefaultRule>();
        _templates = RequestTemplateEngine.LoadTemplates(section.GetSection("requestTemplates"));
    }

    public string DefaultApi { get; }

    public string DefaultModel { get; }

    public string DefaultPrompt { get; }

    public IReadOnlyList<VisionModelInfo> ListModels() => _models;

    public VisionModelInfo? GetModel(string? id) =>
        _models.FirstOrDefault(model => string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase));

    public VisionApiInfo? GetApi(string? id) =>
        _apis.FirstOrDefault(api => string.Equals(api.Id, id, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<VisionApiInfo> ApisForModel(string? modelId)
    {
        var model = GetModel(modelId);
        if (model is null)
        {
            return Array.Empty<VisionApiInfo>();
        }

        return model.Apis
            .Select(GetApi)
            .Where(api => api is not null)
            .Select(api => api!)
            .ToList();
    }

    public Dictionary<string, string> GetDefaults(string? modelId, string? apiId)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return result;
        }

        foreach (var rule in _rules)
        {
            if (!Matches(rule.AppliesTo, modelId) || !ApiMatches(rule.AppliesToAPI, apiId))
            {
                continue;
            }

            foreach (var field in rule.Fields)
            {
                foreach (var (name, value) in field)
                {
                    result[name] = value;
                }
            }
        }

        return result;
    }

    public string ResolveEndpointPath(string? modelId, string? apiId, IReadOnlyDictionary<string, string>? fields = null)
    {
        var api = GetApi(apiId);
        if (api is null)
        {
            return string.Empty;
        }

        var endpoint = api.Endpoint.Replace("{model}", modelId ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        if (!UsesQueryParameters(api) || fields is null)
        {
            return endpoint;
        }

        foreach (var (name, value) in fields)
        {
            if (!string.IsNullOrWhiteSpace(value) && !ContainsQueryParameter(endpoint, name))
            {
                endpoint = AppendQueryParameter(endpoint, name, value);
            }
        }

        return endpoint;
    }

    public bool RequiresBase64Image(string? apiId)
    {
        var api = GetApi(apiId);
        return api is not null && api.ImageInputs.Any(input =>
            input.Contains("base64", StringComparison.OrdinalIgnoreCase) ||
            input.Contains("inline_data", StringComparison.OrdinalIgnoreCase));
    }

    public bool RequiresExternalImageUrl(string? apiId)
    {
        var api = GetApi(apiId);
        return api is not null && api.Id.StartsWith("azure-ai-vision", StringComparison.OrdinalIgnoreCase);
    }

    public string BuildRequestBody(VisionRequestBuildContext context)
    {
        var template = RequestTemplateEngine.FindTemplate(_templates, context.ApiId);
        var body = template?.Body?.DeepClone() ?? BuildFallbackRequestBody(context);
        RequestTemplateEngine.ReplaceTokens(body, BuildTokens(context));
        RequestTemplateEngine.ApplyFields(body, context.Fields, ResolveFieldPlacement(context.ApiId));
        return RequestTemplateEngine.Serialize(body);
    }

    private static IReadOnlyDictionary<string, string> BuildTokens(VisionRequestBuildContext context) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["model"] = context.ModelId,
            ["prompt"] = context.Prompt,
            ["imageUrl"] = context.ImageUrl,
            ["imageBase64"] = context.ImageBase64,
            ["imageMimeType"] = context.ImageMimeType
        };

    private static FieldPlacement ResolveFieldPlacement(string apiId) =>
        apiId.StartsWith("azure-ai-vision", StringComparison.OrdinalIgnoreCase) ? FieldPlacement.Skip
        : apiId.Contains("generate-content", StringComparison.OrdinalIgnoreCase) ? FieldPlacement.GeminiGenerationConfig
        : FieldPlacement.TopLevel;

    private static JsonNode BuildFallbackRequestBody(VisionRequestBuildContext context)
    {
        return new JsonObject
        {
            ["model"] = context.ModelId,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = context.Prompt },
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = context.ImageUrl }
                        }
                    }
                }
            }
        };
    }

    private static bool UsesQueryParameters(VisionApiInfo api) =>
        api.Id.StartsWith("azure-ai-vision", StringComparison.OrdinalIgnoreCase);

    private static string AppendQueryParameter(string endpoint, string name, string value)
    {
        var separator = endpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return endpoint + separator + Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value);
    }

    private static bool ContainsQueryParameter(string endpoint, string name)
    {
        var queryIndex = endpoint.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex < 0)
        {
            return false;
        }

        var query = endpoint[(queryIndex + 1)..];
        return query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)[0])
            .Any(key => string.Equals(Uri.UnescapeDataString(key), name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ApiMatches(IReadOnlyCollection<string> patterns, string? api)
    {
        if (patterns is null || patterns.Count == 0)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(api) && Matches(patterns, api);
    }

    private static bool Matches(IEnumerable<string> patterns, string value)
    {
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
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
                if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
