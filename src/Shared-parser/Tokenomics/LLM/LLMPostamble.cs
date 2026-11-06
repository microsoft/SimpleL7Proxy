namespace SharedParser.Tokenomics.LLM;

/// <summary>Stores normalized final metadata without retaining generated text or source documents.</summary>
public sealed class LLMPostamble {
    /// <summary>Gets the normalized finish reason.</summary>
    public string? FinishReason { get; internal set; }
    /// <summary>Gets the response status when supplied by the source.</summary>
    public string? Status { get; internal set; }
    /// <summary>Gets the matched stop sequence when supplied by the source.</summary>
    public string? StopSequence { get; internal set; }
    /// <summary>Gets a response identifier encountered after the preamble.</summary>
    public string? ResponseId { get; internal set; }
    /// <summary>Gets a message identifier encountered after the preamble.</summary>
    public string? MessageId { get; internal set; }
    /// <summary>Gets a Unix creation time encountered after the preamble.</summary>
    public long? CreatedAt { get; internal set; }
    /// <summary>Gets the source-reported input token count.</summary>
    public long? InputTokens { get; internal set; }
    /// <summary>Gets the source-reported output token count.</summary>
    public long? OutputTokens { get; internal set; }
    /// <summary>Gets the source-reported total token count.</summary>
    public long? TotalTokens { get; internal set; }
    /// <summary>Gets the cached input token count when supplied.</summary>
    public long? CachedInputTokens { get; internal set; }
    /// <summary>Gets the cache creation input token count when supplied.</summary>
    public long? CacheCreationInputTokens { get; internal set; }
    /// <summary>Gets the reasoning output token count when supplied.</summary>
    public long? ReasoningOutputTokens { get; internal set; }
}