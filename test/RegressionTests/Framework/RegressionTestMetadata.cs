namespace SimpleL7Proxy.Test;

public sealed record RegressionFeature(
    string Domain,
    string Name,
    string WhyItMatters);

public interface IRegressionTestMetadata
{
    IReadOnlyDictionary<string, RegressionFeature> RegressionFeatures { get; }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RegressionTestCaseAttribute(
    string feature,
    string title,
    string description) : Attribute
{
    public string Feature { get; } = feature;
    public string Title { get; } = title;
    public string Description { get; } = description;
}