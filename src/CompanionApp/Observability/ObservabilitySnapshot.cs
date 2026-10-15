using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using CompanionApp.Components.Shared;
using CompanionApp.Components.Shared.EventHub;

namespace CompanionApp.Observability;

/// <summary>One immutable publication shared by the monitor and Insights circuits.</summary>
public sealed record ObservabilitySnapshot {
    public long Version { get; init; }
    public DateTimeOffset PublishedUtc { get; init; }
    public DateTimeOffset LastDataReceivedUtc { get; init; }
    public DateTimeOffset WindowStartUtc { get; init; }
    public DateTimeOffset WindowEndUtc { get; init; }
    public bool IsReplay { get; init; }
    public bool RetentionTruncated { get; init; }
    public ObservabilityMonitorSnapshot Monitor { get; init; } = new();
    public ImmutableArray<ProxyMetricsCatalog.ScopeOption> Scopes { get; init; } = [];
    public ImmutableDictionary<string, ImmutableArray<ProxyMetricsCatalog.MetricGroupSnapshot>> MetricGroupsByScope { get; init; } =
        ImmutableDictionary<string, ImmutableArray<ProxyMetricsCatalog.MetricGroupSnapshot>>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns metric groups from this publication, preserving the catalog's all-scope fallback.</summary>
    public IReadOnlyList<ProxyMetricsCatalog.MetricGroupSnapshot> GetActiveGroups(string? scopeId) {
        if (scopeId is not null && MetricGroupsByScope.TryGetValue(scopeId, out var groups)) return groups;
        return MetricGroupsByScope.GetValueOrDefault("all", []);
    }

    /// <summary>Checks a scope against this publication rather than mutable ingestion state.</summary>
    public bool IsKnownScope(string? scopeId) =>
        string.IsNullOrWhiteSpace(scopeId)
        || string.Equals(scopeId, "all", StringComparison.OrdinalIgnoreCase)
        || MetricGroupsByScope.ContainsKey(scopeId);
}

/// <summary>Frozen monitor state; no request object is shared with the ingestion writer.</summary>
public sealed record ObservabilityMonitorSnapshot {
    public DateTimeOffset TimestampUtc { get; init; }
    public DateTimeOffset LastDataUtc { get; init; }
    public bool HasData { get; init; }
    public bool HasUserInfo { get; init; }
    public bool HasModelInfo { get; init; }
    public RuntimeStatsSnapshot Stats { get; init; } = new();
    public CircuitBreakerSnapshot CircuitBreaker { get; init; } = new();
    public ServerErrorSnapshot ServerErrors { get; init; } = new();
    public ImmutableArray<BackendHealthSnapshot> Backends { get; init; } = [];
    public ImmutableArray<ObservedRequestSnapshot> Requests { get; init; } = [];
    public ImmutableArray<EndpointStat> Endpoints { get; init; } = [];
    public ImmutableArray<PathCount> SuccessfulPaths { get; init; } = [];
    public ImmutableArray<PathCount> FailedPaths { get; init; } = [];
    public ImmutableArray<UserCount> SuccessfulUsers { get; init; } = [];
    public ImmutableArray<UserCount> FailedUsers { get; init; } = [];

    /// <summary>Creates detached rows for the existing request inspector and view projections.</summary>
    public MonitorSnapshot ToMonitorSnapshot() => new() {
        TimestampUtc = TimestampUtc,
        LastDataUtc = LastDataUtc,
        HasData = HasData,
        HasUserInfo = HasUserInfo,
        HasModelInfo = HasModelInfo,
        Stats = Stats,
        CircuitBreaker = CircuitBreaker,
        ServerErrors = ServerErrors,
        Backends = Backends,
        Requests = Requests.Select(request => request.ToStatusItem()).ToImmutableArray(),
        Endpoints = Endpoints,
        SuccessfulPaths = SuccessfulPaths,
        FailedPaths = FailedPaths,
        SuccessfulUsers = SuccessfulUsers,
        FailedUsers = FailedUsers,
    };
}

/// <summary>Immutable enqueue, attempt and final fields for a captured request lifecycle.</summary>
public sealed record ObservedRequestPhases {
    public string SevenPId { get; init; } = string.Empty;
    public ImmutableArray<KeyValuePair<string, string>>? Enqueue { get; init; }
    public ImmutableArray<ImmutableArray<KeyValuePair<string, string>>> Attempts { get; init; } = [];
    public ImmutableArray<KeyValuePair<string, string>>? Final { get; init; }
    public string? BackendLog { get; init; }

    internal static ObservedRequestPhases Capture(RequestPhaseView source) => new() {
        SevenPId = source.SevenPId,
        Enqueue = source.Enqueue?.ToImmutableArray(),
        Attempts = source.Attempts.Select(attempt => attempt.ToImmutableArray()).ToImmutableArray(),
        Final = source.Final?.ToImmutableArray(),
        BackendLog = source.BackendLog,
    };

    internal RequestPhaseView ToPhaseView() => new() {
        SevenPId = SevenPId,
        Enqueue = Enqueue is { } enqueue ? enqueue : null,
        Attempts = Attempts.Select(attempt => (IReadOnlyList<KeyValuePair<string, string>>)attempt).ToImmutableArray(),
        Final = Final is { } final ? final : null,
        BackendLog = BackendLog,
    };
}

/// <summary>Immutable request data, including normalized usage and frozen diagnostics.</summary>
public sealed record ObservedRequestSnapshot {
    private static readonly JsonSerializerOptions VisionJsonOptions = new() {
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
    };

    public int RequestNumber { get; init; }
    public string ContainerApp { get; init; } = string.Empty;
    public string Replica { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string StatusMessage { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string BackendHost { get; init; } = string.Empty;
    public string EndpointKey { get; init; } = string.Empty;
    public bool IsEndpointCircuitBreakerOpen { get; init; }
    public bool IsServerCircuitBreakerSignal { get; init; }
    public int? StatusCode { get; init; }
    public string ContentType { get; init; } = string.Empty;
    public TimeSpan? TimeToFirstByte { get; init; }
    public TimeSpan? Duration { get; init; }
    public int Chunks { get; init; }
    public long TotalBytes { get; init; }
    public long RequestContentLength { get; init; }
    public string RequestHeadersText { get; init; } = string.Empty;
    public string ResponseHeadersText { get; init; } = string.Empty;
    public string RequestBodyDisplay { get; init; } = string.Empty;
    public string ResponseBody { get; init; } = string.Empty;
    public string? VisionResultJson { get; init; }
    public bool IsRunning { get; init; }
    public bool IsComplete { get; init; }
    public bool IsFailed { get; init; }
    public DateTimeOffset? EnqueuedAtUtc { get; init; }
    public DateTimeOffset? FinalizedAtUtc { get; init; }
    public ObservedRequestPhases? Phases { get; init; }
    public string? Path { get; init; }
    public string? UserId { get; init; }
    public ImmutableArray<BackendCallRecord> BackendCalls { get; init; } = [];
    public ImmutableDictionary<string, long> UsageMetrics { get; init; } =
        ImmutableDictionary<string, long>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
    public bool HasUsage => UsageMetrics.Count > 0;

    internal static ObservedRequestSnapshot Capture(MultiRequestStatusItem source, ImmutableDictionary<string, long> usage) => new() {
        RequestNumber = source.RequestNumber,
        ContainerApp = source.ContainerApp,
        Replica = source.Replica,
        Status = source.Status,
        StatusMessage = source.StatusMessage,
        EventType = source.EventType,
        BackendHost = source.BackendHost,
        EndpointKey = source.EndpointKey,
        IsEndpointCircuitBreakerOpen = source.IsEndpointCircuitBreakerOpen,
        IsServerCircuitBreakerSignal = source.IsServerCircuitBreakerSignal,
        StatusCode = source.StatusCode,
        ContentType = source.ContentType,
        TimeToFirstByte = source.TimeToFirstByte,
        Duration = source.Duration,
        Chunks = source.Chunks,
        TotalBytes = source.TotalBytes,
        RequestContentLength = source.RequestContentLength,
        RequestHeadersText = source.RequestHeadersText,
        ResponseHeadersText = source.ResponseHeadersText,
        RequestBodyDisplay = source.RequestBodyDisplay,
        ResponseBody = source.ResponseBody,
        VisionResultJson = source.VisionResult is null ? null : JsonSerializer.Serialize(source.VisionResult),
        IsRunning = source.IsRunning,
        IsComplete = source.IsComplete,
        IsFailed = source.IsFailed,
        EnqueuedAtUtc = source.EnqueuedAtUtc,
        FinalizedAtUtc = source.FinalizedAtUtc,
        Phases = source.Phases is null ? null : ObservedRequestPhases.Capture(source.Phases),
        Path = source.Path,
        UserId = source.UserId,
        BackendCalls = source.BackendCalls.ToImmutableArray(),
        UsageMetrics = usage,
    };

    /// <summary>Creates a circuit-owned inspector row without exposing writer-owned mutable data.</summary>
    public MultiRequestStatusItem ToStatusItem() => new() {
        RequestNumber = RequestNumber,
        ContainerApp = ContainerApp,
        Replica = Replica,
        Status = Status,
        StatusMessage = StatusMessage,
        EventType = EventType,
        BackendHost = BackendHost,
        EndpointKey = EndpointKey,
        IsEndpointCircuitBreakerOpen = IsEndpointCircuitBreakerOpen,
        IsServerCircuitBreakerSignal = IsServerCircuitBreakerSignal,
        StatusCode = StatusCode,
        ContentType = ContentType,
        TimeToFirstByte = TimeToFirstByte,
        Duration = Duration,
        Chunks = Chunks,
        TotalBytes = TotalBytes,
        RequestContentLength = RequestContentLength,
        RequestHeadersText = RequestHeadersText,
        ResponseHeadersText = ResponseHeadersText,
        RequestBodyDisplay = RequestBodyDisplay,
        ResponseBody = ResponseBody,
        VisionResult = VisionResultJson is null ? null : JsonSerializer.Deserialize<VisionResultView>(VisionResultJson, VisionJsonOptions),
        IsRunning = IsRunning,
        IsComplete = IsComplete,
        IsFailed = IsFailed,
        EnqueuedAtUtc = EnqueuedAtUtc,
        FinalizedAtUtc = FinalizedAtUtc,
        Phases = Phases?.ToPhaseView(),
        Path = Path,
        UserId = UserId,
        BackendCalls = BackendCalls,
        ObservedUsageMetrics = UsageMetrics,
    };
}
