using chat_tester.Components.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EventHubMonitorTests;

/// <summary>
/// Revalidation suite for the EventHub Monitor page. Drives the real ingest path
/// (<see cref="EventHubReader"/> importing an NDJSON fixture into a real
/// <see cref="EventHubMonitorStore"/>) and asserts the derived snapshot values that the
/// page renders. Maps 1:1 to the validation checklist in
/// <c>EVENTHUB_MONITOR_REQUIREMENTS.md</c> (§J) and the invariants (§I).
///
/// Run: <c>dotnet test test/EventHubMonitorTests/EventHubMonitorTests.csproj</c>
/// </summary>
[TestClass]
public sealed class EventHubMonitorRevalidationTests
{
    // Deterministic clock so requests-per-second (5s trailing window) and retention are exact.
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private MonitorSnapshot _snapshot = new();
    private EventHubMonitorStore _store = null!;

    [TestInitialize]
    public async Task LoadFixtureAsync()
    {
        _store = new EventHubMonitorStore(new FixedTimeProvider(FixedNow));
        var catalog = new ProxyMetricsCatalog();

        var options = Options.Create(new EventHubMonitorOptions
        {
            EventHubEnabled = false, // import the file, then ExecuteAsync returns.
            LocalFilePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "eventhub-sample.ndjson"),
        });

        var reader = new EventHubReader(_store, catalog, options, NullLogger<EventHubReader>.Instance);

        await reader.StartAsync(CancellationToken.None);
        await reader.ExecuteTask!; // completes once the file import finishes.
        await reader.StopAsync(CancellationToken.None);

        _snapshot = _store.GetSnapshot();
    }

    // ── §J: page loads / has data, aging disabled during import (§I #4) ──

    [TestMethod]
    public void Import_MarksDataReceived_AndDisablesAging()
    {
        Assert.IsTrue(_snapshot.HasData, "Snapshot should report data after import.");
        Assert.IsTrue(_store.DisableRequestAging, "Local-file import MUST disable request aging.");
    }

    // ── §I #1: backend-request items feed Endpoints only, excluded from runtime aggregates ──

    [TestMethod]
    public void BackendRequestItems_AreStoredButExcludedFromRuntimeTotals()
    {
        var backendRequestCount = _snapshot.Requests.Count(r =>
            string.Equals(r.EventType, "S7P-BackendRequest", StringComparison.OrdinalIgnoreCase));

        Assert.AreEqual(2, backendRequestCount, "Both S7P-BackendRequest attempts should be retained for Endpoints.");
        Assert.AreEqual(9, _snapshot.Requests.Count, "7 final requests + 2 backend attempts.");
        Assert.AreEqual(7, _snapshot.Stats.TotalRequests, "Runtime totals must exclude backend-request items.");
    }

    // ── §F.1: Request tile ──

    [TestMethod]
    public void RequestTile_CountsMatchFixture()
    {
        var stats = _snapshot.Stats;
        Assert.AreEqual(7, stats.CompletedCount, "Completed = non-backend request count.");
        Assert.AreEqual(3, stats.Failed, "Failed decided requests: 500, 429, expired(408).");
        Assert.AreEqual(57.142, stats.SuccessRate, 0.01, "4 of 7 decided requests are 2xx.");
        Assert.AreEqual(7, stats.EnqueuedCount, "7 S7P-ProxyRequestEnqueued successes.");
        Assert.AreEqual(0, stats.ProcessingCount, "max(0, Enqueued - Completed) = 0.");
        Assert.AreEqual(197.142, stats.AverageRequestSizeBytes, 0.01, "Mean RequestContentLength over 7 items.");
        Assert.AreEqual(1.4, stats.RequestsPerSecond, 0.001, "7 requests within the 5s trailing window.");
    }

    // ── §F.2: Server tile ──

    [TestMethod]
    public void ServerTile_LatencyAndEnqueueMetrics()
    {
        var stats = _snapshot.Stats;
        var errors = _snapshot.ServerErrors;

        Assert.AreEqual(5702.857, stats.AvgLatencyMs, 0.01, "Mean Duration over 7 non-backend requests.");
        Assert.AreEqual("latency", stats.LoadBalancingMode);
        Assert.AreEqual(3060.0, stats.BackendProbeLatencyMs, 0.01, "Mean backend latency (120, 6000).");

        Assert.AreEqual(9, errors.EnqueueAttempts, "7 enqueues + 2 server errors.");
        Assert.AreEqual(7, errors.EnqueueSuccess);
        Assert.AreEqual(2, errors.EnqueueFailed);
        Assert.AreEqual(77.777, errors.EnqueueSuccessRate, 0.01);
        Assert.AreEqual(2, errors.RejectedRequests, "Two S7P-ServerError events.");
        Assert.AreEqual(1, errors.NotAuthorized403Count, "One 403 'Not Authorized'.");
        Assert.AreEqual(3, errors.LastEnqueueQueueLength, "Last enqueue QueueLength.");
        Assert.AreEqual(2, errors.LastEnqueueActiveHosts);
    }

    // ── §F.3: Circuit-breaker tile (§I #7) ──

    [TestMethod]
    public void CircuitBreakerTile_StateAndCounts()
    {
        var stats = _snapshot.Stats;
        var cb = _snapshot.CircuitBreaker;

        Assert.IsFalse(stats.ServerCircuitBreakerOpen, "Last request (200) resets server CB to CLOSED.");
        Assert.AreEqual(2, stats.EndpointCount, "Two distinct endpoint paths observed.");
        Assert.AreEqual(1, stats.EndpointCircuitBreakerOpenCount, "/responses is open; /chat is closed.");
        Assert.AreEqual(1, cb.ServerEventCount, "One hostless S7P-CircuitBreakerError.");
        Assert.AreEqual(503, cb.LastErrorCode);
    }

    // ── §F.4: Backends card ──

    [TestMethod]
    public void Backends_ParsedWithProbeAndRequestStats()
    {
        Assert.AreEqual(2, _snapshot.Backends.Count);

        var good = _snapshot.Backends.Single(b => b.Name == "good.openai.azure.com");
        Assert.AreEqual("healthy", good.Css, "Active + success rate >= threshold.");
        Assert.AreEqual(50, good.ProbeSuccesses); // Calls - Errors
        Assert.AreEqual(0, good.ProbeFailures);
        Assert.AreEqual(4, good.RequestCalls, "4 final S7P-ProxyRequest routed to good host.");
        Assert.AreEqual(0, good.RequestFailures);
        Assert.AreEqual(975.0, good.AvgRequestLatencyMs, 0.01, "(1000+1200+900+800)/4.");

        var bad = _snapshot.Backends.Single(b => b.Name == "bad.openai.azure.com");
        Assert.AreEqual("down", bad.Css, "Success rate 60 < threshold 80.");
        Assert.AreEqual(12, bad.ProbeSuccesses); // 20 - 8
        Assert.AreEqual(8, bad.ProbeFailures);
        Assert.AreEqual(2, bad.RequestCalls, "500 and 429 routed to bad host.");
        Assert.AreEqual(2, bad.RequestFailures);
        Assert.AreEqual(3010.0, bad.AvgRequestLatencyMs, 0.01, "(6000+20)/2.");
    }

    // ── §F.7 inputs: Paths (success = [200,400)) ──

    [TestMethod]
    public void PathClassificationInputs_MatchFixture()
    {
        // Success paths from final proxy requests with 2xx/3xx status.
        var chatSuccess = FinalRequests()
            .Count(r => GetHeader(r.RequestHeadersText, "Path") == "/openai/v1/chat/completions"
                && r.StatusCode is >= 200 and < 400);
        Assert.AreEqual(4, chatSuccess, "req1, req2, req5, req6.");

        var responsesFailed = FinalRequests()
            .Count(r => GetHeader(r.RequestHeadersText, "Path") == "/openai/v1/responses"
                && !(r.StatusCode is >= 200 and < 400));
        Assert.AreEqual(1, responsesFailed, "req3 (500).");
    }

    // ── §F.8 inputs: Users ──

    [TestMethod]
    public void UserClassificationInputs_MatchFixture()
    {
        Assert.AreEqual(2, UserSuccessCount("alice"));
        Assert.AreEqual(2, UserSuccessCount("carol"));
        Assert.AreEqual(0, UserSuccessCount("bob"), "bob has only failures (500, 429).");
        Assert.AreEqual(0, UserSuccessCount("dave"), "dave has only an expired request.");
    }

    // ── §F.6 inputs + §I #2: Endpoints dedupe scenario is set up ──

    [TestMethod]
    public void EndpointDedupe_AttemptEchoedInProxyRequest_UsesIdenticalBackendLog()
    {
        var backendAttempt = _snapshot.Requests.Single(r =>
            string.Equals(r.EventType, "S7P-BackendRequest", StringComparison.OrdinalIgnoreCase)
            && GetHeader(r.ResponseHeadersText, "backendLog").Contains("Using GOOD URL"));

        var attemptLog = GetHeader(backendAttempt.ResponseHeadersText, "backendLog");

        // The same log string is echoed inside the final S7P-ProxyRequest for s1 as
        // Attempt-1-backendLog; the Endpoints card MUST dedupe on the exact string.
        var proxyEcho = _snapshot.Requests.Any(r =>
            string.Equals(r.EventType, "S7P-ProxyRequest", StringComparison.OrdinalIgnoreCase)
            && r.ResponseHeadersText.Contains(attemptLog, StringComparison.Ordinal));

        Assert.IsTrue(attemptLog.Contains("Using GOOD URL"), "Backend log must carry the endpoint target.");
        Assert.IsTrue(proxyEcho, "The attempt log must also appear inside a proxy request (dedupe input).");
    }

    // ── §J: malformed/unsupported records are skipped without stopping ingestion ──

    [TestMethod]
    public void MalformedAndUnsupportedRecords_AreSkipped()
    {
        // If the garbage line or unknown type had crashed the reader, we would not have
        // the full, exact request set. Reaching the expected totals proves they were skipped.
        Assert.AreEqual(7, _snapshot.Stats.TotalRequests);
        Assert.IsFalse(_snapshot.Requests.Any(r =>
            string.Equals(r.EventType, "S7P-UnknownEvent", StringComparison.OrdinalIgnoreCase)));
    }

    // ── §E / §I #4: retention purges records older than one hour when aging is enabled ──

    [TestMethod]
    public void Retention_PurgesRequestsOlderThanOneHour()
    {
        var clock = new MutableTimeProvider(FixedNow);
        var store = new EventHubMonitorStore(clock);

        store.AddRequest(new MultiRequestStatusItem { EventType = "S7P-ProxyRequest", StatusCode = 200 });
        Assert.AreEqual(1, store.GetSnapshot().Requests.Count);

        clock.Now = FixedNow.AddHours(2);
        Assert.AreEqual(0, store.GetSnapshot().Requests.Count, "Request older than 1h must be purged.");
    }

    // ── helpers ──

    private IEnumerable<MultiRequestStatusItem> FinalRequests() => _snapshot.Requests.Where(r =>
        string.Equals(r.EventType, "S7P-ProxyRequest", StringComparison.OrdinalIgnoreCase)
        || string.Equals(r.EventType, "S7P-ProxyRequestExpired", StringComparison.OrdinalIgnoreCase));

    private int UserSuccessCount(string user) => FinalRequests()
        .Count(r => GetHeader(r.RequestHeadersText, "UserID") == user && r.StatusCode is >= 200 and < 400);

    private static string GetHeader(string headers, string name)
    {
        var prefix = name + ":";
        foreach (var line in headers.Replace("\r\n", "\n").Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return line[prefix.Length..].Trim();
            }
        }

        return string.Empty;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
