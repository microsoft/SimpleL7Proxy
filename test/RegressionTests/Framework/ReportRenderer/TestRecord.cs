namespace RegressionReportRenderer;

internal sealed class TestRecord
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DefinitionName { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public List<string> Categories { get; set; } = [];
    public string Outcome { get; set; } = string.Empty;
    public double DurationMs { get; set; }
    public string StartTime { get; set; } = string.Empty;
    public string EndTime { get; set; } = string.Empty;
    public string Stdout { get; set; } = string.Empty;
    public string Stderr { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string StackTrace { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string Feature { get; set; } = string.Empty;
    public string Why { get; set; } = string.Empty;
    public string ExecutionLabel { get; set; } = string.Empty;
    public List<string> Artifacts { get; set; } = [];
}
