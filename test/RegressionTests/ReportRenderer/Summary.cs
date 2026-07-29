namespace RegressionReportRenderer;

internal sealed class Summary
{
    public int Total { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public int Inconclusive { get; set; }
    public int Other { get; set; }
    public double DurationMs { get; set; }

    public static Summary From(IEnumerable<TestRecord> tests)
    {
        var result = new Summary();
        foreach (var test in tests)
        {
            result.Total++;
            result.DurationMs += test.DurationMs;
            switch (test.Outcome)
            {
                case "Passed": result.Passed++; break;
                case "Failed" or "Error" or "Timeout" or "Aborted": result.Failed++; break;
                case "NotExecuted" or "NotRunnable" or "Disconnected": result.Skipped++; break;
                case "Inconclusive" or "Warning": result.Inconclusive++; break;
                default: result.Other++; break;
            }
        }
        result.DurationMs = Math.Round(result.DurationMs, 3);
        return result;
    }
}
