namespace SharedParser.Tokenomics.LLM.Catalog;

/// <summary>Groups models by request-parameter compatibility.</summary>
public enum ModelParameterFamily
{
    Unknown,
    Classic,
    Gpt5,
    Reasoning,
    Decision
}