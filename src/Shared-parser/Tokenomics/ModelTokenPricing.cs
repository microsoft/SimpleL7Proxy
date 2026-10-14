using System.Globalization;
using System.Reflection;
using SimpleL7Proxy.Extensions;

namespace SimpleL7Proxy.Tokenomics;

/// <summary>Defines USD prices for each token category supported by a model.</summary>
public sealed class ModelTokenPricing
{
    /// <summary>Gets or sets the USD price per non-cached input token.</summary>
    public decimal Input { get; set; }

    /// <summary>Gets or sets the USD price per cached input token.</summary>
    public decimal CachedInput { get; set; }

    /// <summary>Gets or sets the USD price per output token.</summary>
    public decimal Output { get; set; }
}