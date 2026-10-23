using System.Globalization;
using System.Reflection;
using SimpleL7Proxy.Extensions;

namespace SimpleL7Proxy.Tokenomics;

public class TokenomicsHostOptions
{
    public bool Enabled { get; set; } = true;
    
    public Dictionary<string, TokenomicsApiOptions> APIs { get; set; } = new();
}