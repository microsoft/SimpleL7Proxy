using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace chat_tester.Components.Shared;

/// <summary>Where model parameter fields are written into a request body.</summary>
public enum FieldPlacement
{
    /// <summary>Fields are written as top-level properties of the request object.</summary>
    TopLevel,

    /// <summary>Fields are nested under a Gemini <c>generationConfig</c> object using camelCase names.</summary>
    GeminiGenerationConfig,

    /// <summary>Fields are not written into the body (for example, they travel as query parameters).</summary>
    Skip
}

/// <summary>A single request-body template bound to an API id.</summary>
public sealed class RequestTemplate
{
    public string Api { get; init; } = string.Empty;

    public JsonNode? Body { get; init; }
}

/// <summary>
/// Shared engine that turns a configured JSON template plus token and field values into a
/// request body. Both the vision and chat model catalogs use it so the substitution rules
/// (token replacement, scalar parsing, field placement) live in exactly one place.
/// </summary>
public static class RequestTemplateEngine
{
    /// <summary>Serializer options shared by every request body this engine produces.</summary>
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Reads the children of a <c>requestTemplates</c> section into API-keyed templates.</summary>
    public static IReadOnlyList<RequestTemplate> LoadTemplates(IConfigurationSection templatesSection)
    {
        return templatesSection.GetChildren()
            .Select(section => new RequestTemplate
            {
                Api = section["api"] ?? string.Empty,
                Body = BuildJsonNode(section.GetSection("body"))
            })
            .Where(template => !string.IsNullOrWhiteSpace(template.Api) && template.Body is not null)
            .ToList();
    }

    /// <summary>Finds the template for an API id, or <c>null</c> when none is configured.</summary>
    public static RequestTemplate? FindTemplate(IReadOnlyList<RequestTemplate> templates, string? apiId) =>
        templates.FirstOrDefault(template => string.Equals(template.Api, apiId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Recursively converts a configuration section into a JSON node (object, array, or scalar).</summary>
    public static JsonNode? BuildJsonNode(IConfigurationSection section)
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

    /// <summary>Parses a raw string into the most specific JSON scalar (bool, long, double, or string).</summary>
    public static JsonNode? ParseScalar(string? value)
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

    /// <summary>Replaces <c>{{token}}</c> placeholders in every string value of the tree.</summary>
    public static void ReplaceTokens(JsonNode? node, IReadOnlyDictionary<string, string> tokens)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.ToList())
                {
                    ReplaceTokens(property.Value, tokens);
                }
                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    ReplaceTokens(item, tokens);
                }
                break;

            case JsonValue value when value.TryGetValue<string>(out var text):
                node.ReplaceWith(JsonValue.Create(ApplyTokens(text, tokens))!);
                break;
        }
    }

    /// <summary>Writes model parameter fields into the body according to the placement strategy.</summary>
    public static void ApplyFields(JsonNode? body, IReadOnlyDictionary<string, string> fields, FieldPlacement placement)
    {
        if (body is not JsonObject obj || fields.Count == 0 || placement == FieldPlacement.Skip)
        {
            return;
        }

        if (placement == FieldPlacement.GeminiGenerationConfig)
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

    /// <summary>Serializes a request body node to a JSON string using the shared options.</summary>
    public static string Serialize(JsonNode body) => body.ToJsonString(SerializerOptions);

    private static string ApplyTokens(string value, IReadOnlyDictionary<string, string> tokens)
    {
        foreach (var (name, replacement) in tokens)
        {
            value = value.Replace("{{" + name + "}}", replacement ?? string.Empty, StringComparison.Ordinal);
        }

        return value;
    }

    private static string ToGeminiFieldName(string name) => name.ToLowerInvariant() switch
    {
        "top_p" => "topP",
        "top_k" => "topK",
        "max_output_tokens" => "maxOutputTokens",
        _ => name
    };
}
