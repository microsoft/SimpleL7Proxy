namespace chat_tester.Components.Shared;

public sealed class MultiRequestStatusItem
{    public int RequestNumber { get; set; }
    public string ContainerApp { get; set; } = string.Empty;
    public string Replica { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public string StatusMessage { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string BackendHost { get; set; } = string.Empty;
    public string EndpointKey { get; set; } = string.Empty;
    public bool IsEndpointCircuitBreakerOpen { get; set; }
    public bool IsServerCircuitBreakerSignal { get; set; }
    public int? StatusCode { get; set; }
    public string ContentType { get; set; } = "-";
    public TimeSpan? TimeToFirstByte { get; set; }
    public TimeSpan? Duration { get; set; }
    public int Chunks { get; set; }
    public long TotalBytes { get; set; }
    public long RequestContentLength { get; set; }
    public string RequestHeadersText { get; set; } = string.Empty;
    public string ResponseHeadersText { get; set; } = string.Empty;
    public string RequestBodyDisplay { get; set; } = string.Empty;
    public string ResponseBody { get; set; } = string.Empty;
    public VisionResultView? VisionResult { get; set; }
    public bool IsRunning { get; set; }
    public bool IsComplete { get; set; }
    public bool IsFailed { get; set; }

    /// <summary>
    /// EventHub-only: when the request was enqueued (the enqueue event's "Date" field). Used to
    /// display a live, increasing elapsed time for requests that are still running (enqueued but
    /// not yet finalized).
    /// </summary>
    public DateTimeOffset? EnqueuedAtUtc { get; set; }

    /// <summary>
    /// EventHub-only: when ChatTester processed the final request event. Trend charts use this
    /// local observation time so a source timestamp with clock skew cannot delay a new result.
    /// </summary>
    public DateTimeOffset? FinalizedAtUtc { get; set; }

    /// <summary>
    /// EventHub-only: structured per-phase (enqueue / attempts / final) field capture keyed by
    /// S7P-ID. Null for requests that don't originate from the EventHub monitor.
    /// </summary>
    public EventHub.RequestPhaseView? Phases { get; set; }

    /// <summary>EventHub-only: request path used for the Paths card aggregation.</summary>
    public string? Path { get; set; }

    /// <summary>EventHub-only: user id used for the Users card aggregation.</summary>
    public string? UserId { get; set; }

    /// <summary>EventHub-only: backend attempts used for the Endpoints card aggregation.</summary>
    public IReadOnlyList<BackendCallRecord> BackendCalls { get; set; } = Array.Empty<BackendCallRecord>();
}