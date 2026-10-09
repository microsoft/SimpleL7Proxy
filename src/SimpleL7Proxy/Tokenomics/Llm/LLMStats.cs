namespace SimpleL7Proxy.Tokenomics.Llm;

public class LLMStats
{
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CachedTokens { get; set; }
    public bool IsJailbreakDetected { get; set; }
    public bool IsContentFiltered { get; set; }
    public decimal Cost { get; set; }
}