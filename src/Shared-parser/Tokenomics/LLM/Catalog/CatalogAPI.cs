using System.Collections.Frozen;

namespace SharedParser.Tokenomics.LLM.Catalog;

/// <summary>Defines an API identity, default path, names, aliases, and wire-compatible family.</summary>
public sealed class CatalogAPI {
    /// <summary>Gets the immutable catalog entries keyed by API identity.</summary>
    public static FrozenDictionary<LLMApiEnum, CatalogAPI> APIs { get; } =
        new CatalogAPI[] {
            new(LLMApiEnum.OpenAIChatCompletions, "/openai/v1/chat/completions", "openai-chat", LLMApiEnum.OpenAIChatCompletions,
                "OpenAI Chat Completions", "/v1/chat/completions", "/openai/chat/completions"),
            new(LLMApiEnum.OpenAIResponses, "/openai/v1/responses", "openai-responses", LLMApiEnum.OpenAIResponses,
                "OpenAI Responses", "/v1/responses", "/openai/responses"),
            new(LLMApiEnum.OpenAIDecisions, "/v1/decisions", "openai-decisions", LLMApiEnum.OpenAIDecisions, "OpenAI Decisions"),
            new(LLMApiEnum.AnthropicMessages, "/v1/messages", "anthropic-messages", LLMApiEnum.AnthropicMessages, "Anthropic Messages"),
            new(LLMApiEnum.GeminiGenerateContent, "/v1beta/models/{model}:generateContent", "gemini-generate-content", LLMApiEnum.GeminiGenerateContent,
                "Gemini generateContent", "/v1beta/models/{model}:streamGenerateContent"),
            new(LLMApiEnum.GeminiOpenAICompatible, "/v1beta/openai/chat/completions", "gemini-openai-chat", LLMApiEnum.OpenAIChatCompletions,
                "Gemini (OpenAI-compatible)"),
            new(LLMApiEnum.CohereOpenAICompatible, "/compatibility/v1/chat/completions", "cohere-openai-chat", LLMApiEnum.OpenAIChatCompletions,
                "Cohere (OpenAI-compatible)"),
            new(LLMApiEnum.CohereV2Chat, "/v2/chat", "cohere-v2-chat", LLMApiEnum.CohereV2Chat, "Cohere v2 Chat"),
            new(LLMApiEnum.CohereV1Chat, "/v1/chat", "cohere-v1-chat", LLMApiEnum.CohereV1Chat, "Cohere v1 Chat"),
            new(LLMApiEnum.SystemOne, "/providers/microsoft/v1/systemone", "systemone", LLMApiEnum.SystemOne, "SystemOne"),
            new(LLMApiEnum.OpenAIChatCompletionsVision, "/openai/v1/chat/completions", "openai-chat-vision", LLMApiEnum.OpenAIChatCompletions,
                "OpenAI Chat Completions (vision)"),
            new(LLMApiEnum.OpenAIResponsesVision, "/openai/v1/responses", "openai-responses-vision", LLMApiEnum.OpenAIResponses,
                "OpenAI Responses (vision)"),
            new(LLMApiEnum.AnthropicMessagesVision, "/v1/messages", "anthropic-messages-vision", LLMApiEnum.AnthropicMessages,
                "Anthropic Messages (vision)"),
            new(LLMApiEnum.GeminiGenerateContentVision, "/v1beta/models/{model}:generateContent", "gemini-generate-content-vision", LLMApiEnum.GeminiGenerateContent,
                "Gemini generateContent (vision)"),
            new(LLMApiEnum.AzureAIVisionImageAnalysis, "/computervision/imageanalysis:analyze?api-version=2024-02-01", "azure-ai-vision-image-analysis",
                LLMApiEnum.AzureAIVisionImageAnalysis, "Azure AI Vision Image Analysis")
        }.ToFrozenDictionary(api => api.API);

    /// <summary>Gets the catalog API identity.</summary>
    public LLMApiEnum API { get; }
    /// <summary>Gets the default path, including any model placeholder or query string.</summary>
    public string Path { get; }
    /// <summary>Gets the canonical short name used in configuration.</summary>
    public string ShortName { get; }
    /// <summary>Gets the API identity representing the wire-compatible family.</summary>
    public LLMApiEnum Family { get; }
    /// <summary>Gets the display name accepted by the catalog parser.</summary>
    public string DisplayName { get; }
    /// <summary>Gets additional request paths recognized for this API.</summary>
    public IReadOnlyList<string> PathAliases { get; }

    /// <summary>Creates immutable API metadata, using the short name when no display name is supplied.</summary>
    public CatalogAPI(LLMApiEnum api, string path, string shortName, LLMApiEnum family,
        string? displayName = null, params string[] pathAliases) {
        if (api == LLMApiEnum.Unknown || !Enum.IsDefined(api)) {
            throw new ArgumentOutOfRangeException(nameof(api), api, "A known API identity is required.");
        }
        if (family == LLMApiEnum.Unknown || !Enum.IsDefined(family)) {
            throw new ArgumentOutOfRangeException(nameof(family), family, "A known API family is required.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(shortName);
        ArgumentNullException.ThrowIfNull(pathAliases);

        API = api;
        Path = path;
        ShortName = shortName;
        Family = family;
        DisplayName = displayName ?? shortName;
        PathAliases = Array.AsReadOnly((string[])pathAliases.Clone());
    }
}