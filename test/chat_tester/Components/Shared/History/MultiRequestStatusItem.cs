namespace chat_tester.Components.Shared;

public sealed class MultiRequestStatusItem
{
    public int RequestNumber { get; set; }
    public string ContainerApp { get; set; } = string.Empty;
    public string Replica { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public string StatusMessage { get; set; } = string.Empty;
    public int? StatusCode { get; set; }
    public string ContentType { get; set; } = "-";
    public TimeSpan? TimeToFirstByte { get; set; }
    public TimeSpan? Duration { get; set; }
    public int Chunks { get; set; }
    public long TotalBytes { get; set; }
    public string RequestHeadersText { get; set; } = string.Empty;
    public string ResponseHeadersText { get; set; } = string.Empty;
    public string RequestBodyDisplay { get; set; } = string.Empty;
    public string ResponseBody { get; set; } = string.Empty;
    public VisionResultView? VisionResult { get; set; }
    public bool IsRunning { get; set; }
    public bool IsComplete { get; set; }
    public bool IsFailed { get; set; }
}