using System.Collections.Immutable;
using CompanionApp.Components.Shared;
using CompanionApp.Observability;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EventHubMonitorTests;

/// <summary>Validates shared request analytics and independently selected Insights views of immutable publications.</summary>
[TestClass]
public sealed class ObservabilityProjectionTests {
    private static readonly DateTimeOffset Anchor = new(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly ImmutableArray<ProxyMetricsCatalog.ScopeOption> Scopes = [
        new("all", "All proxy instances", "", "", true, false),
        new("app:app-a", "App A", "app-a", "", false, false),
        new("replica:app-a:r1", "App A / R1", "app-a", "r1", false, true),
        new("replica:app-a:r2", "App A / R2", "app-a", "r2", false, true),
        new("app:app-b", "App B", "app-b", "", false, false),
    ];

    [TestMethod]
    public void RequestFilters_KeepGlobalStatsBackendsAndOptionsOutsideFilteredRequestSections() {
        var cache = ObservabilityProjectionCache.For(CreatePublication(
            Request(1, "alice", "model-one", 200, "/one"),
            Request(2, "bob", "model-two", 503, "/two") with { Replica = "r2" },
            Request(3, "carol", "model-three", 302, "/other") with { ContainerApp = "app-b" },
            Request(4, "alice", "model-one", 500, "/attempt") with { EventType = "S7P-BackendRequest" },
            Request(5, "expired", "model-one", null, "/expired") with { EventType = "S7P-ProxyRequestExpired" },
            Request(6, "queued", "model-one", null, "/queued") with { EventType = "S7P-RequestEnqueued" }));

        var replica = cache.GetRequests("replica:app-a:r1", "All envs", "All users", "All models");
        CollectionAssert.AreEqual(new[] { 1, 4, 5, 6 }, replica.FilteredRequests.Select(item => item.RequestNumber).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 5, 6 }, replica.HistoryItems.Select(item => item.RequestNumber).ToArray());
        Assert.AreEqual("/one", replica.SuccessfulPaths.Single().Path);
        Assert.AreEqual(1, replica.SuccessfulPaths.Single().Count);
        Assert.AreEqual("/expired", replica.FailedPaths.Single().Path);
        Assert.AreEqual(1, replica.FailedPaths.Single().Count);
        Assert.AreEqual("alice", replica.SuccessfulUsers.Single().User);
        Assert.AreEqual("expired", replica.FailedUsers.Single().User);
        Assert.AreEqual(999, replica.Snapshot.Stats.TotalRequests);
        Assert.AreEqual("global-backend", replica.Snapshot.Backends.Single().Name);
        Assert.AreEqual(6, replica.Snapshot.Requests.Length);
        Assert.IsTrue(replica.UserFilterOptions.Contains("bob"), "User options remain global even when the request section selects a replica.");
        Assert.IsTrue(replica.UserFilterOptions.Contains("carol"));
        Assert.IsTrue(replica.ModelFilterOptions.Contains("model-two"));
        Assert.IsTrue(replica.ModelFilterOptions.Contains("model-three"));
        Assert.IsTrue(replica.EnvironmentFilterOptions.Contains("App B"));

        var app = cache.GetRequests("app:app-a", "All envs", "All users", "All models");
        CollectionAssert.AreEqual(new[] { 1, 2, 4, 5, 6 }, app.FilteredRequests.Select(item => item.RequestNumber).ToArray());
        var all = cache.GetRequests("all", "All envs", "All users", "All models");
        Assert.AreEqual(6, all.FilteredRequests.Count);
        Assert.AreSame(all.Snapshot.Stats, replica.Snapshot.Stats);
        Assert.AreEqual(all.Snapshot.Backends, replica.Snapshot.Backends);
    }

    [TestMethod]
    public void RequestFilters_IntersectScopeEnvironmentUserAndPhaseResolvedModel() {
        var canonical = Request(1, "header-user", "header-model", 200, "/one") with {
            UserId = "canonical-user",
            Phases = new ObservedRequestPhases {
                Enqueue = ImmutableArray.Create(new KeyValuePair<string, string>("ModelName", "phase-model")),
            },
        };
        var headerFallback = Request(2, "header-user", "header-model", 200, "/two") with { UserId = null };
        var cache = ObservabilityProjectionCache.For(CreatePublication(
            canonical,
            headerFallback,
            Request(3, "other", "other-model", 200, "/three") with { Replica = "r2" },
            Request(4, "other", "other-model", 200, "/four") with { ContainerApp = "app-b" }));

        var combined = cache.GetRequests("app:app-a", "App A / R1", " CANONICAL-USER ", " PHASE-MODEL ");
        Assert.AreEqual(1, combined.FilteredRequests.Single().RequestNumber);
        Assert.AreEqual("header-user", combined.SuccessfulUsers.Single().User,
            "The existing Paths/Users outcome chart reads UserID headers, while the user filter prefers the captured identity.");
        Assert.IsTrue(combined.UserFilterOptions.Contains("canonical-user"));
        Assert.IsTrue(combined.ModelFilterOptions.Contains("phase-model"));

        var fallback = cache.GetRequests("all", "All envs", "header-user", "header-model");
        Assert.AreEqual(2, fallback.FilteredRequests.Single().RequestNumber);
        var incompatible = cache.GetRequests("replica:app-a:r1", "App A / R2", "All users", "All models");
        Assert.AreEqual(0, incompatible.FilteredRequests.Count, "Environment filters intersect rather than replace the URL scope.");
        Assert.AreEqual(0, incompatible.EndpointTiles.Count);
        var appEnvironment = cache.GetRequests("all", "App A", "All users", "All models");
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, appEnvironment.FilteredRequests.Select(item => item.RequestNumber).ToArray());
        var rawEnvironment = cache.GetRequests("all", "app-b", "All users", "All models");
        Assert.AreEqual(4, rawEnvironment.FilteredRequests.Single().RequestNumber);
        var allEnvironment = cache.GetRequests("replica:app-a:r1", "All proxy instances", "All users", "All models");
        CollectionAssert.AreEqual(new[] { 1, 2 }, allEnvironment.FilteredRequests.Select(item => item.RequestNumber).ToArray());
    }

    [TestMethod]
    public void Endpoints_DeduplicateBackendAttemptAlsoPresentInSummaryAndRetainSummaryOnlyRetry() {
        const string firstLog = "Using PRIMARY URL: https://primary.example/api | Proxy-Queue-Duration: 4 | Proxy-Process-Duration: 8";
        const string secondLog = "Using SECONDARY URL: https://secondary.example/api | Proxy-Queue-Duration: 2 | Proxy-Process-Duration: 6";
        var attempt = Request(1, "alice", "model-one", 503, "/chat") with {
            EventType = "S7P-BackendRequest",
            RequestHeadersText = "S7P-ID: mid-1\nModel: model-one\nPriority: 2\nx-PolicyCycleCounter: 3\nRequest-Process-Duration: 24",
            ResponseHeadersText = $"backendLog: {firstLog}",
        };
        var summary = Request(2, "alice", "model-one", 200, "/chat") with {
            RequestHeadersText = "S7P-ID: mid-1\nModel: model-one\nPriority: 2",
            ResponseHeadersText = $"Attempt-1-backendLog: {firstLog}\nAttempt-2-backendLog: {secondLog}",
        };
        var requests = ObservabilityProjectionCache.For(CreatePublication(summary, attempt))
            .GetRequests("all", "All envs", "All users", "All models");

        var model = requests.EndpointTiles.Single();
        Assert.AreEqual("model-one", model.Path, "The endpoint card groups backend targets by model, not request path.");
        Assert.AreEqual(2, model.Calls);
        Assert.AreEqual(1, model.Failures);
        Assert.AreEqual(2, model.EndpointCount);
        Assert.AreEqual(2, model.Priority2Count);
        Assert.AreEqual(3.0, model.AverageEndpointAttempts, 0.0001, "Per-attempt timing headers win over summary duplicates.");
        Assert.AreEqual(24.0, model.AverageRequestProcessDuration, 0.0001);
        Assert.AreEqual(3.0, model.AverageQueueDurationMs, 0.0001);
        Assert.AreEqual(7.0, model.AverageProcessDurationMs, 0.0001);
        Assert.AreEqual(1, model.EndpointUsages.Single(target => target.TargetName == "PRIMARY").Calls);
        Assert.AreEqual(1, model.EndpointUsages.Single(target => target.TargetName == "PRIMARY").Failures);
        Assert.AreEqual(1, model.EndpointUsages.Single(target => target.TargetName == "SECONDARY").Calls);
        Assert.AreEqual(0, model.EndpointUsages.Single(target => target.TargetName == "SECONDARY").Failures);
        Assert.AreEqual(2, requests.HistoryItems.Single().RequestNumber);
    }

    [TestMethod]
    public void Endpoints_AllDuplicateLogsDoNotReenterLabelFallbackAndDoubleCount() {
        const string log = "Using PRIMARY URL: https://primary.example/api";
        var attempt = Request(1, "alice", "model-one", 200, "/chat") with {
            EventType = "S7P-BackendRequest",
            ResponseHeadersText = $"backendLog: {log}\nx-backend-label: deployed-primary",
        };
        var duplicateAttempt = attempt with { RequestNumber = 2 };
        var summary = Request(3, "alice", "model-one", 200, "/chat") with {
            ResponseHeadersText = $"Attempt-1-backendLog: {log}\nx-backend-label: deployed-primary",
        };
        var endpoints = ObservabilityProjectionCache.For(CreatePublication(summary, duplicateAttempt, attempt))
            .GetRequests("all", "All envs", "All users", "All models").EndpointTiles;

        Assert.AreEqual(1, endpoints.Single().Calls, "A present but previously seen log is not evidence for an additional fallback attempt.");
        Assert.AreEqual(1, endpoints.Single().EndpointUsages.Single().Calls);
        Assert.AreEqual("deployed-primary", endpoints.Single().EndpointUsages.Single().TargetName);
    }

    [TestMethod]
    public void Insights_DynamicUsageAndUserShareSelectionsUseNormalizedMetricsIncludingMissingUsage() {
        var cache = ObservabilityProjectionCache.For(CreateUsagePublication());
        var requests = cache.GetRequests("all", "All envs", "All users", "All models");
        var calls = cache.GetInsights(requests, "__calls", new[] { "__total_queries" });
        var usage = cache.GetInsights(requests, "usage.vendor.accepted_tokens", new[] { "usage.vendor.accepted_tokens" });

        Assert.AreEqual(50.0, calls.UserShares.Single(row => row.User == "alice").Percent, 0.0001);
        Assert.AreEqual(25.0, calls.UserShares.Single(row => row.User == "bob").Percent, 0.0001);
        Assert.AreEqual(25.0, calls.UserShares.Single(row => row.User == "charlie").Percent, 0.0001);
        Assert.AreEqual("alice", usage.UserShares[0].User);
        Assert.AreEqual(75.0, usage.UserShares.Single(row => row.User == "alice").Percent, 0.0001);
        Assert.AreEqual(25.0, usage.UserShares.Single(row => row.User == "bob").Percent, 0.0001);
        Assert.AreEqual(0.0, usage.UserShares.Single(row => row.User == "charlie").Percent, 0.0001);
        Assert.IsFalse(usage.UserShares.Single(row => row.User == "charlie").HasUsage,
            "A normalized empty dictionary must not fall back to the nonempty response body's usage.");
        Assert.AreEqual(30L, usage.UserShares.Single(row => row.User == "alice").UsageMetrics["usage.vendor.accepted_tokens"]);
        Assert.AreEqual(0L, usage.UserShares.Single(row => row.User == "alice").UsageMetrics["usage.zero"]);
        Assert.IsTrue(usage.UserShares.Single(row => row.User == "alice").HasUsage, "Measured zero remains observed usage.");
        Assert.IsTrue(usage.TrendMetricOptions.Any(metric => metric.Key == "usage.vendor.accepted_tokens"
            && metric.Label == "Vendor / Accepted Tokens" && !metric.IsSynthetic));
        Assert.IsFalse(usage.TrendMetricOptions.Any(metric => metric.Key == "usage.body_only"));
        Assert.AreEqual(40L, usage.SelectedTrendMetrics.Single().Values.Sum());
        Assert.AreEqual(4L, calls.SelectedTrendMetrics.Single().Values.Sum());
        Assert.AreEqual(4, usage.UsageTrendPoints.Sum(point => point.Calls));
        Assert.AreEqual(40L, usage.UsageTrendPoints.Sum(point => point.UsageMetrics.GetValueOrDefault("usage.vendor.accepted_tokens")));
    }

    [TestMethod]
    public void Insights_TrendSlotsUseCapturedTimeAndHalfOpenMembershipWithSortedRequestDrilldowns() {
        var previousMinute = Anchor.AddMinutes(-1);
        var cache = ObservabilityProjectionCache.For(CreatePublication(
            Request(2, "alice", "model-one", 200, "/start") with { FinalizedAtUtc = previousMinute },
            Request(1, "alice", "model-one", 200, "/end") with { FinalizedAtUtc = Anchor.AddTicks(-1) },
            Request(3, "alice", "model-one", null, "/expired") with {
                EventType = "S7P-ProxyRequestExpired", FinalizedAtUtc = null, EnqueuedAtUtc = Anchor,
            },
            Request(4, "alice", "model-one", 200, "/future") with { FinalizedAtUtc = Anchor.AddMinutes(1) },
            Request(5, "alice", "model-one", 200, "/backend") with { EventType = "S7P-BackendRequest" },
            Request(6, "alice", "model-one", 200, "/other-app") with { ContainerApp = "app-b" },
            Request(7, "alice", "model-one", 200, "/unknown-time") with { FinalizedAtUtc = null, EnqueuedAtUtc = null },
            Request(8, "alice", "model-one", 200, "/too-old") with { FinalizedAtUtc = Anchor.AddHours(-1).AddTicks(-1) }));
        var requests = cache.GetRequests("app:app-a", "All envs", "All users", "All models");
        var insights = cache.GetInsights(requests, "__calls", new[] { "__total_queries" });

        Assert.AreEqual("Window: Last minute data", insights.TrendWindowLabel);
        Assert.AreEqual(2, insights.UsageTrendSlots.Count);
        var first = insights.UsageTrendSlots[0];
        var last = insights.UsageTrendSlots[1];
        Assert.AreEqual(previousMinute, first.StartUtc);
        Assert.AreEqual(Anchor, first.EndExclusiveUtc);
        Assert.AreEqual(Anchor, last.StartUtc);
        Assert.AreEqual(Anchor.AddMinutes(1), last.EndExclusiveUtc);
        CollectionAssert.AreEqual(new[] { 1, 2 }, insights.RequestsForSlot(first).Select(item => item.RequestNumber).ToArray());
        CollectionAssert.AreEqual(new[] { 3 }, insights.RequestsForSlot(last).Select(item => item.RequestNumber).ToArray());
        Assert.AreSame(insights.RequestsForSlot(first), insights.RequestsForSlot(first), "Drilldowns are cached, not rebuilt during rendering.");
        foreach (var slot in insights.UsageTrendSlots) {
            Assert.AreEqual(slot.Calls, insights.RequestsForSlot(slot).Count);
            Assert.IsTrue(insights.RequestsForSlot(slot).All(item => item.EventType != "S7P-BackendRequest"));
        }
        Assert.AreEqual(3L, insights.SelectedTrendMetrics.Single().Values.Sum());
    }

    [TestMethod]
    public void Cache_QueryAndMetricChangesRefreshImmediatelyWithoutAnotherPublication() {
        var publication = CreateUsagePublication();
        var cache = ObservabilityProjectionCache.For(publication);
        var all = cache.GetRequests("all", "All envs", "All users", "All models");
        var original = cache.GetInsights(all, "__calls", new[] { "__total_queries" });
        var alice = cache.GetRequests("all", "All envs", "alice", "All models");
        var filtered = cache.GetInsights(alice, "__calls", new[] { "__total_queries" });
        var changedMetric = cache.GetInsights(all, "usage.vendor.accepted_tokens", new[] { "usage.vendor.accepted_tokens" });
        var deselected = cache.GetInsights(all, "__calls", Array.Empty<string>());

        Assert.AreEqual(4, all.HistoryItems.Count);
        Assert.AreEqual(2, alice.HistoryItems.Count);
        Assert.AreEqual(2L, filtered.SelectedTrendMetrics.Single().Values.Sum());
        Assert.AreEqual(100.0, filtered.UserShares.Single().Percent, 0.0001);
        Assert.AreEqual(40L, changedMetric.SelectedTrendMetrics.Single().Values.Sum());
        Assert.AreEqual(0, deselected.SelectedTrendMetrics.Count);
        Assert.AreEqual(4, deselected.UsageTrendPoints.Sum(point => point.Calls), "Deselecting series hides charts without discarding underlying data.");
        Assert.AreEqual(4L, original.SelectedTrendMetrics.Single().Values.Sum());
        Assert.AreEqual(1L, publication.Version);
        Assert.AreSame(all, cache.GetRequests("all", "All envs", "All users", "All models"));
        Assert.AreSame(original, cache.GetInsights(all, "__calls", new[] { "__total_queries" }));
    }

    [TestMethod]
    public void Cache_ReusesQueriesAndOrderIndependentMetricSetsButInvalidatesForNewPublications() {
        var first = CreateUsagePublication();
        var cache = ObservabilityProjectionCache.For(first);
        var requests = cache.GetRequests("all", "All envs", "All users", "All models");
        var insights = cache.GetInsights(requests, "__calls", new[] { "__total_queries", "usage.vendor.accepted_tokens" });
        Assert.AreSame(cache, ObservabilityProjectionCache.For(first));
        Assert.AreSame(requests, cache.GetRequests("all", "All envs", "All users", "All models"));
        Assert.AreSame(insights, cache.GetInsights(requests, "__calls", new[] { "usage.vendor.accepted_tokens", "__total_queries" }));

        var next = first with {
            Version = first.Version + 1,
            PublishedUtc = first.PublishedUtc.AddMinutes(1),
            Monitor = first.Monitor with {
                TimestampUtc = Anchor.AddMinutes(1),
                Requests = first.Monitor.Requests.Add(Request(5, "dave", "model-one", 200, "/new") with { FinalizedAtUtc = Anchor.AddMinutes(1) }),
            },
        };
        var nextCache = ObservabilityProjectionCache.For(next);
        var nextRequests = nextCache.GetRequests("all", "All envs", "All users", "All models");
        var nextInsights = nextCache.GetInsights(nextRequests, "__calls", new[] { "__total_queries", "usage.vendor.accepted_tokens" });
        Assert.AreNotSame(cache, nextCache);
        Assert.AreNotSame(requests, nextRequests);
        Assert.AreNotSame(insights, nextInsights);
        Assert.AreEqual(5, nextRequests.HistoryItems.Count);
        Assert.AreEqual(5L, nextInsights.SelectedTrendMetrics.Single(series => series.Key == "__total_queries").Values.Sum());
        Assert.AreEqual(4, requests.HistoryItems.Count, "A new version cannot mutate an already displayed projection.");
        Assert.AreEqual(4L, insights.SelectedTrendMetrics.Single(series => series.Key == "__total_queries").Values.Sum());
        Assert.AreNotSame(cache, ObservabilityProjectionCache.For(first with { }),
            "Publication identity, not value equality or a reused version number, owns the detached projection data.");
    }

    [TestMethod]
    public void Insights_CircuitSelectionsAreCapturedAndCannotChangeAnotherCircuitsCachedView() {
        var cache = ObservabilityProjectionCache.For(CreateUsagePublication());
        var requests = cache.GetRequests("all", "All envs", "All users", "All models");
        var firstCircuitSelection = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "__total_queries" };
        var firstCircuit = cache.GetInsights(requests, "__calls", firstCircuitSelection);
        var secondCircuit = cache.GetInsights(requests, "__calls", new[] { "__total_queries" });
        Assert.AreSame(firstCircuit, secondCircuit, "Circuits may reuse data when their current selections agree.");

        firstCircuitSelection.Clear();
        firstCircuitSelection.Add("usage.vendor.accepted_tokens");
        var changedFirstCircuit = cache.GetInsights(requests, "usage.vendor.accepted_tokens", firstCircuitSelection);
        Assert.AreNotSame(firstCircuit, changedFirstCircuit);
        Assert.AreEqual("__total_queries", secondCircuit.SelectedTrendMetrics.Single().Key);
        Assert.AreEqual(4L, secondCircuit.SelectedTrendMetrics.Single().Values.Sum());
        Assert.AreEqual(50.0, secondCircuit.UserShares.Single(row => row.User == "alice").Percent, 0.0001);
        Assert.AreEqual("usage.vendor.accepted_tokens", changedFirstCircuit.SelectedTrendMetrics.Single().Key);
        Assert.AreEqual(75.0, changedFirstCircuit.UserShares.Single(row => row.User == "alice").Percent, 0.0001);

        firstCircuitSelection.Clear();
        Assert.AreEqual("usage.vendor.accepted_tokens", changedFirstCircuit.SelectedTrendMetrics.Single().Key,
            "Changing caller-owned selection collections cannot mutate previously cached series.");
        Assert.AreSame(secondCircuit, cache.GetInsights(requests, "__calls", new[] { "__total_queries" }));
    }

    [TestMethod]
    public void CircuitViews_MutableInspectorRowsCannotChangeAnotherViewerPublicationOrCachedAnalytics() {
        var publication = CreateUsagePublication();
        var cache = ObservabilityProjectionCache.For(publication);
        var requests = cache.GetRequests("all", "All envs", "All users", "All models");
        var insights = cache.GetInsights(requests, "__calls", new[] { "__total_queries" });
        var firstViewer = cache.CreateCircuitView();
        var secondViewer = cache.CreateCircuitView();
        var firstHistory = firstViewer.SelectRows(requests.HistoryItems);
        var secondHistory = secondViewer.SelectRows(requests.HistoryItems);
        var slot = insights.UsageTrendSlots.Single(candidate => candidate.Calls > 0);
        var firstDrilldown = firstViewer.SelectRows(insights.RequestsForSlot(slot));
        var secondDrilldown = secondViewer.SelectRows(insights.RequestsForSlot(slot));
        Assert.AreNotSame(firstHistory[0], secondHistory[0]);
        Assert.AreSame(firstHistory[0], firstViewer.Snapshot.Requests[0]);
        Assert.AreSame(firstHistory[0], firstDrilldown.Single(item => item.RequestNumber == firstHistory[0].RequestNumber));

        firstHistory[0].StatusCode = 503;
        firstHistory[0].UserId = "changed-by-first-viewer";
        firstHistory[0].RequestHeadersText = "UserID: changed-by-first-viewer\nModel: changed";
        firstHistory[0].ObservedUsageMetrics = Usage(("usage.vendor.accepted_tokens", 999));
        Assert.AreEqual(200, secondHistory[0].StatusCode);
        Assert.AreEqual("alice", secondHistory[0].UserId);
        Assert.AreEqual(20L, secondHistory[0].ObservedUsageMetrics!["usage.vendor.accepted_tokens"]);
        Assert.AreSame(secondHistory[0], secondDrilldown.Single(item => item.RequestNumber == secondHistory[0].RequestNumber));
        Assert.AreEqual(200, publication.Monitor.Requests[0].StatusCode);
        Assert.AreEqual("alice", publication.Monitor.Requests[0].UserId);
        Assert.AreEqual(20L, publication.Monitor.Requests[0].UsageMetrics["usage.vendor.accepted_tokens"]);
        Assert.AreSame(publication.Monitor, cache.Snapshot);
        Assert.AreSame(publication.Monitor.Requests[0], requests.FilteredRequests[0],
            "Shared query results contain immutable source records, not either viewer's mutable inspector row.");
        Assert.AreSame(publication.Monitor.Requests[0], insights.RequestsForSlot(slot)[0]);
        Assert.AreEqual(4L, insights.SelectedTrendMetrics.Single().Values.Sum());

        var freshAliceQuery = cache.GetRequests("all", "All envs", "alice", "All models");
        var freshUsage = cache.GetInsights(freshAliceQuery, "usage.vendor.accepted_tokens", new[] { "usage.vendor.accepted_tokens" });
        Assert.AreEqual(2, freshAliceQuery.HistoryItems.Count);
        Assert.AreEqual(30L, freshUsage.SelectedTrendMetrics.Single().Values.Sum(),
            "Even a newly computed query must use frozen source data rather than mutated circuit rows.");
        Assert.AreEqual(200, cache.CreateCircuitView().Snapshot.Requests[0].StatusCode);
    }

    [TestMethod]
    public void Cache_BoundedQueryEvictionDoesNotChangeRetainedViewerDataOrCircuitSelections() {
        var cache = ObservabilityProjectionCache.For(CreateUsagePublication());
        var originalRequests = cache.GetRequests("all", "All envs", "All users", "All models");
        var originalInsights = cache.GetInsights(originalRequests, "__calls", new[] { "__total_queries" });
        var viewer = cache.CreateCircuitView();
        var rows = viewer.SelectRows(originalRequests.HistoryItems);
        for (var index = 0; index < 65; index++) {
            cache.GetRequests("all", "All envs", $"uncaptured-user-{index}", "All models");
        }

        var recomputed = cache.GetRequests("all", "All envs", "All users", "All models");
        Assert.AreNotSame(originalRequests, recomputed, "Arbitrary user queries cannot grow the cache indefinitely.");
        Assert.AreEqual(4, originalRequests.HistoryItems.Count);
        Assert.AreEqual(4, rows.Count);
        Assert.AreEqual(4L, originalInsights.SelectedTrendMetrics.Single().Values.Sum());
        Assert.AreEqual(4, recomputed.HistoryItems.Count);
        Assert.AreSame(rows[0], viewer.SelectRows(recomputed.HistoryItems)[0], "Eviction must not replace circuit-owned inspector rows.");

        var retainedInsights = cache.GetInsights(recomputed, "__calls", new[] { "__total_queries" });
        for (var index = 0; index < 129; index++) {
            cache.GetInsights(recomputed, $"uncaptured-metric-{index}", Array.Empty<string>());
        }

        var recomputedInsights = cache.GetInsights(recomputed, "__calls", new[] { "__total_queries" });
        Assert.AreNotSame(retainedInsights, recomputedInsights);
        Assert.AreEqual("__total_queries", retainedInsights.SelectedTrendMetrics.Single().Key);
        Assert.AreEqual(4L, recomputedInsights.SelectedTrendMetrics.Single().Values.Sum());
    }

    private static ObservabilitySnapshot CreateUsagePublication() => CreatePublication(
        Request(1, "alice", "model-one", 200, "/one") with {
            UsageMetrics = Usage(("usage.vendor.accepted_tokens", 20), ("usage.zero", 0)),
        },
        Request(2, "alice", "model-one", 200, "/two") with {
            UsageMetrics = Usage(("usage.vendor.accepted_tokens", 10)),
        },
        Request(3, "bob", "model-one", 200, "/three") with {
            UsageMetrics = Usage(("usage.vendor.accepted_tokens", 10)),
        },
        Request(4, "charlie", "model-one", 200, "/four") with {
            ResponseBody = """{"usage":{"body_only":999,"vendor":{"accepted_tokens":999}}}""",
        });

    private static ImmutableDictionary<string, long> Usage(params (string Key, long Value)[] metrics) =>
        metrics.ToImmutableDictionary(metric => metric.Key, metric => metric.Value, StringComparer.OrdinalIgnoreCase);

    private static ObservedRequestSnapshot Request(int number, string user, string model, int? status, string path) => new() {
        RequestNumber = number,
        ContainerApp = "app-a",
        Replica = "r1",
        UserId = user,
        EventType = "S7P-ProxyRequest",
        StatusCode = status,
        RequestHeadersText = $"Path: {path}\nUserID: {user}\nModel: {model}",
        FinalizedAtUtc = Anchor,
    };

    private static ObservabilitySnapshot CreatePublication(params ObservedRequestSnapshot[] requests) => new() {
        Version = 1,
        PublishedUtc = Anchor.AddDays(1),
        Scopes = Scopes,
        MetricGroupsByScope = Scopes.ToImmutableDictionary(
            scope => scope.Id, _ => ImmutableArray<ProxyMetricsCatalog.MetricGroupSnapshot>.Empty, StringComparer.OrdinalIgnoreCase),
        Monitor = new ObservabilityMonitorSnapshot {
            TimestampUtc = Anchor,
            LastDataUtc = Anchor,
            HasData = requests.Length > 0,
            HasUserInfo = true,
            HasModelInfo = true,
            Stats = new RuntimeStatsSnapshot { TotalRequests = 999 },
            Backends = [new BackendHealthSnapshot { Name = "global-backend" }],
            Requests = requests.ToImmutableArray(),
        },
    };
}
