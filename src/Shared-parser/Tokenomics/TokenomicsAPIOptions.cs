using System.Globalization;
using System.Reflection;
using SimpleL7Proxy.Extensions;

namespace SimpleL7Proxy.Tokenomics;

public class TokenomicsApiOptions
{
    public bool OverridePath { get; set; } = false;
    public string? Path { get; set; } = null;
}