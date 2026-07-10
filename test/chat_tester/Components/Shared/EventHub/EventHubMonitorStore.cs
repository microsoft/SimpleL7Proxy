namespace chat_tester.Components.Shared;

/// <summary>
/// Server-side singleton that holds live Event Hub monitor state. The Event Hub reader
/// (added later, top-down) pushes updates in through the <c>Update*</c>/<c>Add*</c> methods.
/// Blazor circuits subscribe to <see cref="Changed"/> and pull an immutable
/// <see cref="MonitorSnapshot"/> on their own cadence (typically every few seconds) rather
/// than re-rendering on every event.
///
/// Backend health and fleet info reflect only the latest reported values. Requests are
/// retained in memory for <see cref="RequestRetention"/> and then purged.
/// </summary>
public sealed class EventHubMonitorStore
{
    /// <summary>How long individual request records are kept before being purged.</summary>
    public static readonly TimeSpan RequestRetention = TimeSpan.FromHours(1);

    /// <summary>Trailing window used to compute requests-per-second.</summary>
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time;
    private readonly object _gate = new();

    // Requests are stored oldest-first so purging removes from the front.
    private readonly LinkedList<TimedRequest> _requests = new();

    private IReadOnlyList<BackendHealthSnapshot> _backends = Array.Empty<BackendHealthSnapshot>();
    private FleetInfoSnapshot _fleet = new();
    private int _requestCounter;
    private bool _hasData;

    public EventHubMonitorStore(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Raised whenever any state changes. Handlers must be cheap and thread-safe; the UI
    /// uses this only as a signal that fresh data exists, not as a render trigger.
    /// </summary>
    public event Action? Changed;

    private readonly record struct TimedRequest(DateTimeOffset ReceivedUtc, MultiRequestStatusItem Item);

    // ── Ingest API (called by the server-side Event Hub reader) ──

    /// <summary>Replaces the current backend health list with the latest reported values.</summary>
    public void UpdateBackends(IEnumerable<BackendHealthSnapshot> backends)
    {
        ArgumentNullException.ThrowIfNull(backends);
        lock (_gate)
        {
            _backends = backends.ToArray();
            _hasData = true;
        }
        Changed?.Invoke();
    }

    /// <summary>Replaces the current fleet-level information with the latest reported values.</summary>
    public void UpdateFleet(FleetInfoSnapshot fleet)
    {
        ArgumentNullException.ThrowIfNull(fleet);
        lock (_gate)
        {
            _fleet = fleet;
            _hasData = true;
        }
        Changed?.Invoke();
    }

    /// <summary>Appends a completed request. A store-managed sequence number is assigned.</summary>
    public void AddRequest(MultiRequestStatusItem request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            request.RequestNumber = ++_requestCounter;
            _requests.AddLast(new TimedRequest(now, request));
            PurgeExpired(now);
            _hasData = true;
        }
        Changed?.Invoke();
    }

    /// <summary>Appends multiple requests in order, raising a single change notification.</summary>
    public void AddRequests(IEnumerable<MultiRequestStatusItem> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var request in requests)
            {
                request.RequestNumber = ++_requestCounter;
                _requests.AddLast(new TimedRequest(now, request));
            }
            PurgeExpired(now);
            _hasData = true;
        }
        Changed?.Invoke();
    }

    /// <summary>Clears all buffered state.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _requests.Clear();
            _backends = Array.Empty<BackendHealthSnapshot>();
            _fleet = new FleetInfoSnapshot();
            _requestCounter = 0;
            _hasData = false;
        }
        Changed?.Invoke();
    }

    // ── Snapshot API (called by the UI) ──

    /// <summary>Produces an immutable snapshot of the current state, purging expired requests first.</summary>
    public MonitorSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            PurgeExpired(now);

            var requests = new MultiRequestStatusItem[_requests.Count];
            var windowStart = now - RateWindow;
            var succeeded = 0;
            var failed = 0;
            var decided = 0;
            var latencyCount = 0;
            var latencyTotalMs = 0.0;
            var inWindow = 0;

            var index = 0;
            foreach (var timed in _requests)
            {
                requests[index++] = timed.Item;

                if (timed.Item.StatusCode is { } status)
                {
                    decided++;
                    if (status is >= 200 and < 300)
                    {
                        succeeded++;
                    }
                    else
                    {
                        failed++;
                    }
                }

                if (timed.Item.Duration is { } duration)
                {
                    latencyTotalMs += duration.TotalMilliseconds;
                    latencyCount++;
                }

                if (timed.ReceivedUtc >= windowStart)
                {
                    inWindow++;
                }
            }

            var stats = new RuntimeStatsSnapshot
            {
                TotalRequests = _requests.Count,
                RequestsPerSecond = inWindow / RateWindow.TotalSeconds,
                Failed = failed,
                SuccessRate = decided == 0 ? 0 : succeeded * 100.0 / decided,
                AvgLatencyMs = latencyCount == 0 ? 0 : latencyTotalMs / latencyCount,
                ActiveHosts = _fleet.ActiveHosts,
                TotalHosts = _fleet.TotalHosts,
                BackendProbeLatencyMs = _fleet.ProbeLatencyMs,
                LoadBalancingMode = _fleet.LoadBalancingMode,
                PrimaryBackend = _fleet.PrimaryBackend,
                ProxyVersion = _fleet.ProxyVersion,
            };

            return new MonitorSnapshot
            {
                TimestampUtc = now,
                HasData = _hasData,
                Stats = stats,
                Backends = _backends,
                Requests = requests,
            };
        }
    }

    // Requires the lock to be held. Oldest requests sit at the front of the list.
    private void PurgeExpired(DateTimeOffset now)
    {
        var cutoff = now - RequestRetention;
        while (_requests.First is { } first && first.Value.ReceivedUtc < cutoff)
        {
            _requests.RemoveFirst();
        }
    }
}
