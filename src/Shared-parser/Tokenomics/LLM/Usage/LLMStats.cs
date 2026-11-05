namespace SimpleL7Proxy.Tokenomics.Llm;

public class LLMStats
{
    public UsageProvider Provider { get; set; }
    public bool UsageAvailable { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CachedTokens { get; set; }
    public int TotalTokens { get; set; }
    public int ReasoningTokens { get; set; }
    public int CacheCreationTokens { get; set; }
    public bool IsJailbreakDetected { get; set; }
    public bool IsContentFiltered { get; set; }
    public decimal Cost { get; set; }
}