using CompanionApp.Components.Shared;

namespace CompanionApp.Observability;

/// <summary>Owns one circuit's mutable inspector rows; shared projections expose only frozen source requests.</summary>
public sealed class ObservabilityCircuitView
{
    private readonly Dictionary<ObservedRequestSnapshot, MultiRequestStatusItem> _rows = new(ReferenceEqualityComparer.Instance);

    internal ObservabilityCircuitView(ObservabilityMonitorSnapshot source)
    {
        Snapshot = source.ToMonitorSnapshot();
        for (var index = 0; index < source.Requests.Length; index++)
        {
            _rows.Add(source.Requests[index], Snapshot.Requests[index]);
        }
    }

    /// <summary>The detached monitor and inspector rows owned by this circuit.</summary>
    public MonitorSnapshot Snapshot { get; }

    /// <summary>Binds a cached immutable query or drilldown to this circuit's existing rows without reparsing or copying them.</summary>
    public IReadOnlyList<MultiRequestStatusItem> SelectRows(IEnumerable<ObservedRequestSnapshot> requests) =>
        requests.Select(request => _rows[request]).ToArray();
}
