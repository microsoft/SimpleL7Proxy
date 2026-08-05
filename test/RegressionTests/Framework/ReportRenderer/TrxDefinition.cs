namespace RegressionReportRenderer;

internal sealed class TrxDefinition
{
    public string Name { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
    public List<string> Categories { get; set; } = [];
}
