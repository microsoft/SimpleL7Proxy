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

internal sealed class VisionRequestTemplate
{
    public string Api { get; init; } = string.Empty;

    public JsonNode? Body { get; init; }
}

public sealed class VisionModelCatalog
{
    public const string SectionName = "vision-models";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IReadOnlyList<VisionApiInfo> _apis;
    private readonly IReadOnlyList<VisionModelInfo> _models;
    private readonly IReadOnlyList<ModelDefaultRule> _rules;
    private readonly IReadOnlyList<VisionRequestTemplate> _templates;

    public VisionModelCatalog(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        DefaultApi = section["defaultApi"] ?? string.Empty;
        DefaultModel = section["defaultModel"] ?? string.Empty;
        DefaultPrompt = section["defaultPrompt"] ?? "Describe the image.";

        _apis = section.GetSection("apis").Get<List<VisionApiInfo>>() ?? new List<VisionApiInfo>();
        _models = section.GetSection("models").Get<List<VisionModelInfo>>() ?? new List<VisionModelInfo>();
        _rules = section.GetSection("modeldefaults").Get<List<ModelDefaultRule>>() ?? new List<ModelDefaultRule>();
        _templates = section.GetSection("requestTemplates")
            .GetChildren()
            .Select(ReadTemplate)
            .Where(template => !string.IsNullOrWhiteSpace(template.Api) && template.Body is not null)
            .ToList();
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
        var template = _templates.FirstOrDefault(item => string.Equals(item.Api, context.ApiId, StringComparison.OrdinalIgnoreCase));
        var body = template?.Body?.DeepClone() ?? BuildFallbackRequestBody(context);
        ReplaceTemplateValues(body, context);
        ApplyFieldValues(body, context.ApiId, context.Fields);
        return body.ToJsonString(SerializerOptions);
    }

    private static VisionRequestTemplate ReadTemplate(IConfigurationSection section)
    {
        return new VisionRequestTemplate
        {
            Api = section["api"] ?? string.Empty,
            Body = BuildJsonNode(section.GetSection("body"))
        };
    }

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

    private static JsonNode? BuildJsonNode(IConfigurationSection section)
    {
        var children = section.GetChildren().ToList();
        if (children.Count == 0)
        {
            return ParseScalar(section.Value);
        }

        if (children.All(child => int.TryParse(child.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
        {
            var array = new JsonArray();
            foreach (var child in children.OrderBy(child => int.Parse(child.Key, CultureInfo.InvariantCulture)))
            {
                array.Add(BuildJsonNode(child));
            }

            return array;
        }

        var obj = new JsonObject();
        foreach (var child in children)
        {
            obj[child.Key] = BuildJsonNode(child);
        }

        return obj;
    }

    private static JsonNode? ParseScalar(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (bool.TryParse(value, out var boolValue))
        {
            return JsonValue.Create(boolValue);
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
        {
            return JsonValue.Create(longValue);
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
        {
            return JsonValue.Create(doubleValue);
        }

        return JsonValue.Create(value);
    }

    private static void ReplaceTemplateValues(JsonNode? node, VisionRequestBuildContext context)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.ToList())
                {
                    ReplaceTemplateValues(property.Value, context);
                }
                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    ReplaceTemplateValues(item, context);
                }
                break;

            case JsonValue value when value.TryGetValue<string>(out var text):
                node.ReplaceWith(JsonValue.Create(ReplaceTokens(text, context))!);
                break;
        }
    }

    private static string ReplaceTokens(string value, VisionRequestBuildContext context)
    {
        return value
            .Replace("{{model}}", context.ModelId, StringComparison.Ordinal)
            .Replace("{{prompt}}", context.Prompt, StringComparison.Ordinal)
            .Replace("{{imageUrl}}", context.ImageUrl, StringComparison.Ordinal)
            .Replace("{{imageBase64}}", context.ImageBase64, StringComparison.Ordinal)
            .Replace("{{imageMimeType}}", context.ImageMimeType, StringComparison.Ordinal);
    }

    private static void ApplyFieldValues(JsonNode? body, string apiId, IReadOnlyDictionary<string, string> fields)
    {
        if (body is not JsonObject obj || fields.Count == 0 || apiId.StartsWith("azure-ai-vision", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (apiId.Contains("gemini", StringComparison.OrdinalIgnoreCase))
        {
            if (obj["generationConfig"] is not JsonObject generationConfig)
            {
                generationConfig = new JsonObject();
                obj["generationConfig"] = generationConfig;
            }

            foreach (var (name, value) in fields)
            {
                generationConfig[ToGeminiFieldName(name)] = ParseScalar(value);
            }

            return;
        }

        foreach (var (name, value) in fields)
        {
            obj[name] = ParseScalar(value);
        }
    }

    private static string ToGeminiFieldName(string name) => name.ToLowerInvariant() switch
    {
        "top_p" => "topP",
        "top_k" => "topK",
        "max_output_tokens" => "maxOutputTokens",
        _ => name
    };

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
