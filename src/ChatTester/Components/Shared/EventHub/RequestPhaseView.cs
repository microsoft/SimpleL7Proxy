namespace chat_tester.Components.Shared.EventHub;

/// <summary>
/// Structured, per-phase view of a single request's EventHub records, keyed by S7P-ID.
/// Consumed by the EventHub-specific request popup to render the enqueue tab, one tab per
/// backend attempt, and the final proxy-request tab.
/// </summary>
public sealed class RequestPhaseView
{
    public string SevenPId { get; init; } = string.Empty;

    /// <summary>Fields from the S7P-ProxyRequestEnqueued record (null if none was seen).</summary>
    public IReadOnlyList<KeyValuePair<string, string>>? Enqueue { get; init; }

    /// <summary>Fields from each S7P-BackendRequest record, in attempt order.</summary>
    public IReadOnlyList<IReadOnlyList<KeyValuePair<string, string>>> Attempts { get; init; }
        = Array.Empty<IReadOnlyList<KeyValuePair<string, string>>>();

    /// <summary>Fields from the final S7P-ProxyRequest record (null if none was seen).</summary>
    public IReadOnlyList<KeyValuePair<string, string>>? Final { get; init; }

    /// <summary>The final backendLog string (from the proxy request or its last attempt), if any.</summary>
    public string? BackendLog { get; init; }
}
