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
    private DateTimeOffset _lastDataUtc;
    private bool _serverCircuitBreakerOpen;
    private DateTimeOffset _circuitBreakerLastTriggered = DateTimeOffset.UtcNow;
    private int _serverCircuitBreakerEventCount;
    private int? _lastCircuitBreakerErrorCode;
    private readonly Dictionary<string, CircuitBreakerIssue> _backendCircuitBreakerIssues = new(StringComparer.OrdinalIgnoreCase);
    private int _serverRejectedRequests;
    private int _serverNotAuthorized403Count;
    private int _latestServerQueueLength;
    private int _maxServerQueueLength;
    private readonly Dictionary<string, int> _serverPathCounts = new(StringComparer.OrdinalIgnoreCase);
    private int _enqueueAttempts;
    private int _enqueueSuccess;
    private int _enqueueFailed;
    private int _lastEnqueueQueueLength;
    private int _lastEnqueueActiveHosts;
    private readonly Dictionary<string, int> _enqueuePathCounts = new(StringComparer.OrdinalIgnoreCase);

    public bool DisableRequestAging { get; set; }

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

    // Stamps the moment fresh data last arrived so the UI can tell a live feed from a stale one.
    private void MarkDataReceived()
    {
        _hasData = true;
        _lastDataUtc = _time.GetUtcNow();
    }

    // ── Ingest API (called by the server-side Event Hub reader) ──

    /// <summary>Replaces the current backend health list with the latest reported values.</summary>
    public void UpdateBackends(IEnumerable<BackendHealthSnapshot> backends)
    {
        ArgumentNullException.ThrowIfNull(backends);
        lock (_gate)
        {
            _backends = backends.ToArray();
            MarkDataReceived();
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
            MarkDataReceived();
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
            UpdateServerCircuitBreakerState(request);
            if (!DisableRequestAging)
            {
                PurgeExpired(now);
            }
            MarkDataReceived();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Marks a previously-added, still-running request (see <see cref="AddRequest"/>) as finalized.
    /// The caller MUST have already mutated the request's fields (status, duration, etc.) in place;
    /// this only refreshes circuit-breaker/data-received bookkeeping and notifies subscribers. The
    /// request keeps its original position and <see cref="MultiRequestStatusItem.RequestNumber"/>.
    /// </summary>
    public void MarkRequestFinalized(MultiRequestStatusItem request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            UpdateServerCircuitBreakerState(request);
            MarkDataReceived();
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
                UpdateServerCircuitBreakerState(request);
            }
            if (!DisableRequestAging)
            {
                PurgeExpired(now);
            }
            MarkDataReceived();
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
            _serverCircuitBreakerOpen = false;
            _serverCircuitBreakerEventCount = 0;
            _lastCircuitBreakerErrorCode = null;
            _backendCircuitBreakerIssues.Clear();
            _serverRejectedRequests = 0;
            _serverNotAuthorized403Count = 0;
            _latestServerQueueLength = 0;
            _maxServerQueueLength = 0;
            _serverPathCounts.Clear();
            _enqueueAttempts = 0;
            _enqueueSuccess = 0;
            _enqueueFailed = 0;
            _lastEnqueueQueueLength = 0;
            _lastEnqueueActiveHosts = 0;
            _enqueuePathCounts.Clear();
        }
        Changed?.Invoke();
    }

    /// <summary>Records a message enqueue success (S7P-ProxyRequestEnqueued).</summary>
    public void RecordEnqueueSuccess(int? queueLength, int? activeHosts, string? path)
    {
        lock (_gate)
        {
            _enqueueAttempts++;
            _enqueueSuccess++;

            if (queueLength is { } queue)
            {
                _lastEnqueueQueueLength = queue;
            }

            if (activeHosts is { } active)
            {
                _lastEnqueueActiveHosts = active;
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                var key = path.Trim();
                _enqueuePathCounts[key] = _enqueuePathCounts.TryGetValue(key, out var count)
                    ? count + 1
                    : 1;
            }

            MarkDataReceived();
        }

        Changed?.Invoke();
    }

    /// <summary>Records a server-side rejection summary from S7P-ServerError.</summary>
    public void RecordServerErrorEvent(
        int? statusCode,
        string? message,
        int? queueLength,
        string? path)
    {
        lock (_gate)
        {
            _serverRejectedRequests++;
            _enqueueAttempts++;
            _enqueueFailed++;

            if (statusCode == 403
                && !string.IsNullOrWhiteSpace(message)
                && message.Contains("Not Authorized", StringComparison.OrdinalIgnoreCase))
            {
                _serverNotAuthorized403Count++;
            }

            if (queueLength is { } queue)
            {
                _latestServerQueueLength = queue;
                _maxServerQueueLength = Math.Max(_maxServerQueueLength, queue);
                _lastEnqueueQueueLength = queue;
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                var key = path.Trim();
                _serverPathCounts[key] = _serverPathCounts.TryGetValue(key, out var count)
                    ? count + 1
                    : 1;
                _enqueuePathCounts[key] = _enqueuePathCounts.TryGetValue(key, out var enqueueCount)
                    ? enqueueCount + 1
                    : 1;
            }

            MarkDataReceived();
        }

        Changed?.Invoke();
    }

    public void MarkServerCircuitBreakerSignal()
    {
        lock (_gate)
        {
            _serverCircuitBreakerOpen = true;
            MarkDataReceived();
        }

        Changed?.Invoke();
    }

    /// <summary>Records a server-level circuit breaker event in history.</summary>
    public void RecordServerCircuitBreakerEvent(int errorCode, int? reportedCount = null)
    {
        lock (_gate)
        {
            _serverCircuitBreakerEventCount = reportedCount is > 0
                ? Math.Max(_serverCircuitBreakerEventCount, reportedCount.Value)
                : _serverCircuitBreakerEventCount + 1;
            _lastCircuitBreakerErrorCode = errorCode;
            _circuitBreakerLastTriggered = _time.GetUtcNow();
            MarkDataReceived();
        }

        Changed?.Invoke();
    }

    /// <summary>Records a circuit breaker issue for a specific backend.</summary>
    public void RecordCircuitBreakerIssue(string backendHost, int errorCode)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(backendHost))
            {
                var key = backendHost.ToLowerInvariant();
                if (_backendCircuitBreakerIssues.TryGetValue(key, out var existing))
                {
                    _backendCircuitBreakerIssues[key] = existing with
                    {
                        OccurrenceCount = existing.OccurrenceCount + 1,
                        LastOccurrenceUtc = _time.GetUtcNow(),
                        ErrorCode = errorCode
                    };
                }
                else
                {
                    _backendCircuitBreakerIssues[key] = new CircuitBreakerIssue
                    {
                        BackendHost = backendHost,
                        ErrorCode = errorCode,
                        OccurrenceCount = 1,
                        LastOccurrenceUtc = _time.GetUtcNow()
                    };
                }
                _circuitBreakerLastTriggered = _time.GetUtcNow();
            }
            MarkDataReceived();
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
            if (!DisableRequestAging)
            {
                PurgeExpired(now);
            }

            var requests = new MultiRequestStatusItem[_requests.Count];
            var windowStart = now - RateWindow;
            var succeeded = 0;
            var failed = 0;
            var decided = 0;
            var latencyCount = 0;
            var latencyTotalMs = 0.0;
            var inWindow = 0;
            var nonBackendRequestCount = 0;
            var requestSizeTotal = 0L;
            var requestSizeCount = 0;
            var latestEndpointStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var backendRequestStats = new Dictionary<string, BackendRequestAggregate>(StringComparer.OrdinalIgnoreCase);
            var hasUserInfo = false;
            var hasModelInfo = false;

            var index = 0;
            foreach (var timed in _requests)
            {
                requests[index++] = timed.Item;

                if (!hasUserInfo)
                {
                    hasUserInfo = !string.IsNullOrWhiteSpace(timed.Item.UserId)
                        || timed.Item.RequestHeadersText.Contains("UserID:", StringComparison.OrdinalIgnoreCase);
                }

                if (!hasModelInfo)
                {
                    hasModelInfo = timed.Item.RequestHeadersText.Contains("Model:", StringComparison.OrdinalIgnoreCase)
                        || timed.Item.ResponseHeadersText.Contains("Model:", StringComparison.OrdinalIgnoreCase)
                        || timed.Item.EndpointKey.Contains("| model=", StringComparison.OrdinalIgnoreCase);
                }

                // Backend-request items feed the Endpoints card only. They are excluded from the
                // request panel and from every runtime aggregate below, which are owned by the
                // final S7P-ProxyRequest.
                if (string.Equals(timed.Item.EventType, "S7P-BackendRequest", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Still-running (enqueued but not yet finalized) placeholder rows are shown in the
                // Request Status list, but they aren't "completed" yet, so they're excluded from
                // Completed/TotalRequests and every other runtime aggregate below until finalized.
                if (timed.Item.IsRunning)
                {
                    continue;
                }

                nonBackendRequestCount++;

                if (timed.Item.RequestContentLength > 0)
                {
                    requestSizeTotal += timed.Item.RequestContentLength;
                    requestSizeCount++;
                }

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

                if (!string.IsNullOrWhiteSpace(timed.Item.EndpointKey)
                    && !latestEndpointStates.ContainsKey(timed.Item.EndpointKey))
                {
                    latestEndpointStates[timed.Item.EndpointKey] = timed.Item.IsEndpointCircuitBreakerOpen;
                }

                if (string.Equals(timed.Item.EventType, "S7P-ProxyRequest", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(timed.Item.BackendHost))
                {
                    if (!backendRequestStats.TryGetValue(timed.Item.BackendHost, out var aggregate))
                    {
                        aggregate = new BackendRequestAggregate();
                        backendRequestStats[timed.Item.BackendHost] = aggregate;
                    }

                    aggregate.RequestCalls++;
                    if (timed.Item.StatusCode is >= 200 and < 300)
                    {
                        aggregate.RequestSuccesses++;
                    }
                    else
                    {
                        aggregate.RequestFailures++;
                    }

                    if (timed.Item.Duration is { } requestLatency)
                    {
                        aggregate.TotalRequestLatencyMs += requestLatency.TotalMilliseconds;
                        aggregate.LatencySamples++;
                    }
                }
            }

            var endpointCount = latestEndpointStates.Count;
            var endpointCircuitBreakerOpenCount = latestEndpointStates.Values.Count(isOpen => isOpen);
            var aggregateServerOpen = endpointCount > 0 && endpointCircuitBreakerOpenCount == endpointCount;

            var stats = new RuntimeStatsSnapshot
            {
                TotalRequests = nonBackendRequestCount,
                RequestsPerSecond = inWindow / RateWindow.TotalSeconds,
                Failed = failed,
                SuccessRate = decided == 0 ? 0 : succeeded * 100.0 / decided,
                AvgLatencyMs = latencyCount == 0 ? 0 : latencyTotalMs / latencyCount,
                EnqueuedCount = _enqueueSuccess,
                CompletedCount = nonBackendRequestCount,
                ProcessingCount = Math.Max(0, _enqueueSuccess - nonBackendRequestCount),
                AverageRequestSizeBytes = requestSizeCount == 0 ? 0 : (double)requestSizeTotal / requestSizeCount,
                ActiveHosts = _fleet.ActiveHosts,
                TotalHosts = _fleet.TotalHosts,
                BackendProbeLatencyMs = _fleet.ProbeLatencyMs,
                LoadBalancingMode = _fleet.LoadBalancingMode,
                PrimaryBackend = _fleet.PrimaryBackend,
                ProxyVersion = _fleet.ProxyVersion,
                ServerCircuitBreakerOpen = _serverCircuitBreakerOpen || aggregateServerOpen,
                EndpointCircuitBreakerOpenCount = endpointCircuitBreakerOpenCount,
                EndpointCount = endpointCount,
            };

            var backends = _backends
                .Select(backend =>
                {
                    if (!backendRequestStats.TryGetValue(backend.HostKey, out var aggregate))
                    {
                        return backend with
                        {
                            ProbeSuccesses = Math.Max(0, backend.Calls - backend.Errors),
                            ProbeFailures = Math.Max(0, backend.Errors),
                            RequestCalls = 0,
                            RequestSuccesses = 0,
                            RequestFailures = 0,
                            AvgRequestLatencyMs = 0,
                        };
                    }

                    var avgRequestLatencyMs = aggregate.LatencySamples == 0
                        ? 0
                        : aggregate.TotalRequestLatencyMs / aggregate.LatencySamples;

                    return backend with
                    {
                        ProbeSuccesses = Math.Max(0, backend.Calls - backend.Errors),
                        ProbeFailures = Math.Max(0, backend.Errors),
                        RequestCalls = aggregate.RequestCalls,
                        RequestSuccesses = aggregate.RequestSuccesses,
                        RequestFailures = aggregate.RequestFailures,
                        AvgRequestLatencyMs = avgRequestLatencyMs,
                    };
                })
                .ToArray();

            var circuitBreaker = new CircuitBreakerSnapshot
            {
                IsOpen = _serverCircuitBreakerOpen || aggregateServerOpen,
                LastTriggeredUtc = _circuitBreakerLastTriggered,
                ServerEventCount = _serverCircuitBreakerEventCount,
                LastErrorCode = _lastCircuitBreakerErrorCode,
                BackendIssues = _backendCircuitBreakerIssues.Values.OrderByDescending(x => x.LastOccurrenceUtc).ToArray(),
                EndpointCircuitBreakerOpenCount = endpointCircuitBreakerOpenCount,
                EndpointCircuitBreakerTotalCount = endpointCount,
            };

            var serverErrors = new ServerErrorSnapshot
            {
                RejectedRequests = _serverRejectedRequests,
                NotAuthorized403Count = _serverNotAuthorized403Count,
                LatestQueueLength = _latestServerQueueLength,
                MaxQueueLength = _maxServerQueueLength,
                TopPaths = _serverPathCounts
                    .OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(10)
                    .Select(pair => new ServerPathCount
                    {
                        Path = pair.Key,
                        Count = pair.Value,
                    })
                    .ToArray(),
                EnqueueAttempts = _enqueueAttempts,
                EnqueueSuccess = _enqueueSuccess,
                EnqueueFailed = _enqueueFailed,
                EnqueueSuccessRate = _enqueueAttempts == 0
                    ? 0
                    : (_enqueueSuccess * 100.0) / _enqueueAttempts,
                LastEnqueueQueueLength = _lastEnqueueQueueLength,
                LastEnqueueActiveHosts = _lastEnqueueActiveHosts,
                TopEnqueuePaths = _enqueuePathCounts
                    .OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(10)
                    .Select(pair => new ServerPathCount
                    {
                        Path = pair.Key,
                        Count = pair.Value,
                    })
                    .ToArray(),
            };

            return new MonitorSnapshot
            {
                TimestampUtc = now,
                LastDataUtc = _lastDataUtc,
                HasData = _hasData,
                HasUserInfo = hasUserInfo,
                HasModelInfo = hasModelInfo,
                Stats = stats,
                CircuitBreaker = circuitBreaker,
                ServerErrors = serverErrors,
                Backends = backends,
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

    private void UpdateServerCircuitBreakerState(MultiRequestStatusItem request)
    {
        if (request.IsServerCircuitBreakerSignal)
        {
            _serverCircuitBreakerOpen = true;
            return;
        }

        if (request.StatusCode is >= 200 and < 400)
        {
            _serverCircuitBreakerOpen = false;
        }
    }

    private sealed class BackendRequestAggregate
    {
        public int RequestCalls { get; set; }
        public int RequestSuccesses { get; set; }
        public int RequestFailures { get; set; }
        public int LatencySamples { get; set; }
        public double TotalRequestLatencyMs { get; set; }
    }
}
