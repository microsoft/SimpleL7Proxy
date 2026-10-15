using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using CompanionApp.Components.Shared;
using Microsoft.Extensions.Options;

namespace CompanionApp.Observability;

/// <summary>Publishes one consistent observability generation on a server-wide cadence.</summary>
public sealed class ObservabilityDashboardStore : BackgroundService {
    private readonly EventHubMonitorStore _monitor;
    private readonly ProxyMetricsCatalog _catalog;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _refreshInterval;
    private readonly ConditionalWeakTable<MultiRequestStatusItem, UsageCache> _usage = new();
    private ObservabilitySnapshot _snapshot = new();
    private long _version;

    /// <summary>Creates the publisher using the monitor's existing refresh interval (five seconds by default).</summary>
    public ObservabilityDashboardStore(
        EventHubMonitorStore monitor,
        ProxyMetricsCatalog catalog,
        IOptions<EventHubMonitorOptions> options,
        TimeProvider? timeProvider = null) {
        _monitor = monitor;
        _catalog = catalog;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _refreshInterval = TimeSpan.FromSeconds(options.Value.RefreshSeconds < 1 ? 5 : options.Value.RefreshSeconds);
    }

    /// <summary>Signals that circuits can read a new immutable publication.</summary>
    public event Action? Changed;

    /// <summary>Returns the latest publication without reaggregating retained events.</summary>
    public ObservabilitySnapshot GetSnapshot() => Volatile.Read(ref _snapshot);

    /// <summary>Publishes centrally, including idle ticks needed for rates, expiry and elapsed-time displays.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        PublishSnapshot();
        using var timer = new PeriodicTimer(_refreshInterval, _timeProvider);
        try {
            while (await timer.WaitForNextTickAsync(stoppingToken)) {
                PublishSnapshot();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private void PublishSnapshot() {
        ObservabilitySnapshot publication;
        lock (_monitor.IngestionGate) {
            var monitor = _monitor.GetSnapshot();
            var catalog = _catalog.CapturePublication();
            var requests = monitor.Requests.Select(request =>
                ObservedRequestSnapshot.Capture(request, GetUsage(request))).ToImmutableArray();
            var now = _timeProvider.GetUtcNow();
            publication = new ObservabilitySnapshot {
                Version = ++_version,
                PublishedUtc = now,
                LastDataReceivedUtc = monitor.LastDataUtc > catalog.LastDataReceivedUtc
                    ? monitor.LastDataUtc : catalog.LastDataReceivedUtc,
                WindowStartUtc = now - EventHubMonitorStore.RequestRetention,
                WindowEndUtc = now,
                IsReplay = _monitor.DisableRequestAging,
                RetentionTruncated = catalog.RetentionTruncated,
                Monitor = new ObservabilityMonitorSnapshot {
                    TimestampUtc = monitor.TimestampUtc,
                    LastDataUtc = monitor.LastDataUtc,
                    HasData = monitor.HasData,
                    HasUserInfo = monitor.HasUserInfo,
                    HasModelInfo = monitor.HasModelInfo,
                    Stats = monitor.Stats,
                    CircuitBreaker = monitor.CircuitBreaker with {
                        BackendIssues = monitor.CircuitBreaker.BackendIssues.ToImmutableArray(),
                    },
                    ServerErrors = monitor.ServerErrors with {
                        TopPaths = monitor.ServerErrors.TopPaths.ToImmutableArray(),
                        TopEnqueuePaths = monitor.ServerErrors.TopEnqueuePaths.ToImmutableArray(),
                    },
                    Backends = monitor.Backends.ToImmutableArray(),
                    Requests = requests,
                    Endpoints = monitor.Endpoints.ToImmutableArray(),
                    SuccessfulPaths = monitor.SuccessfulPaths.ToImmutableArray(),
                    FailedPaths = monitor.FailedPaths.ToImmutableArray(),
                    SuccessfulUsers = monitor.SuccessfulUsers.ToImmutableArray(),
                    FailedUsers = monitor.FailedUsers.ToImmutableArray(),
                },
                Scopes = catalog.Scopes,
                MetricGroupsByScope = catalog.Groups,
            };
            Interlocked.Exchange(ref _snapshot, publication);
        }
        Changed?.Invoke();
    }

    private ImmutableDictionary<string, long> GetUsage(MultiRequestStatusItem request) {
        var cached = _usage.GetValue(request, _ => new UsageCache());
        if (cached.Metrics is null
            || cached.RequestHeaders != request.RequestHeadersText
            || cached.ResponseHeaders != request.ResponseHeadersText
            || cached.ResponseBody != request.ResponseBody) {
            cached.Metrics = ObservedUsageParser.Parse(request);
            cached.RequestHeaders = request.RequestHeadersText;
            cached.ResponseHeaders = request.ResponseHeadersText;
            cached.ResponseBody = request.ResponseBody;
        }
        return cached.Metrics;
    }

    private sealed class UsageCache {
        public string? RequestHeaders { get; set; }
        public string? ResponseHeaders { get; set; }
        public string? ResponseBody { get; set; }
        public ImmutableDictionary<string, long>? Metrics { get; set; }
    }
}
