using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SimpleL7Proxy.Auth;
using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Backend.Iterators;
using SimpleL7Proxy.Config;

namespace SimpleL7Proxy.Test.CircuitBreakerRegression;

[TestClass]
[DoNotParallelize]
public sealed class CircuitBreakerDeadlineTests : IRegressionTestMetadata
{
    public IReadOnlyDictionary<string, RegressionFeature> RegressionFeatures { get; } =
        new Dictionary<string, RegressionFeature>
        {
            ["circuit-breaker-deadlines"] = new(
                "Reliability & Capacity",
                "Circuit-breaker retry deadlines",
                "Confirms failure windows and Retry-After headers produce bounded backend retry deadlines and parent backpressure.")
        };

    [TestMethod]
    [RegressionTestCase(
        "circuit-breaker-deadlines",
        "Failures below the threshold leave the deadline closed",
        "Tracks a failure without Retry-After headers and confirms it does not create a retry deadline below the configured threshold.")]
    public void TrackStatus_WithoutRetryHeaders_DoesNotSetDeadlineBelowThreshold()
    {
        using var breaker = CreateBreaker(threshold: 2, timeFrameSeconds: 30, trackRetryAfter: true);
        using var response = new HttpResponseMessage();

        breaker.TrackStatus(503, true, "test", response.Headers);

        Assert.AreEqual(default(DateTime), breaker.NextRetryDeadlineUtc);
        Assert.AreEqual(0, breaker.GetMsToNextRetry());
    }

    [TestMethod]
    [RegressionTestCase(
        "circuit-breaker-deadlines",
        "Disabled Retry-After tracking ignores response headers",
        "Confirms Retry-After headers do not affect the deadline when retry tracking is disabled for the backend.")]
    public void TrackStatus_WhenRetryTrackingDisabled_IgnoresRetryHeaders()
    {
        using var breaker = CreateBreaker(threshold: 2, timeFrameSeconds: 30, trackRetryAfter: false);
        using var response = CreateResponse(("Retry-After-Ms", "5000"));

        breaker.TrackStatus(503, true, "test", response.Headers);

        Assert.AreEqual(default(DateTime), breaker.NextRetryDeadlineUtc);
        Assert.AreEqual(0, breaker.GetMsToNextRetry());
    }

    [TestMethod]
    [RegressionTestCase(
        "circuit-breaker-deadlines",
        "The larger Retry-After header controls the deadline",
        "Supplies Retry-After in seconds and milliseconds and confirms the larger delay plus fixed jitter determines the deadline.")]
    public void TrackStatus_WithBothRetryHeaders_UsesLargerValueAndFixedJitter()
    {
        using var breaker = CreateBreaker(threshold: 2, timeFrameSeconds: 30, trackRetryAfter: true);
        using var response = CreateResponse(
            ("retry-after", "1"),
            ("RETRY-AFTER-MS", "2500"));
        var before = DateTime.UtcNow;

        breaker.TrackStatus(503, true, "test", response.Headers);

        var after = DateTime.UtcNow;
        AssertDeadline(
            breaker.NextRetryDeadlineUtc,
            before,
            after,
            2500 + Constants.RetryAfterJitterMaxMs);
    }

    [DataTestMethod]
    [DataRow(5, 1000, 5000)]
    [DataRow(1, 3000, 3000 + Constants.RetryAfterJitterMaxMs)]
    [RegressionTestCase(
        "circuit-breaker-deadlines",
        "The later retry constraint wins for a {0}-second failure window",
        "Compares a {1} ms Retry-After value with the failure window and confirms the effective delay is {2} ms.")]
    public void TrackStatus_UsesLaterOfRetryAfterAndFailureWindow(
        int timeFrameSeconds,
        int retryAfterMs,
        int expectedDelayMs)
    {
        using var breaker = CreateBreaker(threshold: 1, timeFrameSeconds, trackRetryAfter: true);
        using var response = CreateResponse(("Retry-After-Ms", retryAfterMs.ToString()));
        var before = DateTime.UtcNow;

        breaker.TrackStatus(503, true, "test", response.Headers);

        var after = DateTime.UtcNow;
        AssertDeadline(breaker.NextRetryDeadlineUtc, before, after, expectedDelayMs);
        Assert.IsTrue(breaker.GetMsToNextRetry() > 0);
    }

    [TestMethod]
    [RegressionTestCase(
        "circuit-breaker-deadlines",
        "An all-blocked parent returns aggregate backpressure",
        "Opens every child circuit breaker and confirms the parent reports twice the maximum child delay.")]
    public void Parent_WhenEveryChildIsBlocked_ReturnsDoubleMaximumDelay()
    {
        using var firstChild = CreateBreaker(threshold: 1, timeFrameSeconds: 30);
        using var secondChild = CreateBreaker(threshold: 1, timeFrameSeconds: 30);
        using var parent = CreateBreaker(threshold: 1, timeFrameSeconds: 30, isParent: true);

        firstChild.TrackStatus(503, true, "test");
        secondChild.TrackStatus(503, true, "test");

        Assert.IsTrue(
            SpinWait.SpinUntil(CircuitBreaker.AreAllCircuitBreakersBlocked, TimeSpan.FromSeconds(2)),
            "The timer did not mark every child circuit breaker as blocked.");
        Assert.AreEqual(2000, parent.GetMsToNextRetry());
    }

    private static CircuitBreaker CreateBreaker(
        int threshold,
        int timeFrameSeconds,
        bool trackRetryAfter = false,
        bool isParent = false)
    {
        var options = Options.Create(new ProxyConfig
        {
            CircuitBreakerErrorThreshold = threshold,
            CircuitBreakerTimeslice = timeFrameSeconds,
            AcceptableStatusCodes = [200]
        });

        return new CircuitBreaker(options, NullLogger<CircuitBreaker>.Instance, isParent)
        {
            TrackRetryAfter = trackRetryAfter
        };
    }

    private static HttpResponseMessage CreateResponse(params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage();
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    private static void AssertDeadline(
        DateTime actual,
        DateTime before,
        DateTime after,
        int expectedDelayMs)
    {
        var earliest = before.AddMilliseconds(expectedDelayMs);
        var latest = after.AddMilliseconds(expectedDelayMs);
        Assert.IsTrue(actual >= earliest, $"Deadline {actual:o} was earlier than {earliest:o}.");
        Assert.IsTrue(actual <= latest, $"Deadline {actual:o} was later than {latest:o}.");
    }
}

[TestClass]
[DoNotParallelize]
public sealed class NextHostCircuitBreakerTests : IRegressionTestMetadata
{
    public IReadOnlyDictionary<string, RegressionFeature> RegressionFeatures { get; } =
        new Dictionary<string, RegressionFeature>
        {
            ["circuit-breaker-host-availability"] = new(
                "Traffic Routing",
                "Circuit-breaker host availability",
                "Confirms backend selection distinguishes all-open host sets from sets that still contain an eligible backend.")
        };

    [TestMethod]
    [RegressionTestCase(
        "circuit-breaker-host-availability",
        "All open hosts return the shortest retry delay",
        "Evaluates three blocked hosts and confirms selection reports all open with the shortest available retry delay.")]
    public void EvalHostAvailability_WhenAllHostsOpen_ReturnsShortestRetry()
    {
        var firstBreaker = new StubCircuitBreaker(5000);
        var secondBreaker = new StubCircuitBreaker(2000);
        var thirdBreaker = new StubCircuitBreaker(7000);
        using var hosts = new HostFixture(firstBreaker, secondBreaker, thirdBreaker);
        var (iterator, state) = hosts.CreateIterator();

        var result = iterator.EvalHostAvailability(state, hosts[0], firstBreaker.GetMsToNextRetry());

        Assert.IsTrue(result.AllOpen);
        Assert.AreEqual(2000, result.RetryAfterMs);
        Assert.AreEqual(3, result.CheckedHostCount);
    }

    [TestMethod]
    [RegressionTestCase(
        "circuit-breaker-host-availability",
        "An eligible peer keeps the host set available",
        "Evaluates one blocked and one available host and confirms the host set remains selectable.")]
    public void EvalHostAvailability_WhenAnotherHostIsAvailable_ReturnsNotAllOpen()
    {
        var blockedBreaker = new StubCircuitBreaker(5000);
        var availableBreaker = new StubCircuitBreaker(0);
        using var hosts = new HostFixture(blockedBreaker, availableBreaker);
        var (iterator, state) = hosts.CreateIterator();

        var result = iterator.EvalHostAvailability(state, hosts[0], blockedBreaker.GetMsToNextRetry());

        Assert.IsFalse(result.AllOpen);
        Assert.AreEqual(2, result.CheckedHostCount);
    }

    [TestMethod]
    [RegressionTestCase(
        "circuit-breaker-host-availability",
        "Host availability is rescanned when cached state changes",
        "Opens a previously available peer and confirms the next evaluation rescans the set and reports the shortest retry delay.")]
    public void EvalHostAvailability_WhenCachedAvailableHostOpens_RescansAllHosts()
    {
        var firstBreaker = new StubCircuitBreaker(5000);
        var secondBreaker = new StubCircuitBreaker(0);
        using var hosts = new HostFixture(firstBreaker, secondBreaker);
        var (iterator, state) = hosts.CreateIterator();

        Assert.IsFalse(iterator.EvalHostAvailability(state, hosts[0], 5000).AllOpen);
        secondBreaker.RetryAfterMs = 1500;
        var result = iterator.EvalHostAvailability(state, hosts[0], 5000);

        Assert.IsTrue(result.AllOpen);
        Assert.AreEqual(1500, result.RetryAfterMs);
    }

    [TestMethod]
    [RegressionTestCase(
        "circuit-breaker-host-availability",
        "Per-request iteration visits every configured host",
        "Traverses a two-host iterator and confirms each backend is returned exactly once for the request.")]
    public void TryGet_TraversesPerRequestIterator()
    {
        using var hosts = new HostFixture(new StubCircuitBreaker(0), new StubCircuitBreaker(0));
        var (iterator, state) = hosts.CreateIterator();
        var visited = new HashSet<BaseHostHealth>();

        while (iterator.TryGet(state, out var host))
        {
            Assert.IsNotNull(host);
            visited.Add(host);
        }

        Assert.AreEqual(2, visited.Count);
    }
}

[TestClass]
[DoNotParallelize]
public sealed class CircuitBreakerPerformanceTests : IRegressionTestMetadata
{
    public IReadOnlyDictionary<string, RegressionFeature> RegressionFeatures { get; } =
        new Dictionary<string, RegressionFeature>
        {
            ["circuit-breaker-evaluation-throughput"] = new(
                "Reliability & Capacity",
                "Circuit-breaker evaluation throughput",
                "Measures sustained all-open host evaluation while confirming each result remains correct under repeated calls.")
        };

    [TestMethod]
    [RegressionTestCase(
        "circuit-breaker-evaluation-throughput",
        "Three-host availability evaluation sustains repeated calls",
        "Runs all-open host evaluation for ten seconds, reports throughput, and confirms every final availability field remains valid.")]
    [TestCategory("Performance")]
    public void EvalHostAvailability_ThreeOpenHosts_TenSecondsReportsThroughput()
    {
        const int hostCount = 3;
        const int durationSeconds = 10;
        const int batchSize = 1024;
        using var hosts = new RealCircuitBreakerHostFixture(hostCount);
        var (iterator, state) = hosts.CreateIterator();
        var currentHost = hosts[0];
        var duration = TimeSpan.FromSeconds(durationSeconds);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long evaluations = 0;
        (bool AllOpen, int RetryAfterMs, int CheckedHostCount) result = default;

        do
        {
            for (int batchIndex = 0; batchIndex < batchSize; batchIndex++)
            {
                var currentRetryAfterMs = currentHost.Config.GetMsToNextRetry();
                result = iterator.EvalHostAvailability(state, currentHost, currentRetryAfterMs);
                evaluations++;
            }
        }
        while (stopwatch.Elapsed < duration);

        stopwatch.Stop();
        var circuitBreakerCalls = evaluations * hostCount;
        var callsPerSecond = circuitBreakerCalls / stopwatch.Elapsed.TotalSeconds;

        Console.WriteLine(
            $"Circuit breaker throughput over {stopwatch.Elapsed.TotalSeconds:F2}s: " +
            $"{evaluations:N0} host-set evaluations, {circuitBreakerCalls:N0} breaker calls, " +
            $"{callsPerSecond:N0} breaker calls/sec.");

        Assert.IsTrue(result.AllOpen);
        Assert.AreEqual(hostCount, result.CheckedHostCount);
        Assert.IsTrue(result.RetryAfterMs > 0);
        Assert.IsTrue(evaluations > 0);
    }
}

internal sealed class HostFixture : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly List<BaseHostHealth> _hosts = [];

    internal HostFixture(params StubCircuitBreaker[] breakers)
    {
        var pendingBreakers = new Queue<ICircuitBreaker>(breakers);
        var services = new ServiceCollection();
        services.AddSingleton<IBackendTokenProvider, AzureProvider>();
        services.AddTransient<ICircuitBreaker>(_ => pendingBreakers.Dequeue());
        _services = services.BuildServiceProvider();

        HostConfig.Initialize(NullLogger.Instance, _services);
        for (int hostIndex = 0; hostIndex < breakers.Length; hostIndex++)
        {
            var config = new HostConfig($"host=https://host-{hostIndex}.example.com;mode=direct");
            config.Activate();
            _hosts.Add(new NonProbeableHostHealth(config, NullLogger.Instance));
        }
    }

    internal BaseHostHealth this[int index] => _hosts[index];

    internal (BaseIterator Iterator, IterationState State) CreateIterator()
    {
        var iterator = new RoundRobinHostIterator(_hosts);
        var state = new IterationState(IterationModeEnum.SinglePass, maxAttempts: 1);
        return (iterator, state);
    }

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.Config.SpinDown();
        }

        _services.Dispose();
    }
}

internal sealed class RealCircuitBreakerHostFixture : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly List<BaseHostHealth> _hosts = [];

    internal RealCircuitBreakerHostFixture(int hostCount)
    {
        var options = Options.Create(new ProxyConfig
        {
            CircuitBreakerErrorThreshold = 1,
            CircuitBreakerTimeslice = 60,
            AcceptableStatusCodes = [200]
        });
        var services = new ServiceCollection();
        services.AddSingleton<IBackendTokenProvider, AzureProvider>();
        services.AddSingleton<IOptions<ProxyConfig>>(options);
        services.AddTransient<ICircuitBreaker>(provider => new CircuitBreaker(
            provider.GetRequiredService<IOptions<ProxyConfig>>(),
            NullLogger<CircuitBreaker>.Instance));
        _services = services.BuildServiceProvider();

        HostConfig.Initialize(NullLogger.Instance, _services);
        for (int hostIndex = 0; hostIndex < hostCount; hostIndex++)
        {
            var config = new HostConfig($"host=https://perf-host-{hostIndex}.example.com;mode=direct");
            config.Activate();
            _hosts.Add(new NonProbeableHostHealth(config, NullLogger.Instance));
        }

        using var response = new HttpResponseMessage();
        response.Headers.TryAddWithoutValidation("Retry-After-Ms", "60000");
        foreach (var host in _hosts)
        {
            host.Config.TrackStatus(503, true, "perf", response.Headers);
        }
    }

    internal BaseHostHealth this[int index] => _hosts[index];

    internal (BaseIterator Iterator, IterationState State) CreateIterator()
    {
        var iterator = new RoundRobinHostIterator(_hosts);
        var state = new IterationState(IterationModeEnum.SinglePass, maxAttempts: 1);
        return (iterator, state);
    }

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.Config.SpinDown();
        }

        _services.Dispose();
    }
}

internal sealed class StubCircuitBreaker(int retryAfterMs) : ICircuitBreaker
{
    public string ID { get; set; } = string.Empty;
    public bool TrackRetryAfter { get; set; }
    internal int RetryAfterMs { get; set; } = retryAfterMs;

    public void TrackStatus(int code, bool wasFailure, string state, HttpResponseHeaders? responseHeaders = null)
    {
    }

    public int GetBackpressureDelay() => 0;
    public int GetMsToNextRetry() => RetryAfterMs;
    public void Deregister() { }
    public string GetCircuitBreakerStatusString() => string.Empty;
}

internal sealed class AzureProvider : IBackendTokenProvider
{
    public void AddAudience(string audience) { }
    public Task<string> OAuth2Token(string? audience = null) => Task.FromResult(string.Empty);
    public void StartTokenRefresh() { }
}