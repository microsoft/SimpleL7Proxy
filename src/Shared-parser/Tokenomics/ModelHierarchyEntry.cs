using SharedParser.Tokenomics.LLM.Catalog;

namespace SimpleL7Proxy.Tokenomics;

/// <summary>Defines a model and API destination in an ordered model hierarchy.</summary>
public class ModelHierarchyEntry
{
    /// <summary>Gets or sets the destination model.</summary>
    public string Model { get; set; } = "";

    /// <summary>Gets or sets the destination API; Unknown retains the request API.</summary>
    public LLMApiEnum APIName { get; set; } = LLMApiEnum.Unknown;
}