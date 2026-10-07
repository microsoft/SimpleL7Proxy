using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CompanionApp.Components.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Tokenomics;
using SimpleL7Proxy.Tokenomics.Llm;

namespace EventHubMonitorTests;

/// <summary>Exercises startup validation, local cadence and immutable replay aggregation.</summary>
[TestClass]
public sealed class TokenomicsReplayTests {
    private string _directory = null!;

    [TestInitialize]
    public void Initialize() {
        _directory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", $"tokenomics-replay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    [TestMethod]
    public void Arguments_PreserveNormalAndUiOnlyFlags_AndRejectInvalidReplay() {
        var normal = TokenomicsReplayOptions.Parse(["--uionly", "--urls", "http://localhost:5259"]);
        Assert.IsNull(normal.FileName);
        CollectionAssert.AreEqual(new[] { "--uionly", "--urls", "http://localhost:5259" }, normal.ApplicationArgs);
        var file = WriteFile("");
        var replay = TokenomicsReplayOptions.Parse(["--uionly", "--run", "events", file, "--urls", "http://localhost:5259"]);
        CollectionAssert.AreEqual(normal.ApplicationArgs, replay.ApplicationArgs);
        Assert.AreEqual(file, replay.FileName);
        foreach (var args in new[] {
            new[] { "--run" }, new[] { "--run", "events" }, new[] { "--run", "other", file },
            new[] { "--run", "events", "--uionly" }, new[] { "--run=events" },
            new[] { "--run", "events", file, "--run", "events", file }
        }) {
            Assert.ThrowsException<ArgumentException>(() => TokenomicsReplayOptions.Parse(args));
        }
    }

    [TestMethod]
    public void FileValidation_RejectsMissingAndMalformedFilesBeforeAnyReplay() {
        var missing = Assert.ThrowsException<ArgumentException>(() =>
            TokenomicsReplayOptions.Parse(["--run", "events", Path.Combine(_directory, "missing.ndjson")]));
        StringAssert.Contains(missing.Message, "Cannot read replay file");
        foreach (var bad in new[] {
            "not json", "[]", "{}", "{\"Type\":\"S7P-Tokenomics\",\"MID\":\"a\",\"RecordKind\":\"other\"}",
            Event("a", "RequestOutcome", input: "-1"), Event("a", "RequestOutcome", cached: "101"),
            Event("a", "RequestOutcome", status: "OK"), Event("a", "RequestOutcome", timestamp: "bad"),
            "{\"Type\":\"S7P-Tokenomics\",\"MID\":\"a\",\"RecordKind\":\"PolicyDecision\"}",
            "{\"Type\":\"S7P-Tokenomics\",\"MID\":\"a\",\"SchemaVersion\":\"1\"}",
            "{\"Type\":\"S7P-Tokenomics\",\"MID\":\"a\",\"RecordKind\":\"\"}",
            "{\"Type\":\"S7P-Tokenomics\",\"RecordKind\":\"RequestOutcome\",\"Status\":\"200\"}"
        }) {
            var file = WriteFile(Event("valid", "RequestOutcome") + "\n" + bad);
            var error = Assert.ThrowsException<FormatException>(() => TokenomicsReplayOptions.Parse(["--run", "events", file]));
            StringAssert.Contains(error.Message, "line 2");
            StringAssert.Contains(error.Message, file);
        }
    }

    [TestMethod]
    public async Task Startup_InvalidArgumentsAndFilesExitWithUsefulErrors() {
        foreach (var args in new[] {
            new[] { "--run", "events" },
            new[] { "--uionly", "--run", "events", Path.Combine(_directory, "missing.ndjson") },
            new[] { "--run", "events", WriteFile("broken json") }
        }) {
            var start = new ProcessStartInfo("dotnet") {
                RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false
            };
            start.ArgumentList.Add(typeof(TokenomicsEventReplay).Assembly.Location);
            foreach (var argument in args) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(1, process.ExitCode);
            StringAssert.Contains((await error).ToLowerInvariant(), "replay");
            Assert.IsFalse((await output).Contains("Now listening", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task Startup_UiOnlyReplay_RendersFinalDataWithoutSyntheticOrInvalidQuotaWidths() {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var assembly = typeof(TokenomicsEventReplay).Assembly.Location;
        var start = new ProcessStartInfo("dotnet") {
            WorkingDirectory = Path.GetDirectoryName(assembly)!,
            RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false
        };
        foreach (var argument in new[] { assembly, "--uionly", "--run", "events",
            WriteFile(Event("rendered", "RequestOutcome")), "--urls", $"http://127.0.0.1:{port}" }) {
            start.ArgumentList.Add(argument);
        }
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["CompanionApp__History__Mode"] = "Disk";
        start.Environment["CompanionApp__Conversations__Mode"] = "Disk";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            string html = "";
            var timeout = Stopwatch.StartNew();
            while (timeout.Elapsed < TimeSpan.FromSeconds(20) && !process.HasExited) {
                try {
                    html = await client.GetStringAsync($"http://127.0.0.1:{port}/tokenomics");
                    if (html.Contains("1/1 events", StringComparison.Ordinal)) break;
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                await Task.Delay(100);
            }
            StringAssert.Contains(html, "1/1 events");
            StringAssert.Contains(html, "EOF");
            StringAssert.Contains(html, "120");
            StringAssert.Contains(html, "no price data");
            Assert.IsFalse(html.Contains("Sample data", StringComparison.Ordinal));
            Assert.IsFalse(html.Contains("vs. previous period", StringComparison.Ordinal));
            Assert.IsFalse(html.Contains("width: Unavailable", StringComparison.Ordinal));
            Assert.IsFalse(html.Contains("Increasing usage trend", StringComparison.Ordinal));
        }
        finally {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        StringAssert.Contains(await output, "UI-only mode");
        Assert.IsFalse((await error).Contains("Unhandled exception", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Replay_PublishesTenEachSecond_StopsAtEof_AndRetainsImmutableFinalSnapshot() {
        var options = TokenomicsReplayOptions.Parse(["--run", "events",
            WriteFile(string.Join("\n", Enumerable.Range(0, 23).Select(index => Event($"request-{index}", "RequestOutcome"))))]);
        using var store = new TokenomicsDashboardStore();
        var clock = new ReplayClock();
        using var replay = new TokenomicsEventReplay(options, store, NullLogger<TokenomicsEventReplay>.Instance, clock);
        var initial = store.GetSnapshot();
        Assert.IsFalse(initial.IsSampleData);
        Assert.IsTrue(initial.Users.IsEmpty);
        Assert.IsTrue(initial.Trend.IsEmpty);
        Assert.IsTrue(initial.Models.IsEmpty);
        Assert.IsTrue(initial.Reports.IsEmpty);
        Assert.IsTrue(initial.TenantSpend.IsEmpty);
        Assert.IsTrue(initial.Quotas.IsEmpty);
        await replay.StartAsync(CancellationToken.None);
        await WaitUntil(() => clock.HasTimer);
        Assert.AreSame(initial, store.GetSnapshot(), "No events before the first one-second tick.");
        clock.AdvanceSecond();
        await WaitUntil(() => Metric(store, "Total Requests") == "10");
        var first = store.GetSnapshot();
        Assert.AreEqual("1,200", first.TotalTokens);
        clock.AdvanceSecond();
        await WaitUntil(() => Metric(store, "Total Requests") == "20");
        clock.AdvanceSecond();
        await replay.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        var final = store.GetSnapshot();
        StringAssert.Contains(final.SnapshotLabel, "23/23 events · EOF");
        Assert.AreEqual("2,760", final.TotalTokens);
        Assert.AreEqual("10", first.Metrics.Single(metric => metric.Name == "Total Requests").Value);
        Assert.AreEqual(0, initial.Trend.Length);
        clock.AdvanceSecond();
        await Task.Delay(1100);
        Assert.AreSame(final, store.GetSnapshot(), "EOF must neither loop nor restart synthetic updates.");
        await replay.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task OrdinaryTokenomicsLogs_ConsumeCadenceWithoutContributingToAggregation() {
        var ordinary = JsonSerializer.Serialize(new Dictionary<string, string> {
            ["Type"] = "S7P-Tokenomics", ["MID"] = "rejected", ["Status"] = "403",
            ["Error"] = "Request Rejected", ["UserId"] = "ordinary-user", ["Tenant"] = "ordinary-tenant",
            ["RequestedModel"] = "ordinary-model", ["InputTokens"] = "999", ["TimestampUtc"] = "not-a-summary-timestamp"
        });
        var records = Enumerable.Repeat(ordinary, 9)
            .Append("{\"Type\":\"S7P-Tokenomics\",\"Status\":\"403\",\"Error\":\"Request Rejected\"}")
            .Append(Event("outcome", "RequestOutcome"))
            .Append(Event("decision", "PolicyDecision"));
        var options = TokenomicsReplayOptions.Parse(["--run", "events", WriteFile(string.Join("\n", records))]);
        Assert.AreEqual(12, options.Events.Length);
        using var store = new TokenomicsDashboardStore();
        var clock = new ReplayClock();
        using var replay = new TokenomicsEventReplay(options, store, NullLogger<TokenomicsEventReplay>.Instance, clock);
        await replay.StartAsync(CancellationToken.None);
        await WaitUntil(() => clock.HasTimer);
        clock.AdvanceSecond();
        await WaitUntil(() => store.GetSnapshot().SnapshotLabel.Contains("10/12 events", StringComparison.Ordinal));
        var first = store.GetSnapshot();
        Assert.AreEqual("0", first.TotalTokens);
        Assert.AreEqual("0", Metric(store, "Total Requests"));
        Assert.AreEqual("0", Metric(store, "Policy Actions"));
        Assert.IsTrue(first.Users.IsEmpty && first.Models.IsEmpty && first.Trend.IsEmpty);
        Assert.IsFalse(replay.ExecuteTask!.IsCompleted);
        clock.AdvanceSecond();
        await replay.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(5));
        var final = store.GetSnapshot();
        StringAssert.Contains(final.SnapshotLabel, "12/12 events · EOF");
        Assert.AreEqual("120", final.TotalTokens);
        Assert.AreEqual("2", Metric(store, "Total Requests"));
        Assert.AreEqual("100.0%", Metric(store, "Success Rate"));
        Assert.AreEqual("1", Metric(store, "Policy Actions"));
        Assert.AreEqual("1", Metric(store, "429 Throttles"));
        Assert.AreEqual(1, final.Users.Length);
        Assert.AreEqual(1, final.Models.Length);
        Assert.IsFalse(final.Tenants.Contains("ordinary-tenant"));
    }

    [TestMethod]
    public async Task Cancellation_BeforeFirstTickAndBetweenBatches_IsNotFaulted() {
        foreach (var submitFirstBatch in new[] { false, true }) {
            var options = TokenomicsReplayOptions.Parse(["--run", "events",
                WriteFile(string.Join("\n", Enumerable.Range(0, 15).Select(index => Event(index.ToString(), "RequestOutcome"))))]);
            using var store = new TokenomicsDashboardStore();
            var clock = new ReplayClock();
            using var replay = new TokenomicsEventReplay(options, store, NullLogger<TokenomicsEventReplay>.Instance, clock);
            await replay.StartAsync(CancellationToken.None);
            await WaitUntil(() => clock.HasTimer);
            if (submitFirstBatch) {
                clock.AdvanceSecond();
                await WaitUntil(() => Metric(store, "Total Requests") == "10");
            }
            var last = store.GetSnapshot();
            await replay.StopAsync(CancellationToken.None);
            Assert.IsTrue(replay.ExecuteTask!.IsCompletedSuccessfully);
            clock.AdvanceSecond();
            Assert.AreSame(last, store.GetSnapshot());
            Assert.IsFalse(last.IsSampleData);
        }
    }

    [TestMethod]
    public async Task ProductionSample_950OutcomesAnd50Decisions_HasNoInventedUsageOrPrices() {
        string[] models = ["gpt-4o", "gpt-4o-mini", "gpt-5", "gpt-5-mini"];
        var actions = Enum.GetValues<TokenActionEnum>();
        var records = Enumerable.Range(0, 1000).Select(index => {
            var summary = new TokenomicsSummaryEvent($"request-{index}", 1, $"user-{index % 20}",
                $"tenant-{index % 4}", models[index % 4], models[index % 4], 2, "sample", actions[index % actions.Length]);
            if (index < 950) {
                summary.PrepForFinalStats(HttpStatusCode.OK, TimeSpan.FromMilliseconds(100), "backend",
                    models[index % 4], 1, 1, new LLMStats { InputTokens = 100, CachedTokens = 25, OutputTokens = 20 });
            }
            else if (actions[index % actions.Length] == TokenActionEnum.Throttle) {
                summary.SetLocalStatus(429);
            }
            return ProxyEvent.ConvertToJson(summary, new Dictionary<string, string> {
                ["MID"] = summary.MID!, ["Type"] = "S7P-Tokenomics",
                ["Status"] = ((int)summary.Status).ToString(CultureInfo.InvariantCulture)
            });
        }).ToArray();
        var options = TokenomicsReplayOptions.Parse(["--run", "events", WriteFile(string.Join("\n", records))]);
        using var store = new TokenomicsDashboardStore();
        var clock = new ReplayClock();
        using var replay = new TokenomicsEventReplay(options, store, NullLogger<TokenomicsEventReplay>.Instance, clock);
        await replay.StartAsync(CancellationToken.None);
        await WaitUntil(() => clock.HasTimer);
        for (var batch = 1; batch <= 100; batch++) {
            clock.AdvanceSecond();
            await WaitUntil(() => store.GetSnapshot().SnapshotLabel.Contains($"{batch * 10}/1000 events", StringComparison.Ordinal));
        }
        await replay.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        var snapshot = store.GetSnapshot();
        Assert.AreEqual("114,000", snapshot.TotalTokens);
        Assert.AreEqual("1,000", Metric(store, "Total Requests"));
        Assert.AreEqual("100.0%", Metric(store, "Success Rate"));
        Assert.AreEqual("50", Metric(store, "Policy Actions"));
        Assert.AreEqual(Enumerable.Range(950, 50).Count(index => actions[index % actions.Length] == TokenActionEnum.Throttle).ToString(), Metric(store, "429 Throttles"));
        Assert.AreEqual(20, snapshot.Users.Length);
        Assert.AreEqual(5, snapshot.Tenants.Length);
        Assert.AreEqual(4, snapshot.Models.Length);
        Assert.AreEqual(950 * 75, snapshot.Trend.Sum(point => point.InputNet));
        Assert.AreEqual(950 * 25, snapshot.Trend.Sum(point => point.Cached));
        Assert.AreEqual(950 * 20, snapshot.Trend.Sum(point => point.Output));
        Assert.IsTrue(snapshot.Metrics.All(metric => metric.Change == ""));
        Assert.AreEqual("Unavailable", Metric(store, "Total Spend (USD)"));
        Assert.IsTrue(snapshot.Users.All(user => user.Spend == "Unavailable" && user.Quota == "Unavailable" && user.Sparkline == ""));
        Assert.IsTrue(snapshot.SpendLine.IsEmpty);
        Assert.IsTrue(snapshot.SpendAxisTicks.IsEmpty);
        Assert.IsTrue(snapshot.TenantSpend.IsEmpty);
        Assert.IsTrue(snapshot.Quotas.IsEmpty);
        Assert.IsFalse(snapshot.ModelGradient.Contains("NaN", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ModelSubstitution_ShowsRequestedAndEffectiveModels_AndZeroUsageIsSafe() {
        var records = new[] {
            Event("routed", "RequestOutcome", model: "requested", effectiveModel: "effective"),
            Event("zero", "RequestOutcome", input: "0", cached: "0", model: "zero-model", output: "0")
        };
        using var store = new TokenomicsDashboardStore();
        var clock = new ReplayClock();
        using var replay = new TokenomicsEventReplay(TokenomicsReplayOptions.Parse(["--run", "events", WriteFile(string.Join("\n", records))]),
            store, NullLogger<TokenomicsEventReplay>.Instance, clock);
        await replay.StartAsync(CancellationToken.None);
        await WaitUntil(() => clock.HasTimer);
        clock.AdvanceSecond();
        await replay.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        var snapshot = store.GetSnapshot();
        Assert.AreEqual("0", snapshot.Models.Single(model => model.Name == "requested").Tokens);
        Assert.AreEqual("120", snapshot.Models.Single(model => model.Name == "effective").Tokens);
        Assert.AreEqual("0.0%", snapshot.Models.Single(model => model.Name == "zero-model").Share);
        Assert.AreEqual("100.0%", Metric(store, "Success Rate"));
        Assert.IsTrue(snapshot.TokenAxisMaximum > 0);
    }

    [TestMethod]
    public async Task DecisionsAndOutcomes_SharingMid_DoNotDoubleCountRequestsOrThrottles() {
        var records = new[] {
            Event("same", "PolicyDecision", action: "Throttle"),
            Event("same", "RequestOutcome", status: "429"),
            Event("same", "RequestOutcome", status: "429"),
            Event("decision-only", "PolicyDecision", action: "Bypass", user: "only-user", tenant: "only-tenant", model: "only-model")
        };
        using var store = new TokenomicsDashboardStore();
        var clock = new ReplayClock();
        using var replay = new TokenomicsEventReplay(TokenomicsReplayOptions.Parse(["--run", "events", WriteFile(string.Join("\n", records))]),
            store, NullLogger<TokenomicsEventReplay>.Instance, clock);
        await replay.StartAsync(CancellationToken.None);
        await WaitUntil(() => clock.HasTimer);
        clock.AdvanceSecond();
        await replay.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        var snapshot = store.GetSnapshot();
        Assert.AreEqual("2", Metric(store, "Total Requests"));
        Assert.AreEqual("0.0%", Metric(store, "Success Rate"));
        Assert.AreEqual("2", Metric(store, "Policy Actions"));
        Assert.AreEqual("1", Metric(store, "429 Throttles"));
        Assert.AreEqual("120", snapshot.TotalTokens);
        Assert.IsTrue(snapshot.Users.Any(user => user.Name == "only-user" && user.Total == "0"));
        Assert.IsTrue(snapshot.Tenants.Contains("only-tenant"));
        Assert.IsTrue(snapshot.Models.Any(model => model.Name == "only-model" && model.Share == "0.0%"));
    }

    [TestMethod]
    public async Task EmptyAndDecisionOnlyReplay_HavePositiveAxisAndNoSampleFallback() {
        foreach (var content in new[] { "", Event("only", "PolicyDecision"), Event("zero", "RequestOutcome", input: "0", cached: "0", output: "0") }) {
            using var store = new TokenomicsDashboardStore();
            var clock = new ReplayClock();
            using var replay = new TokenomicsEventReplay(TokenomicsReplayOptions.Parse(["--run", "events", WriteFile(content)]),
                store, NullLogger<TokenomicsEventReplay>.Instance, clock);
            await replay.StartAsync(CancellationToken.None);
            if (content.Length > 0) {
                await WaitUntil(() => clock.HasTimer);
                clock.AdvanceSecond();
            }
            await replay.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
            var snapshot = store.GetSnapshot();
            Assert.AreEqual("0", snapshot.TotalTokens);
            Assert.IsTrue(snapshot.TokenAxisMaximum > 0 && double.IsFinite(snapshot.TokenAxisMaximum));
            Assert.AreEqual(content.Contains("RequestOutcome", StringComparison.Ordinal) ? "100.0%" : "Unavailable", Metric(store, "Success Rate"));
            Assert.IsFalse(snapshot.IsSampleData);
            Assert.IsTrue(snapshot.Trend.All(point => point.InputNet + point.Cached + point.Output == 0));
            Assert.IsTrue(snapshot.Reports.IsEmpty);
            Assert.IsFalse(snapshot.ModelGradient.Contains("NaN", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task NormalStore_ContinuesOneSecondSyntheticUpdates() {
        using var store = new TokenomicsDashboardStore();
        var initial = store.GetSnapshot();
        await WaitUntil(() => !ReferenceEquals(initial, store.GetSnapshot()));
        Assert.IsTrue(store.GetSnapshot().IsSampleData);
        Assert.IsFalse(store.GetSnapshot().Users.IsEmpty);
    }

    private string WriteFile(string content) {
        var file = Path.Combine(_directory, $"{Guid.NewGuid():N}.ndjson");
        File.WriteAllText(file, content);
        return file;
    }

    private static string Metric(TokenomicsDashboardStore store, string name) =>
        store.GetSnapshot().Metrics.Single(metric => metric.Name == name).Value;

    private static string Event(string mid, string kind, string status = "200", string input = "100", string cached = "25",
        string action = "Throttle", string timestamp = "2026-01-01T00:00:00Z", string user = "user", string tenant = "tenant", string model = "model", string output = "20", string effectiveModel = "") =>
        JsonSerializer.Serialize(new Dictionary<string, string> {
            ["Type"] = "S7P-Tokenomics", ["MID"] = mid, ["RecordKind"] = kind,
            ["StatusCode"] = status, ["Status"] = status, ["InputTokens"] = input, ["CachedTokens"] = cached,
            ["OutputTokens"] = output, ["PolicyAction"] = action, ["TimestampUtc"] = timestamp,
            ["UserId"] = user, ["Tenant"] = tenant, ["RequestedModel"] = model, ["EffectiveModel"] = effectiveModel
        });

    private static async Task WaitUntil(Func<bool> condition) {
        var timeout = Stopwatch.StartNew();
        while (!condition() && timeout.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(5);
        Assert.IsTrue(condition(), "Timed out waiting for replay state.");
    }

    private sealed class ReplayClock : TimeProvider {
        private ReplayTimer? _timer;
        public bool HasTimer => Volatile.Read(ref _timer) is { Active: true };
        public void AdvanceSecond() => Volatile.Read(ref _timer)?.Tick();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
            var timer = new ReplayTimer(callback, state);
            Volatile.Write(ref _timer, timer);
            return timer;
        }

        private sealed class ReplayTimer(TimerCallback callback, object? state) : ITimer {
            private int _active = 1;
            public bool Active => Volatile.Read(ref _active) == 1;
            public void Tick() { if (Active) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => Active;
            public void Dispose() => Interlocked.Exchange(ref _active, 0);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
