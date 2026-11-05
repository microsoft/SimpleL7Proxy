namespace SharedParser.Tokenomics.LLM;

/// <summary>Identifies a supported LLM API format.</summary>
public enum LLMApiFormatEnum {
    /// <summary>OpenAI Chat Completions format.</summary>
    OpenAIChatCompletions,
    /// <summary>OpenAI Responses format.</summary>
    OpenAIResponses,
    /// <summary>Anthropic Messages format.</summary>
    AnthropicMessages
}