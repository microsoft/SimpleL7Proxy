using System.Text.Encodings.Web;
using System.Text.Json;

namespace chat_tester.Components.Shared;

/// <summary>
/// A selectable model template. Add or edit entries in <see cref="ModelCatalog.Templates"/>.
/// </summary>
/// <param name="Key">Stable unique key used by the dropdown.</param>
/// <param name="Provider">Provider/group name shown as an optgroup label.</param>
/// <param name="DisplayName">Friendly name shown in the dropdown.</param>
/// <param name="ModelId">The model id sent in the request body.</param>
/// <param name="Schema">Request body shape: "openai", "anthropic", "gemini", or "cohere".</param>
/// <param name="EndpointPath">Optional endpoint path override. When null, a schema-based default is used.
/// May contain a <c>{model}</c> placeholder which is replaced with <see cref="ModelId"/> (e.g. Gemini).</param>
public sealed record ModelTemplate(string Key, string Provider, string DisplayName, string ModelId, string Schema, string? EndpointPath = null);

/// <summary>
/// Central, easy-to-maintain catalog of supported model templates and how to build their request bodies.
/// To support a new model, add a row to <see cref="Templates"/>. To support a new request shape,
/// add a case to <see cref="BuildBody"/>.
/// </summary>
public static class ModelCatalog
{
    // Most popular providers/models first.
    public static readonly IReadOnlyList<ModelTemplate> Templates = new List<ModelTemplate>
    {
        new("gpt-4o", "OpenAI", "GPT-4o", "gpt-4o", "openai"),
        new("gpt-4o-mini", "OpenAI", "GPT-4o mini", "gpt-4o-mini", "openai"),
        new("gpt-4.1", "OpenAI", "GPT-4.1", "gpt-4.1", "openai"),
        new("gpt-4.1-mini", "OpenAI", "GPT-4.1 mini", "gpt-4.1-mini", "openai"),
        new("o3", "OpenAI", "o3 (reasoning)", "o3", "openai"),
        new("o4-mini", "OpenAI", "o4-mini (reasoning)", "o4-mini", "openai"),
        new("gpt-3.5-turbo", "OpenAI", "GPT-3.5 Turbo", "gpt-3.5-turbo", "openai"),

        new("claude-sonnet-4", "Anthropic", "Claude Sonnet 4", "claude-sonnet-4-20250514", "anthropic"),
        new("claude-3-7-sonnet", "Anthropic", "Claude 3.7 Sonnet", "claude-3-7-sonnet-20250219", "anthropic"),
        new("claude-3-5-sonnet", "Anthropic", "Claude 3.5 Sonnet", "claude-3-5-sonnet-20241022", "anthropic"),
        new("claude-3-5-haiku", "Anthropic", "Claude 3.5 Haiku", "claude-3-5-haiku-20241022", "anthropic"),

        new("gemini-2.5-pro", "Google Gemini", "Gemini 2.5 Pro", "gemini-2.5-pro", "gemini"),
        new("gemini-2.0-flash", "Google Gemini", "Gemini 2.0 Flash", "gemini-2.0-flash", "gemini"),
        new("gemini-1.5-pro", "Google Gemini", "Gemini 1.5 Pro", "gemini-1.5-pro", "gemini"),

        new("llama-3.3-70b", "Meta Llama", "Llama 3.3 70B Instruct", "llama-3.3-70b-instruct", "openai"),
        new("llama-3.1-405b", "Meta Llama", "Llama 3.1 405B Instruct", "llama-3.1-405b-instruct", "openai"),

        new("mistral-large", "Mistral", "Mistral Large", "mistral-large-latest", "openai"),
        new("mistral-small", "Mistral", "Mistral Small", "mistral-small-latest", "openai"),

        new("deepseek-chat", "DeepSeek", "DeepSeek V3 (chat)", "deepseek-chat", "openai"),
        new("deepseek-reasoner", "DeepSeek", "DeepSeek R1 (reasoner)", "deepseek-reasoner", "openai"),

        new("grok-3", "xAI", "Grok 3", "grok-3", "openai"),
        new("grok-2", "xAI", "Grok 2", "grok-2-latest", "openai"),

        new("command-r-plus", "Cohere", "Command R+", "command-r-plus-08-2024", "openai"),
        new("command-r", "Cohere", "Command R", "command-r-08-2024", "openai"),
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Finds a template by its key, or null if not found.</summary>
    public static ModelTemplate? FindByKey(string? key) =>
        Templates.FirstOrDefault(m => m.Key == key);

    /// <summary>Finds a template whose model id matches, or null if not found.</summary>
    public static ModelTemplate? FindByModelId(string? modelId) =>
        Templates.FirstOrDefault(m => string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves the endpoint path for a template: the template's own <see cref="ModelTemplate.EndpointPath"/>
    /// if set, otherwise a schema-based default. Any <c>{model}</c> placeholder is replaced with the model id.
    /// </summary>
    public static string ResolveEndpointPath(ModelTemplate template)
    {
        var path = string.IsNullOrWhiteSpace(template.EndpointPath)
            ? DefaultEndpointForSchema(template.Schema)
            : template.EndpointPath!;
        return path.Replace("{model}", template.ModelId);
    }

    /// <summary>Default endpoint path for a given request schema.</summary>
    private static string DefaultEndpointForSchema(string schema) => schema switch
    {
        "anthropic" => "/v1/messages",
        "gemini" => "/v1beta/models/{model}:streamGenerateContent",
        "cohere" => "/v2/chat",
        _ => "/openai/v1/chat/completions"
    };

    /// <summary>Builds the request body JSON for the given template and user message.</summary>
    public static string BuildBody(ModelTemplate template, string message)
    {
        object payload = template.Schema switch
        {
            "anthropic" => new
            {
                model = template.ModelId,
                max_tokens = 1024,
                messages = new[] { new { role = "user", content = message } },
                stream = true
            },
            "gemini" => new
            {
                contents = new[]
                {
                    new { role = "user", parts = new[] { new { text = message } } }
                }
            },
            "cohere" => new
            {
                model = template.ModelId,
                message,
                stream = true
            },
            _ => new
            {
                model = template.ModelId,
                messages = new[] { new { role = "user", content = message } },
                stream = true
            }
        };

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }
}
