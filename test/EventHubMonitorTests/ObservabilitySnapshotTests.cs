using CompanionApp.Components.Shared;
using CompanionApp.Components.Shared.EventHub;
using CompanionApp.Observability;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EventHubMonitorTests;

/// <summary>Validates immutable publications, central cadence, usage normalization and idle expiry.</summary>
[TestClass]
public sealed class ObservabilitySnapshotTests {
    [TestMethod]
    public async Task Publication_FreezesRequestLifecycleAndNestedCollections() {
        var clock = new PublicationClock();
        var monitor = new EventHubMonitorStore(clock);
        var enqueue = new List<KeyValuePair<string, string>> { new("MID", "request-1") };
        var attempt = new List<KeyValuePair<string, string>> { new("Status", "503") };
        var calls = new List<BackendCallRecord> {
            new() { TargetName = "primary", BackendLog = "attempt-1" },
        };
        var request = new MultiRequestStatusItem {
            IsRunning = true,
            ResponseHeadersText = "Usage.prompt_tokens: 10\nUsage.completion_tokens: 3",
            Phases = new RequestPhaseView {
                SevenPId = "request-1",
                Enqueue = enqueue,
                Attempts = new[] { attempt },
            },
            BackendCalls = calls,
        };
        monitor.AddRequest(request);
        using var publisher = CreatePublisher(monitor, new ProxyMetricsCatalog(clock), clock);
        await StartAsync(publisher, clock);
        var first = publisher.GetSnapshot();
        var captured = first.Monitor.Requests.Single();
        Assert.IsTrue(captured.IsRunning);
        Assert.AreEqual(13L, captured.UsageMetrics["usage.total_tokens"]);

        enqueue[0] = new("MID", "changed");
        attempt[0] = new("Status", "200");
        calls.Clear();
        request.IsRunning = false;
        request.IsComplete = true;
        request.StatusCode = 200;
        request.ResponseHeadersText = "Usage.total_tokens: 20";
        monitor.MarkRequestFinalized(request);

        Assert.AreSame(first, publisher.GetSnapshot(), "Ingestion must not publish on every event.");
        Assert.IsTrue(captured.IsRunning);
        Assert.AreEqual("request-1", captured.Phases!.Enqueue!.Value[0].Value);
        Assert.AreEqual("503", captured.Phases.Attempts[0][0].Value);
        Assert.AreEqual(1, captured.BackendCalls.Length);
        Assert.AreEqual(13L, captured.UsageMetrics["usage.total_tokens"]);

        var viewer = first.Monitor.ToMonitorSnapshot();
        viewer.Requests[0].StatusCode = 500;
        Assert.IsNull(first.Monitor.Requests[0].StatusCode);
        Assert.IsNull(first.Monitor.ToMonitorSnapshot().Requests[0].StatusCode,
            "One circuit's inspector row must not mutate another circuit.");

        await TickAsync(publisher, clock);
        var finalized = publisher.GetSnapshot().Monitor.Requests.Single();
        Assert.IsFalse(finalized.IsRunning);
        Assert.AreEqual(200, finalized.StatusCode);
        Assert.AreEqual(20L, finalized.UsageMetrics["usage.total_tokens"]);
        Assert.AreEqual(captured.RequestNumber, finalized.RequestNumber);
        await publisher.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task Publication_AdvancesOneSharedGenerationAndPreservesMissingUsage() {
        var clock = new PublicationClock();
        var monitor = new EventHubMonitorStore(clock);
        var catalog = new ProxyMetricsCatalog(clock);
        using var publisher = CreatePublisher(monitor, catalog, clock);
        var notifications = 0;
        publisher.Changed += () => Interlocked.Increment(ref notifications);
        await StartAsync(publisher, clock);
        var first = publisher.GetSnapshot();
        var firstNotifications = Volatile.Read(ref notifications);

        monitor.AddRequest(new MultiRequestStatusItem {
            EventType = "S7P-ProxyRequest", StatusCode = 200,
            ContainerApp = "proxy", Replica = "replica-1",
        });
        catalog.Publish(new[] { new ParsedEventRecord("{}", new Dictionary<string, string> {
            ["Type"] = "S7P-ProxyRequest", ["MID"] = "request-1", ["Status"] = "200",
            ["ContainerApp"] = "proxy", ["Replica"] = "replica-1",
        }) });
        Assert.AreSame(first, publisher.GetSnapshot());
        Assert.AreEqual(firstNotifications, Volatile.Read(ref notifications));

        await TickAsync(publisher, clock);
        var snapshot = publisher.GetSnapshot();
        Assert.AreEqual(first.Version + 1, snapshot.Version);
        Assert.AreEqual(firstNotifications + 1, Volatile.Read(ref notifications));
        Assert.IsTrue(snapshot.Monitor.HasData);
        Assert.IsFalse(snapshot.Monitor.Requests.Single().HasUsage);
        Assert.AreEqual(0, snapshot.Monitor.Requests.Single().UsageMetrics.Count);
        Assert.IsTrue(snapshot.Scopes.Any(scope => scope.ContainerApp == "proxy" && scope.IsReplica));
        Assert.IsTrue(snapshot.IsKnownScope(snapshot.Scopes.Single(scope => scope.IsReplica).Id));
        Assert.AreEqual(5, snapshot.GetActiveGroups("all").Count);
        Assert.AreEqual(first.PublishedUtc, snapshot.LastDataReceivedUtc);
        await publisher.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task IdlePublication_UpdatesRatesAndExpiresRequestsAndCatalogScopes() {
        var clock = new PublicationClock();
        var monitor = new EventHubMonitorStore(clock);
        var catalog = new ProxyMetricsCatalog(clock);
        monitor.AddRequest(new MultiRequestStatusItem {
            EventType = "S7P-ProxyRequest", StatusCode = 200,
        });
        catalog.Publish(new[] { new ParsedEventRecord("{}", new Dictionary<string, string> {
            ["Type"] = "S7P-ProxyRequest", ["MID"] = "request-1",
            ["ContainerApp"] = "proxy", ["Replica"] = "replica-1",
        }) });
        using var publisher = CreatePublisher(monitor, catalog, clock);
        await StartAsync(publisher, clock);
        var first = publisher.GetSnapshot();
        Assert.AreEqual(0.2, first.Monitor.Stats.RequestsPerSecond, 0.0001);

        await TickAsync(publisher, clock, TimeSpan.FromSeconds(6));
        var idle = publisher.GetSnapshot();
        Assert.AreEqual(0.0, idle.Monitor.Stats.RequestsPerSecond, 0.0001);
        Assert.AreEqual(first.LastDataReceivedUtc, idle.LastDataReceivedUtc);
        Assert.IsTrue(idle.PublishedUtc > first.PublishedUtc);

        await TickAsync(publisher, clock, TimeSpan.FromHours(1));
        var expired = publisher.GetSnapshot();
        Assert.AreEqual(0, expired.Monitor.Requests.Length);
        Assert.AreEqual(1, expired.Scopes.Length);
        Assert.IsTrue(expired.Scopes[0].IsAll);
        Assert.AreEqual(1, first.Monitor.Requests.Length, "Old publications must remain unchanged after expiry.");
        await publisher.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task UsageNormalization_RetainsDynamicFieldsAndDistinguishesMeasuredZero() {
        var clock = new PublicationClock();
        var monitor = new EventHubMonitorStore(clock);
        monitor.AddRequests(new[] {
            new MultiRequestStatusItem {
                ResponseBody = """{"response":{"usage":{"input_tokens":4,"output_tokens":0,"input_tokens_details":{"cached_tokens":2}}}}""",
            },
            new MultiRequestStatusItem { ResponseHeadersText = "Usage.total_tokens: 0" },
            new MultiRequestStatusItem(),
        });
        using var publisher = CreatePublisher(monitor, new ProxyMetricsCatalog(clock), clock);
        await StartAsync(publisher, clock);
        var requests = publisher.GetSnapshot().Monitor.Requests;
        Assert.AreEqual(4L, requests[0].UsageMetrics["usage.total_tokens"]);
        Assert.AreEqual(2L, requests[0].UsageMetrics["usage.input_tokens_details.cached_tokens"]);
        Assert.IsTrue(requests[1].HasUsage);
        Assert.AreEqual(0L, requests[1].UsageMetrics["usage.total_tokens"]);
        Assert.IsFalse(requests[2].HasUsage);
        await TickAsync(publisher, clock);
        Assert.AreSame(requests[0].UsageMetrics, publisher.GetSnapshot().Monitor.Requests[0].UsageMetrics,
            "Unchanged usage sources must not be parsed again on idle publication.");
        await publisher.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task CatalogSafetyCap_IsVisibleUntilTruncatedRecordsLeaveTheWindow() {
        var clock = new PublicationClock();
        var catalog = new ProxyMetricsCatalog(clock);
        var record = new ParsedEventRecord("{}", new Dictionary<string, string> {
            ["Type"] = "S7P-ProxyRequest", ["MID"] = "request",
        });
        catalog.Publish(Enumerable.Repeat(record, 100_001).ToArray());
        using var publisher = CreatePublisher(new EventHubMonitorStore(clock), catalog, clock);
        await StartAsync(publisher, clock);
        Assert.IsTrue(publisher.GetSnapshot().RetentionTruncated);
        await TickAsync(publisher, clock, TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        Assert.IsFalse(publisher.GetSnapshot().RetentionTruncated);
        await publisher.StopAsync(CancellationToken.None);
    }

    private static ObservabilityDashboardStore CreatePublisher(
        EventHubMonitorStore monitor, ProxyMetricsCatalog catalog, TimeProvider clock) =>
        new(monitor, catalog, Options.Create(new EventHubMonitorOptions { RefreshSeconds = 5 }), clock);

    private static async Task StartAsync(ObservabilityDashboardStore publisher, PublicationClock clock) {
        await publisher.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => publisher.GetSnapshot().Version > 0 && clock.HasTimer);
    }

    private static async Task TickAsync(
        ObservabilityDashboardStore publisher, PublicationClock clock, TimeSpan? elapsed = null) {
        var version = publisher.GetSnapshot().Version;
        clock.Advance(elapsed ?? TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => publisher.GetSnapshot().Version > version);
    }

    private static async Task WaitUntilAsync(Func<bool> condition) {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class PublicationClock : TimeProvider {
        private readonly object _gate = new();
        private DateTimeOffset _now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        private PublicationTimer? _timer;

        public bool HasTimer { get { lock (_gate) return _timer is not null; } }

        public override DateTimeOffset GetUtcNow() {
            lock (_gate) return _now;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
            lock (_gate) return _timer = new PublicationTimer(callback, state);
        }

        public void Advance(TimeSpan elapsed) {
            PublicationTimer? timer;
            lock (_gate) {
                _now += elapsed;
                timer = _timer;
            }
            timer?.Tick();
        }
    }

    private sealed class PublicationTimer(TimerCallback callback, object? state) : ITimer {
        private bool _disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
        public void Tick() { if (!_disposed) callback(state); }
        public void Dispose() => _disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
