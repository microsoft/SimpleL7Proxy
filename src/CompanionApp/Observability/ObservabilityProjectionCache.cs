using System.Runtime.CompilerServices;
using CompanionApp.Components.Shared;

namespace CompanionApp.Observability;

/// <summary>Shares eagerly prepared analytics across circuits for the lifetime of a published snapshot.</summary>
public sealed class ObservabilityProjectionCache
{
    private static readonly ConditionalWeakTable<ObservabilitySnapshot, Lazy<ObservabilityProjectionCache>> Publications = new();
    private readonly object _gate = new();
    private readonly ObservabilitySnapshot _publication;
    private readonly MonitorSnapshot _analyticsSnapshot;
    private readonly IReadOnlyDictionary<MultiRequestStatusItem, ObservedRequestSnapshot> _sourceRequests;
    private readonly Dictionary<(string Scope, string Environment, string User, string Model), ObservabilityRequestProjection> _requests = new();
    private readonly Dictionary<(ObservabilityRequestProjection Requests, string ShareMetric, string TrendMetrics), InsightsProjection> _insights = new();

    private ObservabilityProjectionCache(ObservabilitySnapshot publication)
    {
        _publication = publication;
        _analyticsSnapshot = publication.Monitor.ToMonitorSnapshot();
        _sourceRequests = _analyticsSnapshot.Requests
            .Select((request, index) => (Request: request, Source: publication.Monitor.Requests[index]))
            .ToDictionary(pair => pair.Request, pair => pair.Source);
    }

    /// <summary>Returns the cache associated with exactly one immutable publication.</summary>
    public static ObservabilityProjectionCache For(ObservabilitySnapshot publication) => Publications.GetValue(publication, value => new(() => new(value))).Value;

    /// <summary>Frozen source data; no circuit-owned mutable inspector row is stored in the public cache.</summary>
    public ObservabilityMonitorSnapshot Snapshot => _publication.Monitor;

    /// <summary>Materializes detached inspector rows once for a circuit receiving this publication.</summary>
    public ObservabilityCircuitView CreateCircuitView() => new(_publication.Monitor);

    /// <summary>Returns prepared endpoint, path, user, and filter projections for the supplied query.</summary>
    public ObservabilityRequestProjection GetRequests(string scope, string environment, string user, string model)
    {
        lock (_gate)
        {
            var key = (scope, environment, user, model);
            if (!_requests.TryGetValue(key, out var projection))
            {
                // Bound arbitrary user queries while retaining currently displayed projections.
                if (_requests.Count >= 64)
                {
                    _requests.Clear();
                    _insights.Clear();
                }

                projection = new(_analyticsSnapshot, _publication.Monitor, _sourceRequests, _publication.Scopes, scope, environment, user, model);
                _requests.Add(key, projection);
            }

            return projection;
        }
    }

    /// <summary>Returns prepared usage charts for the request query and current chart selections.</summary>
    public InsightsProjection GetInsights(ObservabilityRequestProjection requests, string shareMetric, IEnumerable<string> trendMetrics)
    {
        var selected = trendMetrics.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray();
        var key = (requests, shareMetric, string.Join('\0', selected));
        lock (_gate)
        {
            if (!_insights.TryGetValue(key, out var projection))
            {
                if (_insights.Count >= 128)
                {
                    _insights.Clear();
                }

                projection = new(requests, _publication.PublishedUtc, shareMetric, selected);
                _insights.Add(key, projection);
            }

            return projection;
        }
    }
}
