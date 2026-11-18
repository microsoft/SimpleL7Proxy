using System.Globalization;
using System.Reflection;
using SimpleL7Proxy.Extensions;
using SharedParser.Tokenomics.LLM.Catalog;

namespace SimpleL7Proxy.Tokenomics;

public class TokenomicsHostOptions
{
    public bool Enabled { get; set; } = true;
    
    public Dictionary<LLMApiEnum, TokenomicsApiOptions> APIs { get; set; } = new();
}