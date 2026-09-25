namespace SimpleL7Proxy.Tokenomics;

/// <summary>Selects the first matching token-governance action for a request.</summary>
public enum ModelOverrideEnum {
    None,
    Override,
    Upgrade,
    Downgrade
}