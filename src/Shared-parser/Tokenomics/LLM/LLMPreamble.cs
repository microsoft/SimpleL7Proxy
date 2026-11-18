namespace SharedParser.Tokenomics.LLM;

/// <summary>Stores normalized source response metadata collected before generated text.</summary>
public sealed class LLMPreamble {
    /// <summary>Gets the source response identifier when available.</summary>
    public string? ResponseId { get; internal set; }
    /// <summary>Gets the source message identifier when available.</summary>
    public string? MessageId { get; internal set; }
    /// <summary>Gets the source model name when available.</summary>
    public string? Model { get; internal set; }
    /// <summary>Gets the source message role when available.</summary>
    public string? Role { get; internal set; }
    /// <summary>Gets the source creation time in Unix seconds when available.</summary>
    public long? CreatedAt { get; internal set; }
}