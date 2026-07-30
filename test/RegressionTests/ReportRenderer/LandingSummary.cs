namespace RegressionReportRenderer;

internal sealed record LandingSummary(int Total, int Passed, int Failed, int Other, IReadOnlyList<string> Features);
