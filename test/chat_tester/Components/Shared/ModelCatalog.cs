namespace chat_tester.Components.Shared;

/// <summary>
/// A selectable model template. Add or edit entries in <see cref="ModelCatalog.Templates"/>.
/// </summary>
/// <param name="Key">Stable unique key used by the dropdown.</param>
/// <param name="Provider">Provider/group name shown as an optgroup label.</param>
/// <param name="DisplayName">Friendly name shown in the dropdown.</param>
/// <param name="ModelId">The model id sent in the request body.</param>
/// <param name="Schema">Request body shape, such as OpenAI chat, OpenAI responses, Anthropic messages, Gemini, or Cohere chat.</param>
/// <param name="EndpointPath">Optional endpoint path override. When null, a schema-based default is used.
/// May contain a <c>{model}</c> placeholder which is replaced with <see cref="ModelId"/> (e.g. Gemini).</param>
public sealed record ModelTemplate(string Key, string Provider, string DisplayName, string ModelId, string Schema, string? EndpointPath = null);

/// <summary>
/// The request-construction family a schema belongs to. Each family is backed by a model
/// component (for example <c>OpenAiChatModel</c>) that owns its request shape and UI.
/// </summary>
public enum ModelFamily
{
    OpenAiChat,
    OpenAiResponses,
    AnthropicMessages,
    Gemini,
    Cohere
}

/// <summary>
/// Central, easy-to-maintain catalog of supported model templates (metadata only).
/// To support a new model, add a row to <see cref="Templates"/>. The request body for each
/// schema is built by its model component; this catalog maps schemas to families and endpoints.
/// </summary>
public static class ModelCatalog
{
    private const string OpenAiChat = "openai-chat";
    private const string OpenAiChatNonStreaming = "openai-chat-nonstreaming";
    private const string OpenAiResponses = "openai-responses";
    private const string OpenAiResponsesNonStreaming = "openai-responses-nonstreaming";
    private const string AnthropicMessages = "anthropic-messages";
    private const string AnthropicMessagesNonStreaming = "anthropic-messages-nonstreaming";
    private const string GeminiStreamGenerateContent = "gemini-stream-generate-content";
    private const string GeminiGenerateContent = "gemini-generate-content";
    private const string GeminiOpenAiChat = "gemini-openai-chat";
    private const string GeminiOpenAiChatNonStreaming = "gemini-openai-chat-nonstreaming";
    private const string CohereV2Chat = "cohere-v2-chat";
    private const string CohereV2ChatNonStreaming = "cohere-v2-chat-nonstreaming";
    private const string CohereV1Chat = "cohere-v1-chat";
    private const string CohereV1ChatNonStreaming = "cohere-v1-chat-nonstreaming";
    private const string CohereOpenAiChat = "cohere-openai-chat";
    private const string CohereOpenAiChatNonStreaming = "cohere-openai-chat-nonstreaming";

    // Most popular providers/models first. Keep the base keys as the default request mode for existing users.
    public static readonly IReadOnlyList<ModelTemplate> Templates = BuildTemplates();

    private static IReadOnlyList<ModelTemplate> BuildTemplates()
    {
        var templates = new List<ModelTemplate>();

        AddOpenAiModel(templates, "gpt-4o", "OpenAI", "GPT-4o", "gpt-4o");
        AddOpenAiModel(templates, "gpt-4o-mini", "OpenAI", "GPT-4o mini", "gpt-4o-mini");
        AddOpenAiModel(templates, "gpt-4.1", "OpenAI", "GPT-4.1", "gpt-4.1");
        AddOpenAiModel(templates, "gpt-4.1-mini", "OpenAI", "GPT-4.1 mini", "gpt-4.1-mini");
        AddOpenAiModel(templates, "o3", "OpenAI", "o3 (reasoning)", "o3");
        AddOpenAiModel(templates, "o4-mini", "OpenAI", "o4-mini (reasoning)", "o4-mini");
        AddOpenAiModel(templates, "gpt-3.5-turbo", "OpenAI", "GPT-3.5 Turbo", "gpt-3.5-turbo");

        AddAnthropicModel(templates, "claude-sonnet-4", "Anthropic", "Claude Sonnet 4", "claude-sonnet-4-20250514");
        AddAnthropicModel(templates, "claude-3-7-sonnet", "Anthropic", "Claude 3.7 Sonnet", "claude-3-7-sonnet-20250219");
        AddAnthropicModel(templates, "claude-3-5-sonnet", "Anthropic", "Claude 3.5 Sonnet", "claude-3-5-sonnet-20241022");
        AddAnthropicModel(templates, "claude-3-5-haiku", "Anthropic", "Claude 3.5 Haiku", "claude-3-5-haiku-20241022");

        AddGeminiModel(templates, "gemini-2.5-pro", "Google Gemini", "Gemini 2.5 Pro", "gemini-2.5-pro");
        AddGeminiModel(templates, "gemini-2.0-flash", "Google Gemini", "Gemini 2.0 Flash", "gemini-2.0-flash");
        AddGeminiModel(templates, "gemini-1.5-pro", "Google Gemini", "Gemini 1.5 Pro", "gemini-1.5-pro");

        AddOpenAiCompatibleModel(templates, "llama-3.3-70b", "Meta Llama", "Llama 3.3 70B Instruct", "llama-3.3-70b-instruct");
        AddOpenAiCompatibleModel(templates, "llama-3.1-405b", "Meta Llama", "Llama 3.1 405B Instruct", "llama-3.1-405b-instruct");

        AddOpenAiCompatibleModel(templates, "mistral-large", "Mistral", "Mistral Large", "mistral-large-latest");
        AddOpenAiCompatibleModel(templates, "mistral-small", "Mistral", "Mistral Small", "mistral-small-latest");

        AddOpenAiCompatibleModel(templates, "deepseek-chat", "DeepSeek", "DeepSeek V3 (chat)", "deepseek-chat");
        AddOpenAiCompatibleModel(templates, "deepseek-reasoner", "DeepSeek", "DeepSeek R1 (reasoner)", "deepseek-reasoner");

        AddOpenAiCompatibleModel(templates, "grok-3", "xAI", "Grok 3", "grok-3");
        AddOpenAiCompatibleModel(templates, "grok-2", "xAI", "Grok 2", "grok-2-latest");

        AddCohereModel(templates, "command-r-plus", "Cohere", "Command R+", "command-r-plus-08-2024");
        AddCohereModel(templates, "command-r", "Cohere", "Command R", "command-r-08-2024");

        return templates;
    }

    private static void AddOpenAiModel(List<ModelTemplate> templates, string key, string provider, string displayName, string modelId)
    {
        AddOpenAiCompatibleModel(templates, key, provider, displayName, modelId);
        templates.Add(new($"{key}-responses", provider, $"{displayName} - Responses API stream", modelId, OpenAiResponses));
        templates.Add(new($"{key}-responses-nonstreaming", provider, $"{displayName} - Responses API", modelId, OpenAiResponsesNonStreaming));
        templates.Add(new($"{key}-responses-v1", provider, $"{displayName} - /v1/responses stream", modelId, OpenAiResponses, "/v1/responses"));
        templates.Add(new($"{key}-responses-v1-nonstreaming", provider, $"{displayName} - /v1/responses", modelId, OpenAiResponsesNonStreaming, "/v1/responses"));
    }

    private static void AddOpenAiCompatibleModel(List<ModelTemplate> templates, string key, string provider, string displayName, string modelId)
    {
        templates.Add(new(key, provider, $"{displayName} - OpenAI chat stream", modelId, OpenAiChat));
        templates.Add(new($"{key}-nonstreaming", provider, $"{displayName} - OpenAI chat", modelId, OpenAiChatNonStreaming));
        templates.Add(new($"{key}-v1-chat", provider, $"{displayName} - /v1/chat/completions stream", modelId, OpenAiChat, "/v1/chat/completions"));
        templates.Add(new($"{key}-v1-chat-nonstreaming", provider, $"{displayName} - /v1/chat/completions", modelId, OpenAiChatNonStreaming, "/v1/chat/completions"));
    }

    private static void AddAnthropicModel(List<ModelTemplate> templates, string key, string provider, string displayName, string modelId)
    {
        templates.Add(new(key, provider, $"{displayName} - Messages API stream", modelId, AnthropicMessages));
        templates.Add(new($"{key}-nonstreaming", provider, $"{displayName} - Messages API", modelId, AnthropicMessagesNonStreaming));
        templates.Add(new($"{key}-v1-chat", provider, $"{displayName} - /v1/chat/completions stream", modelId, OpenAiChat, "/v1/chat/completions"));
        templates.Add(new($"{key}-v1-chat-nonstreaming", provider, $"{displayName} - /v1/chat/completions", modelId, OpenAiChatNonStreaming, "/v1/chat/completions"));
    }

    private static void AddGeminiModel(List<ModelTemplate> templates, string key, string provider, string displayName, string modelId)
    {
        templates.Add(new(key, provider, $"{displayName} - streamGenerateContent", modelId, GeminiStreamGenerateContent));
        templates.Add(new($"{key}-generate-content", provider, $"{displayName} - generateContent", modelId, GeminiGenerateContent));
        templates.Add(new($"{key}-openai-chat", provider, $"{displayName} - OpenAI chat stream", modelId, GeminiOpenAiChat));
        templates.Add(new($"{key}-openai-chat-nonstreaming", provider, $"{displayName} - OpenAI chat", modelId, GeminiOpenAiChatNonStreaming));
    }

    private static void AddCohereModel(List<ModelTemplate> templates, string key, string provider, string displayName, string modelId)
    {
        templates.Add(new(key, provider, $"{displayName} - OpenAI chat stream", modelId, CohereOpenAiChat));
        templates.Add(new($"{key}-openai-chat-nonstreaming", provider, $"{displayName} - OpenAI chat", modelId, CohereOpenAiChatNonStreaming));
        templates.Add(new($"{key}-v2-chat", provider, $"{displayName} - Cohere v2 chat stream", modelId, CohereV2Chat));
        templates.Add(new($"{key}-v2-chat-nonstreaming", provider, $"{displayName} - Cohere v2 chat", modelId, CohereV2ChatNonStreaming));
        templates.Add(new($"{key}-v1-chat", provider, $"{displayName} - Cohere v1 chat stream", modelId, CohereV1Chat));
        templates.Add(new($"{key}-v1-chat-nonstreaming", provider, $"{displayName} - Cohere v1 chat", modelId, CohereV1ChatNonStreaming));
    }

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
        "anthropic" or AnthropicMessages or AnthropicMessagesNonStreaming => "/v1/messages",
        "gemini" or GeminiStreamGenerateContent => "/v1beta/models/{model}:streamGenerateContent",
        GeminiGenerateContent => "/v1beta/models/{model}:generateContent",
        GeminiOpenAiChat or GeminiOpenAiChatNonStreaming => "/v1beta/openai/chat/completions",
        CohereV2Chat or CohereV2ChatNonStreaming => "/v2/chat",
        "cohere" or CohereV1Chat or CohereV1ChatNonStreaming => "/v1/chat",
        CohereOpenAiChat or CohereOpenAiChatNonStreaming => "/compatibility/v1/chat/completions",
        OpenAiResponses or OpenAiResponsesNonStreaming => "/openai/v1/responses",
        _ => "/openai/v1/chat/completions"
    };

    /// <summary>Maps a request schema to the model-component family that builds its request.</summary>
    public static ModelFamily GetFamily(string schema) => schema switch
    {
        OpenAiResponses or OpenAiResponsesNonStreaming => ModelFamily.OpenAiResponses,
        "anthropic" or AnthropicMessages or AnthropicMessagesNonStreaming => ModelFamily.AnthropicMessages,
        "gemini" or GeminiStreamGenerateContent or GeminiGenerateContent => ModelFamily.Gemini,
        CohereV2Chat or CohereV2ChatNonStreaming or "cohere" or CohereV1Chat or CohereV1ChatNonStreaming => ModelFamily.Cohere,
        // openai-chat, gemini-openai-chat, cohere-openai-chat and their non-streaming variants.
        _ => ModelFamily.OpenAiChat
    };

    /// <summary>Whether the given schema sends a streaming request (non-streaming variants opt out).</summary>
    public static bool SchemaIsStreaming(string schema) =>
        !schema.Contains("nonstreaming", StringComparison.OrdinalIgnoreCase);
}
