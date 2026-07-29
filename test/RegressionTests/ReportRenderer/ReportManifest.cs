namespace RegressionReportRenderer;

internal sealed class ReportManifest
{
    public int SchemaVersion { get; set; } = 3;
    public string MasterRunId { get; set; } = string.Empty;
    public string CreatedUtc { get; set; } = string.Empty;
    public string UpdatedUtc { get; set; } = string.Empty;
    public List<ExecutionRecord> Executions { get; set; } = [];
}
