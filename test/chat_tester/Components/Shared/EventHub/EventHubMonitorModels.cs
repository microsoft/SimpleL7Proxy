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

/// <summary>
/// Immutable point-in-time view the UI renders from. Produced by
/// <see cref="EventHubMonitorStore.GetSnapshot"/>.
/// </summary>
public sealed record MonitorSnapshot
{
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool HasData { get; init; }
    public RuntimeStatsSnapshot Stats { get; init; } = new();
    public IReadOnlyList<BackendHealthSnapshot> Backends { get; init; } = Array.Empty<BackendHealthSnapshot>();
    public IReadOnlyList<MultiRequestStatusItem> Requests { get; init; } = Array.Empty<MultiRequestStatusItem>();
}
