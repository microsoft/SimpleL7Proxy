namespace RegressionReportRenderer;

internal sealed class ExecutionRecord
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string StartedUtc { get; set; } = string.Empty;
    public string CompletedUtc { get; set; } = string.Empty;
    public int ExitCode { get; set; }
    public string TrxPath { get; set; } = string.Empty;
    public string ConsoleLog { get; set; } = string.Empty;
    public string ConsoleTail { get; set; } = string.Empty;
    public string ParseError { get; set; } = string.Empty;
    public string TrxRunId { get; set; } = string.Empty;
    public string TrxRunName { get; set; } = string.Empty;
    public string TrxStartedUtc { get; set; } = string.Empty;
    public string TrxCompletedUtc { get; set; } = string.Empty;
    public List<TestRecord> Tests { get; set; } = [];
    public Summary Summary { get; set; } = new();
}
