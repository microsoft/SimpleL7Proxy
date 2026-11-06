namespace SharedParser.Tokenomics.LLM.Catalog;

/// <summary>Identifies a catalog API independently of its response format.</summary>
public enum LLMApiEnum {
    /// <summary>An unspecified or unrecognized API.</summary>
    Unknown = 0,
    /// <summary>OpenAI Chat Completions API.</summary>
    OpenAIChatCompletions,
    /// <summary>OpenAI Responses API.</summary>
    OpenAIResponses,
    /// <summary>OpenAI Decisions API.</summary>
    OpenAIDecisions,
    /// <summary>Anthropic Messages API.</summary>
    AnthropicMessages,
    /// <summary>Gemini generateContent API.</summary>
    GeminiGenerateContent,
    /// <summary>Gemini OpenAI-compatible API.</summary>
    GeminiOpenAICompatible,
    /// <summary>Cohere OpenAI-compatible API.</summary>
    CohereOpenAICompatible,
    /// <summary>Cohere v2 Chat API.</summary>
    CohereV2Chat,
    /// <summary>Cohere v1 Chat API.</summary>
    CohereV1Chat,
    /// <summary>SystemOne API.</summary>
    SystemOne,
    /// <summary>OpenAI Chat Completions vision API.</summary>
    OpenAIChatCompletionsVision,
    /// <summary>OpenAI Responses vision API.</summary>
    OpenAIResponsesVision,
    /// <summary>Anthropic Messages vision API.</summary>
    AnthropicMessagesVision,
    /// <summary>Gemini generateContent vision API.</summary>
    GeminiGenerateContentVision,
    /// <summary>Azure AI Vision Image Analysis API.</summary>
    AzureAIVisionImageAnalysis
}