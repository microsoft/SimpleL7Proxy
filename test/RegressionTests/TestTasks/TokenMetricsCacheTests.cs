using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Options;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.Tokenomics;

namespace SimpleL7Proxy.Test;

/// <summary>Regression coverage for queued token accounting, model pricing, and throttle deadlines.</summary>
[TestClass]
[TestCategory("Tokenomics")]
public sealed class TokenMetricsCacheTests : IRegressionTestMetadata {
    public IReadOnlyDictionary<string, RegressionFeature> RegressionFeatures { get; } =
        new Dictionary<string, RegressionFeature> {
            ["token-rollup"] = new(
                "Tokenomics",
                "Queued token accounting",
                "Keeps per-user token and spending totals accurate while deferring calculation to queue rollup."),
            ["token-periods"] = new(
                "Tokenomics",
                "UTC accounting periods",
                "Attributes queued usage to its original UTC day and month and removes expired period totals."),
            ["model-throttling"] = new(
                "Tokenomics",
                "Model throttle deadlines",
                "Makes models unavailable only while their retry deadline is in the future.")
        };

    /// <summary>Checks that only the rollup publishes per-user quota and spending totals.</summary>
    [TestMethod]
    [RegressionTestCase("token-rollup", "Queued usage becomes visible at rollup", "Daily and monthly totals must exclude pending metrics, then include input and output tokens across models without mixing users.")]
    public async Task AddMetric_LeavesPeriodicTotalsUnchangedUntilRollup() {
        var settings = new TokenomicsSettings {
            ModelCostPerToken = new() { ["model-a"] = 0.01m, ["model-b"] = 0.02m }
        };
        using var cache = new TokenMetricsCache(Options.Create(new ProxyConfig()), settings);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        cache.AddMetric("alice", "model-a", 100, 50);
        cache.AddMetric("alice", "model-b", 40, 10);
        cache.AddMetric("bob", "model-a", 10, 10);

        Assert.AreEqual(150L, cache.GetTokenBalance("alice", "model-a"));
        Assert.AreEqual(0L, cache.GetDailyTokenBalance("alice"));
        Assert.AreEqual(0L, cache.GetMonthlyTokenBalance("alice"));
        Assert.AreEqual(0m, cache.GetDailyBudgetUsage("alice"));
        Assert.AreEqual(0m, cache.GetMonthlyBudgetUsage("alice"));

        await cache.StartAsync(timeout.Token);
        await cache.StopAsync(timeout.Token);

        Assert.AreEqual(150L, cache.GetTokenBalance("alice", "model-a"));
        Assert.AreEqual(50L, cache.GetTokenBalance("alice", "model-b"));
        Assert.AreEqual(200L, cache.GetDailyTokenBalance("alice"));
        Assert.AreEqual(200L, cache.GetMonthlyTokenBalance("alice"));
        Assert.AreEqual(2.5m, cache.GetDailyBudgetUsage("alice"));
        Assert.AreEqual(2.5m, cache.GetMonthlyBudgetUsage("alice"));
        Assert.AreEqual(20L, cache.GetDailyTokenBalance("bob"));
        Assert.AreEqual(20L, cache.GetMonthlyTokenBalance("bob"));
        Assert.AreEqual(0.2m, cache.GetDailyBudgetUsage("bob"));
        Assert.AreEqual(0.2m, cache.GetMonthlyBudgetUsage("bob"));
    }

    /// <summary>Checks that models with no retry deadline are immediately usable.</summary>
    [TestMethod]
    [RegressionTestCase("model-throttling", "Unthrottled models are available", "A model absent from the throttle cache must not be treated as unavailable.")]
    public void IsModelAvailable_WithoutThrottle_ReturnsTrue() {
        using var cache = new TokenMetricsCache(Options.Create(new ProxyConfig()), new TokenomicsSettings());

        Assert.IsTrue(cache.IsModelAvailable("model-a"));
    }

    /// <summary>Checks that live and rolled-up totals use the same 64-bit token arithmetic.</summary>
    [TestMethod]
    [RegressionTestCase("token-rollup", "Large live token totals do not overflow", "Input plus output counts exceeding Int32.MaxValue must retain their full value before and after rollup.")]
    public async Task GetTokenBalance_LargeCounts_DoesNotOverflowBeforeRollup() {
        using var cache = new TokenMetricsCache(Options.Create(new ProxyConfig()), new TokenomicsSettings());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var expected = 2L * int.MaxValue;

        cache.AddMetric("alice", "model-a", int.MaxValue, int.MaxValue);

        Assert.AreEqual(expected, cache.GetTokenBalance("alice", "model-a"));

        await cache.StartAsync(timeout.Token);
        await cache.StopAsync(timeout.Token);

        Assert.AreEqual(expected, cache.GetTokenBalance("alice", "model-a"));
        Assert.AreEqual(expected, cache.GetDailyTokenBalance("alice"));
        Assert.AreEqual(expected, cache.GetMonthlyTokenBalance("alice"));
    }

    /// <summary>Checks that missing accounting entries have zero usage.</summary>
    [TestMethod]
    [RegressionTestCase("token-rollup", "Unknown users and models have no usage", "An empty cache must return zero for live tokens, daily and monthly quotas, and spending.")]
    public void GetBalances_WithoutMetrics_ReturnZero() {
        using var cache = CreateCache();

        Assert.AreEqual(0L, cache.GetTokenBalance("alice", "model-a"));
        Assert.AreEqual(0L, cache.GetDailyTokenBalance("alice"));
        Assert.AreEqual(0L, cache.GetMonthlyTokenBalance("alice"));
        Assert.AreEqual(0m, cache.GetDailyBudgetUsage("alice"));
        Assert.AreEqual(0m, cache.GetMonthlyBudgetUsage("alice"));
    }

    /// <summary>Checks that 429 interval, 429 count, and latency trend are tracked in rolling windows.</summary>
    [TestMethod]
    [RegressionTestCase("token-rollup", "429 interval and latency trend tracking", "Request samples must accumulate 429 gaps and latency drift so operators can compare current latency against the norm and see whether throttling is increasing.")]
    public void RecordRequestOutcome_Tracks429RateAndLatencyDrift() {
        using var cache = CreateCache();
        var now = DateTime.UtcNow;

        var samples = new[] {
            (now.AddSeconds(-5), "alice", "model-a", 200, 100d),
            (now.AddSeconds(-4), "alice", "model-a", 429, 140d),
            (now.AddSeconds(-3), "alice", "model-a", 200, 120d),
            (now.AddSeconds(-2), "alice", "model-a", 429, 180d),
            (now.AddSeconds(-1), "alice", "model-a", 200, 110d)
        };

        foreach (var sample in samples)
        {
            cache.RecordRequestOutcome(sample.Item2, sample.Item3, sample.Item4, sample.Item5);
        }

        Assert.AreEqual(2d, Math.Round(cache.Get429Rate(TimeSpan.FromSeconds(20), "alice"), 1));
        Assert.AreEqual(2, cache.Get429Count(TimeSpan.FromSeconds(20), "alice"));
        Assert.AreEqual(130d, Math.Round(cache.GetAverageLatencyMs(TimeSpan.FromSeconds(20), "alice"), 0));
        Assert.AreEqual(30d, Math.Round(cache.GetLatencyDeltaPercent(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10), "alice"), 0));
    }

    /// <summary>Checks that unpriced and free models still contribute to token quotas.</summary>
    [TestMethod]
    [RegressionTestCase("token-rollup", "Unpriced models consume quota without spend", "Usage from unpriced and zero-cost models must count towards both quotas while only priced usage contributes to spending.")]
    public async Task Rollup_UnpricedModels_CountTokensWithoutCharging() {
        using var cache = CreateCache(new TokenomicsSettings {
            ModelCostPerToken = new() { ["paid"] = 0.01m, ["free"] = 0m }
        });
        cache.AddMetric("alice", "unpriced", 500, 250);
        cache.AddMetric("alice", "free", 3, 1);
        cache.AddMetric("alice", "paid", 2, 1);

        await RollupAsync(cache);

        Assert.AreEqual(750L, cache.GetTokenBalance("alice", "unpriced"));
        Assert.AreEqual(757L, cache.GetDailyTokenBalance("alice"));
        Assert.AreEqual(757L, cache.GetMonthlyTokenBalance("alice"));
        Assert.AreEqual(0.03m, cache.GetDailyBudgetUsage("alice"));
        Assert.AreEqual(0.03m, cache.GetMonthlyBudgetUsage("alice"));
        Assert.AreEqual(0L, cache.GetTokenBalance("bob", "unpriced"));
        Assert.AreEqual(0L, cache.GetTokenBalance("alice", "unknown"));
    }

    /// <summary>Checks that pricing is applied by the consumer, not captured by the producer.</summary>
    [TestMethod]
    [RegressionTestCase("token-rollup", "Rollup applies the current model price", "Changing the configured rate after enqueue must affect the eventual spending calculation, leaving enqueue free of pricing work.")]
    public async Task Rollup_UsesModelPriceAtCollapse() {
        var settings = new TokenomicsSettings {
            ModelCostPerToken = new() { ["model-a"] = 0.01m }
        };
        using var cache = CreateCache(settings);
        cache.AddMetric("alice", "model-a", 100, 50);
        settings.ModelCostPerToken["model-a"] = 0.02m;

        await RollupAsync(cache);

        Assert.AreEqual(150L, cache.GetDailyTokenBalance("alice"));
        Assert.AreEqual(3m, cache.GetDailyBudgetUsage("alice"));
        Assert.AreEqual(3m, cache.GetMonthlyBudgetUsage("alice"));
    }

    /// <summary>Checks concurrent additions without combining different users or model balances.</summary>
    [TestMethod]
    [RegressionTestCase("token-rollup", "Concurrent producers retain every metric", "Concurrent additions across two users and two models must produce exact token and decimal spending totals after rollup.")]
    public async Task Rollup_ConcurrentProducers_IsolatesUsersAndModels() {
        using var cache = CreateCache(new TokenomicsSettings {
            ModelCostPerToken = new() { ["model-a"] = 0.01m, ["model-b"] = 0.02m }
        });

        Parallel.For(0, 1000, iteration => {
            cache.AddMetric("alice", "model-a", 1, 2);
            cache.AddMetric("alice", "model-b", 3, 4);
            cache.AddMetric("bob", "model-a", 5, 6);
        });

        await RollupAsync(cache);

        Assert.AreEqual(3000L, cache.GetTokenBalance("alice", "model-a"));
        Assert.AreEqual(7000L, cache.GetTokenBalance("alice", "model-b"));
        Assert.AreEqual(10000L, cache.GetDailyTokenBalance("alice"));
        Assert.AreEqual(10000L, cache.GetMonthlyTokenBalance("alice"));
        Assert.AreEqual(170m, cache.GetDailyBudgetUsage("alice"));
        Assert.AreEqual(170m, cache.GetMonthlyBudgetUsage("alice"));
        Assert.AreEqual(11000L, cache.GetTokenBalance("bob", "model-a"));
        Assert.AreEqual(11000L, cache.GetDailyTokenBalance("bob"));
        Assert.AreEqual(11000L, cache.GetMonthlyTokenBalance("bob"));
        Assert.AreEqual(110m, cache.GetDailyBudgetUsage("bob"));
        Assert.AreEqual(110m, cache.GetMonthlyBudgetUsage("bob"));
    }

    /// <summary>Checks timer-driven rollup, repeated starts, and final draining without duplicate totals.</summary>
    [TestMethod]
    [RegressionTestCase("token-rollup", "Periodic and final rollup count usage once", "The running service must publish pending usage, accept the next batch, and drain it at shutdown without double counting on repeated lifecycle calls.")]
    public async Task StartAsync_PeriodicallyRollsQueuesWithoutDoubleCounting() {
        using var cache = CreateCache(new TokenomicsSettings {
            ModelCostPerToken = new() { ["model-a"] = 0.01m }
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        cache.AddMetric("alice", "model-a", 200, 100);

        await cache.StartAsync(timeout.Token);
        try {
            await cache.StartAsync(timeout.Token);
            Assert.IsTrue(
                SpinWait.SpinUntil(() => cache.GetMonthlyBudgetUsage("alice") == 3m, TimeSpan.FromSeconds(5)),
                "The periodic consumer did not publish the first batch within five seconds.");
            Assert.AreEqual(300L, cache.GetDailyTokenBalance("alice"));
            cache.AddMetric("alice", "model-a", 25, 75);
        }
        finally {
            await cache.StopAsync(timeout.Token);
        }

        await cache.StopAsync(timeout.Token);

        Assert.AreEqual(400L, cache.GetTokenBalance("alice", "model-a"));
        Assert.AreEqual(400L, cache.GetDailyTokenBalance("alice"));
        Assert.AreEqual(400L, cache.GetMonthlyTokenBalance("alice"));
        Assert.AreEqual(4m, cache.GetDailyBudgetUsage("alice"));
        Assert.AreEqual(4m, cache.GetMonthlyBudgetUsage("alice"));
    }

    /// <summary>Checks shutdown with pending writes in both sides of the double buffer.</summary>
    [TestMethod]
    [RegressionTestCase("token-rollup", "Shutdown drains both metric buffers", "A producer can retain the old queue index across a swap; shutdown must include pending metrics in either buffer after producers finish.")]
    public async Task StopAsync_DrainsBothMetricBuffers() {
        using var cache = CreateCache(new TokenomicsSettings {
            ModelCostPerToken = new() { ["model-a"] = 0.01m, ["model-b"] = 0.02m }
        });
        var queues = GetCacheField<ConcurrentQueue<(string UserId, string Model, int InputTokens, int OutputTokens, DateOnly Day)>[]>(cache, "_metrics");
        cache.AddMetric("alice", "model-a", 100, 50);
        queues[1].Enqueue(("alice", "model-b", 40, 10, DateOnly.FromDateTime(DateTime.UtcNow)));

        await RollupAsync(cache);

        Assert.AreEqual(200L, cache.GetDailyTokenBalance("alice"));
        Assert.AreEqual(200L, cache.GetMonthlyTokenBalance("alice"));
        Assert.AreEqual(2.5m, cache.GetDailyBudgetUsage("alice"));
        Assert.AreEqual(2.5m, cache.GetMonthlyBudgetUsage("alice"));
        Assert.IsTrue(queues.All(queue => queue.IsEmpty), "Shutdown left queued metrics outside the rollup totals.");
    }

    /// <summary>Checks delayed metrics retain their UTC period and stale totals are removed.</summary>
    [DataTestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(-32)]
    [RegressionTestCase("token-periods", "Queued UTC date controls accounting ({0} days)", "A metric dated {0} days from today must retain its original day and month rather than being charged to the rollup date; expired period entries must be removed.")]
    public async Task Rollup_UsesQueuedUtcDateAndRemovesExpiredPeriods(int dayOffset) {
        using var cache = CreateCache(new TokenomicsSettings {
            ModelCostPerToken = new() { ["model-a"] = 0.25m }
        });
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var metricDay = today.AddDays(dayOffset);
        var queues = GetCacheField<ConcurrentQueue<(string UserId, string Model, int InputTokens, int OutputTokens, DateOnly Day)>[]>(cache, "_metrics");
        queues[0].Enqueue(("alice", "model-a", 8, 4, metricDay));

        await RollupAsync(cache);

        var expectedDaily = metricDay == today ? 12L : 0L;
        var expectedMonthly = metricDay.Year == today.Year && metricDay.Month == today.Month ? 12L : 0L;
        Assert.AreEqual(12L, cache.GetTokenBalance("alice", "model-a"));
        Assert.AreEqual(expectedDaily, cache.GetDailyTokenBalance("alice"));
        Assert.AreEqual(expectedMonthly, cache.GetMonthlyTokenBalance("alice"));
        Assert.AreEqual(expectedDaily == 0 ? 0m : 3m, cache.GetDailyBudgetUsage("alice"));
        Assert.AreEqual(expectedMonthly == 0 ? 0m : 3m, cache.GetMonthlyBudgetUsage("alice"));
        Assert.AreEqual(expectedDaily == 0 ? 0 : 1, GetCacheField<ConcurrentDictionary<(string UserId, DateOnly Day), long>>(cache, "_dailyTokenBalance").Count);
        Assert.AreEqual(expectedDaily == 0 ? 0 : 1, GetCacheField<ConcurrentDictionary<(string UserId, DateOnly Day), decimal>>(cache, "_dailyBudgetUsage").Count);
        Assert.AreEqual(expectedMonthly == 0 ? 0 : 1, GetCacheField<ConcurrentDictionary<(string UserId, DateOnly Month), long>>(cache, "_monthlyTokenBalance").Count);
        Assert.AreEqual(expectedMonthly == 0 ? 0 : 1, GetCacheField<ConcurrentDictionary<(string UserId, DateOnly Month), decimal>>(cache, "_monthlyBudgetUsage").Count);
    }

    /// <summary>Checks throttle deadlines are isolated by model.</summary>
    [TestMethod]
    [RegressionTestCase("model-throttling", "Future deadlines block only their model", "A throttled model must remain unavailable while a different, unthrottled model remains available.")]
    public void IsModelAvailable_FutureDeadline_BlocksOnlyThrottledModel() {
        using var cache = CreateCache();

        cache.ModelThrottled("model-a", 60);

        Assert.IsFalse(cache.IsModelAvailable("model-a"));
        Assert.IsTrue(cache.IsModelAvailable("model-b"));
    }

    /// <summary>Checks expiry and subsequent reads both report availability.</summary>
    [TestMethod]
    [RegressionTestCase("model-throttling", "Expired deadlines stay available", "Removing an expired retry deadline must not make the next availability read return false.")]
    public void IsModelAvailable_ExpiredDeadline_RemainsAvailableAcrossReads() {
        using var cache = CreateCache();

        cache.ModelThrottled("model-a", 0);

        Assert.IsTrue(cache.IsModelAvailable("model-a"));
        Assert.IsTrue(cache.IsModelAvailable("model-a"));
    }

    /// <summary>Checks that the latest throttle notification replaces the earlier deadline.</summary>
    [DataTestMethod]
    [DataRow(60, 0, true)]
    [DataRow(0, 60, false)]
    [RegressionTestCase("model-throttling", "Latest retry delay replaces {0} seconds with {1}", "The replacement deadline must determine availability ({2}), including immediate recovery or renewed throttling.")]
    public void ModelThrottled_ReplacesPreviousDeadline(int initialSeconds, int replacementSeconds, bool expectedAvailable) {
        using var cache = CreateCache();

        cache.ModelThrottled("model-a", initialSeconds);
        cache.ModelThrottled("model-a", replacementSeconds);

        Assert.AreEqual(expectedAvailable, cache.IsModelAvailable("model-a"));
    }

    /// <summary>Checks invalid keys cannot enter accounting or throttle state.</summary>
    [DataTestMethod]
    [DataRow("")]
    [DataRow(" \t ")]
    [RegressionTestCase("token-rollup", "Blank accounting and model keys are rejected", "Empty or whitespace user and model identifiers must fail consistently across producer, reader, and throttle operations.")]
    public void BlankIdentifiers_AreRejected(string invalidIdentifier) {
        using var cache = CreateCache();

        Assert.ThrowsException<ArgumentException>(() => cache.AddMetric(invalidIdentifier, "model-a", 1, 1));
        Assert.ThrowsException<ArgumentException>(() => cache.AddMetric("alice", invalidIdentifier, 1, 1));
        Assert.ThrowsException<ArgumentException>(() => cache.GetTokenBalance(invalidIdentifier, "model-a"));
        Assert.ThrowsException<ArgumentException>(() => cache.GetTokenBalance("alice", invalidIdentifier));
        Assert.ThrowsException<ArgumentException>(() => cache.GetDailyTokenBalance(invalidIdentifier));
        Assert.ThrowsException<ArgumentException>(() => cache.GetMonthlyTokenBalance(invalidIdentifier));
        Assert.ThrowsException<ArgumentException>(() => cache.GetDailyBudgetUsage(invalidIdentifier));
        Assert.ThrowsException<ArgumentException>(() => cache.GetMonthlyBudgetUsage(invalidIdentifier));
        Assert.ThrowsException<ArgumentException>(() => cache.ModelThrottled(invalidIdentifier, 1));
        Assert.ThrowsException<ArgumentException>(() => cache.IsModelAvailable(invalidIdentifier));
    }

    /// <summary>Checks invalid durations do not register a throttle.</summary>
    [TestMethod]
    [RegressionTestCase("model-throttling", "Negative retry delays are rejected", "Invalid negative Retry-After seconds must throw without marking the model unavailable.")]
    public void ModelThrottled_NegativeRetrySeconds_Throws() {
        using var cache = CreateCache();

        Assert.ThrowsException<ArgumentOutOfRangeException>(() => cache.ModelThrottled("model-a", -1));
        Assert.IsTrue(cache.IsModelAvailable("model-a"));
    }

    /// <summary>Checks constructor dependencies and disposed lifecycle behavior.</summary>
    [TestMethod]
    [RegressionTestCase("token-rollup", "Invalid cache lifecycle inputs are rejected", "Null configuration dependencies must fail immediately, and disposal must be idempotent and prevent restarting the consumer.")]
    public void Lifecycle_RejectsNullDependenciesAndStartAfterDisposal() {
        Assert.ThrowsException<ArgumentNullException>(() => new TokenMetricsCache(null!, new TokenomicsSettings()));
        Assert.ThrowsException<ArgumentNullException>(() => new TokenMetricsCache(Options.Create(new ProxyConfig()), null!));
        using var cache = CreateCache();
        cache.Dispose();
        cache.Dispose();

        Assert.ThrowsException<ObjectDisposedException>(() => cache.StartAsync(CancellationToken.None));
    }

    private static TokenMetricsCache CreateCache(TokenomicsSettings? settings = null) {
        return new TokenMetricsCache(Options.Create(new ProxyConfig()), settings ?? new TokenomicsSettings());
    }

    private static async Task RollupAsync(TokenMetricsCache cache) {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await cache.StartAsync(timeout.Token);
        await cache.StopAsync(timeout.Token);
    }

    private static TField GetCacheField<TField>(TokenMetricsCache cache, string fieldName) {
        var field = typeof(TokenMetricsCache).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, $"Cache field '{fieldName}' was not found.");
        return (TField)field.GetValue(cache)!;
    }
}