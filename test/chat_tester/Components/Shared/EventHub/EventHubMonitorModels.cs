namespace chat_tester.Components.Shared;

/// <summary>
/// Health snapshot for a single backend host, sourced from the S7P-Backend event
/// (fields "N-Host", "N-Status", "N-Latency", "N-SuccessRate", "N-Calls", "N-Errors").
/// </summary>
public sealed record BackendHealthSnapshot
{
    public string HostKey { get; init; } = string.Empty;
    public required string Name { get; init; }
    public string Url { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public double LatencyMs { get; init; }
    public int SuccessRate { get; init; }
    public int Calls { get; init; }
    public int Errors { get; init; }
    public int ProbeSuccesses { get; init; }
    public int ProbeFailures { get; init; }
    public int RequestCalls { get; init; }
    public int RequestSuccesses { get; init; }
    public int RequestFailures { get; init; }
    public double AvgRequestLatencyMs { get; init; }

    /// <summary>UI status class, e.g. "healthy" or "degraded".</summary>
    public string Css { get; init; } = "healthy";

    public string LatencyText => $"{LatencyMs:0} ms";
}

/// <summary>
/// Fleet-level information sourced from the S7P-Backend host event: active host counts,
/// probe latency, load balancing mode, and the reporting proxy version.
/// </summary>
public sealed record FleetInfoSnapshot
{
    public int ActiveHosts { get; init; }
    public int TotalHosts { get; init; }
    public double ProbeLatencyMs { get; init; }
    public string LoadBalancingMode { get; init; } = "latency";
    public string PrimaryBackend { get; init; } = string.Empty;
    public string ProxyVersion { get; init; } = string.Empty;
}

/// <summary>Aggregate runtime statistics derived from the buffered request stream and fleet info.</summary>
public sealed record RuntimeStatsSnapshot
{
    public int TotalRequests { get; init; }
    public double RequestsPerSecond { get; init; }
    public int Failed { get; init; }
    public double SuccessRate { get; init; }
    public double AvgLatencyMs { get; init; }
    public int EnqueuedCount { get; init; }
    public int ProcessingCount { get; init; }
    public int CompletedCount { get; init; }
    public double AverageRequestSizeBytes { get; init; }
    public int ActiveHosts { get; init; }
    public int TotalHosts { get; init; }
    public double BackendProbeLatencyMs { get; init; }
    public string LoadBalancingMode { get; init; } = "latency";
    public string PrimaryBackend { get; init; } = string.Empty;
    public string ProxyVersion { get; init; } = string.Empty;
    public bool ServerCircuitBreakerOpen { get; init; }
    public int EndpointCircuitBreakerOpenCount { get; init; }
    public int EndpointCount { get; init; }
}

/// <summary>Circuit breaker issue tracking which backends are affected.</summary>
public sealed record CircuitBreakerIssue
{
    public string BackendHost { get; init; } = string.Empty;
    public int ErrorCode { get; init; }
    public int OccurrenceCount { get; init; }
    public DateTimeOffset LastOccurrenceUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ErrorDescription => ErrorCode switch
    {
        408 => "Timeout",
        500 => "Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        _ => $"HTTP {ErrorCode}"
    };
}

/// <summary>Circuit breaker state snapshot.</summary>
public sealed record CircuitBreakerSnapshot
{
    public bool IsOpen { get; init; }
    public string Scope { get; init; } = "server + endpoint";
    public DateTimeOffset LastTriggeredUtc { get; init; } = DateTimeOffset.UtcNow;
    public int ServerEventCount { get; init; }
    public int? LastErrorCode { get; init; }
    public IReadOnlyList<CircuitBreakerIssue> BackendIssues { get; init; } = Array.Empty<CircuitBreakerIssue>();
    public int EndpointCircuitBreakerOpenCount { get; init; }
    public int EndpointCircuitBreakerTotalCount { get; init; }
}

/// <summary>Top rejected path with occurrence count.</summary>
public sealed record ServerPathCount
{
    public string Path { get; init; } = string.Empty;
    public int Count { get; init; }
}

/// <summary>Server-side rejected request aggregate from S7P-ServerError events.</summary>
public sealed record ServerErrorSnapshot
{
    public int RejectedRequests { get; init; }
    public int NotAuthorized403Count { get; init; }
    public int LatestQueueLength { get; init; }
    public int MaxQueueLength { get; init; }
    public IReadOnlyList<ServerPathCount> TopPaths { get; init; } = Array.Empty<ServerPathCount>();
    public int EnqueueAttempts { get; init; }
    public int EnqueueSuccess { get; init; }
    public int EnqueueFailed { get; init; }
    public double EnqueueSuccessRate { get; init; }
    public int LastEnqueueQueueLength { get; init; }
    public int LastEnqueueActiveHosts { get; init; }
    public IReadOnlyList<ServerPathCount> TopEnqueuePaths { get; init; } = Array.Empty<ServerPathCount>();
}

/// <summary>
/// Immutable point-in-time view the UI renders from. Produced by
/// <see cref="EventHubMonitorStore.GetSnapshot"/>.
/// </summary>
public sealed record MonitorSnapshot
{
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastDataUtc { get; init; }
    public bool HasData { get; init; }
    public RuntimeStatsSnapshot Stats { get; init; } = new();
    public CircuitBreakerSnapshot CircuitBreaker { get; init; } = new();
    public ServerErrorSnapshot ServerErrors { get; init; } = new();
    public IReadOnlyList<BackendHealthSnapshot> Backends { get; init; } = Array.Empty<BackendHealthSnapshot>();
    public IReadOnlyList<MultiRequestStatusItem> Requests { get; init; } = Array.Empty<MultiRequestStatusItem>();

    /// <summary>Per-target endpoint aggregates derived from backend-call logs. Top 10 by call count.</summary>
    public IReadOnlyList<EndpointStat> Endpoints { get; init; } = Array.Empty<EndpointStat>();

    /// <summary>Top successful/failed request paths (success = HTTP [200,400)). Top 10 each.</summary>
    public IReadOnlyList<PathCount> SuccessfulPaths { get; init; } = Array.Empty<PathCount>();
    public IReadOnlyList<PathCount> FailedPaths { get; init; } = Array.Empty<PathCount>();

    /// <summary>Top successful/failed users (success = HTTP [200,400)). Top 10 each.</summary>
    public IReadOnlyList<UserCount> SuccessfulUsers { get; init; } = Array.Empty<UserCount>();
    public IReadOnlyList<UserCount> FailedUsers { get; init; } = Array.Empty<UserCount>();
}

/// <summary>
/// A single backend attempt extracted from a backend log. Attached to the originating request
/// item so the store can aggregate the Endpoints card from structured data (no header-text parsing).
/// </summary>
public sealed record BackendCallRecord
{
    public required string TargetName { get; init; }
    public string TargetUrl { get; init; } = string.Empty;
    /// <summary>The exact backend-log string; used to dedupe an attempt echoed by the proxy summary.</summary>
    public required string BackendLog { get; init; }
    public double? PolicyCycleCounter { get; init; }
    public double? RequestProcessDuration { get; init; }
    public double? QueueDurationMs { get; init; }
    public double? ProcessDurationMs { get; init; }
    public bool Throttled { get; init; }
    public bool TargetThrottled { get; init; }
    public bool CallIncomplete { get; init; }
    public bool ExhaustedRetry { get; init; }
    public int? StatusCode { get; init; }
    public string BackendHost { get; init; } = string.Empty;
}

/// <summary>Per-target endpoint aggregate rendered by the Endpoints card.</summary>
public sealed record EndpointStat(
    string Path,
    int Calls,
    int Failures,
    double AverageEndpointAttempts,
    double AverageRequestProcessDuration,
    double AverageQueueDurationMs,
    double AverageProcessDurationMs,
    int ThrottledEvents,
    int TargetThrottledEvents,
    int CallIncompleteEvents,
    int ExhaustedRetryEvents,
    string TargetUrl,
    string Status,
    string Css);

/// <summary>A request path and its occurrence count.</summary>
public sealed record PathCount(string Path, int Count);

/// <summary>A user and their request count.</summary>
public sealed record UserCount(string User, int Count);
