using SimpleL7Proxy;
using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Backend.Iterators;
using SimpleL7Proxy.Test;
using Tests.Helpers;

namespace Tests.Iterators;

[TestClass]
[DoNotParallelize]
public class RoundRobinIteratorTests : IRegressionTestMetadata
{
    public IReadOnlyDictionary<string, RegressionFeature> RegressionFeatures { get; } =
        new Dictionary<string, RegressionFeature>
        {
            ["round-robin-iterator"] = new(
                "Traffic Routing",
                "Round-robin iterator",
                "Confirms round-robin host selection remains complete, fair, bounded, resettable, and thread-safe.")
        };

    [ClassInitialize]
    public static void ClassInit(TestContext _) => TestHostFactory.EnsureInitialized();

    // ──────────────────────────────────────────────────────────────
    //  Basic Distribution
    // ──────────────────────────────────────────────────────────────

    [TestMethod]
    [RegressionTestCase(
        "round-robin-iterator",
        "Single-pass iteration visits every host once",
        "Drains a three-host iterator and confirms every configured host is returned exactly once.")]
    public void SinglePass_VisitsEveryHostExactlyOnce()
    {
        // Arrange
        var hosts = TestHostFactory.CreateHosts(3);
        var iterator = new RoundRobinHostIterator(hosts);

        // Act
        var visited = Drain(iterator);

        // Assert — all 3 hosts visited, no duplicates
        Assert.AreEqual(3, visited.Count, "Should visit every host exactly once in SinglePass.");
        CollectionAssert.AreEquivalent(
            hosts.Select(h => h.Host).ToList(),
            visited.Select(h => h.Host).ToList(),
            "Every host should be visited.");
    }

    [TestMethod]
    [RegressionTestCase(
        "round-robin-iterator",
        "Independent iterators distribute requests evenly",
        "Creates 30 one-request iterators across three hosts and confirms each host receives ten selections.")]
    public void EvenDistribution_AcrossMultipleIterators()
    {
        // Arrange — 3 hosts, 30 sequential iterators each doing SinglePass
        var hosts = TestHostFactory.CreateHosts(3);
        var hitCounts = new Dictionary<string, int>();
        foreach (var h in hosts) hitCounts[h.Host] = 0;

        // Act — each iterator gets one host via the global counter
        for (int i = 0; i < 30; i++)
        {
            var iterator = new RoundRobinHostIterator(hosts);
            if (iterator.MoveNext())
            {
                hitCounts[iterator.Current.Host]++;
            }
        }

        // Assert — each host should be hit 10 times (30 / 3)
        foreach (var kvp in hitCounts)
        {
            Assert.AreEqual(10, kvp.Value,
                $"Host {kvp.Key} should receive exactly 10 of 30 requests. Got {kvp.Value}.");
        }
    }

    [TestMethod]
    [RegressionTestCase(
        "round-robin-iterator",
        "Independent iterators share one rotation",
        "Creates eight iterators across four hosts and confirms their first selections follow one continuous round-robin sequence.")]
    public void GlobalCounter_DistributesAcrossIndependentIterators()
    {
        // Arrange
        var hosts = TestHostFactory.CreateHosts(4);
        var selectedHosts = new List<string>();

        // Act — create 8 separate iterators, take first host from each
        for (int i = 0; i < 8; i++)
        {
            var it = new RoundRobinHostIterator(hosts);
            Assert.IsTrue(it.MoveNext());
            selectedHosts.Add(it.Current.Host);
        }

        // Assert — should cycle through all 4 hosts twice from any global starting offset
        var firstHostIndex = hosts.FindIndex(host => host.Host == selectedHosts[0]);
        Assert.IsTrue(firstHostIndex >= 0);
        for (int i = 0; i < selectedHosts.Count; i++)
        {
            var expectedHostIndex = (firstHostIndex + i) % hosts.Count;
            Assert.AreEqual(hosts[expectedHostIndex].Host, selectedHosts[i],
                $"Request {i} should hit host index {expectedHostIndex} but hit {selectedHosts[i]}.");
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  Edge Cases
    // ──────────────────────────────────────────────────────────────

    [TestMethod]
    [RegressionTestCase(
        "round-robin-iterator",
        "An empty host list yields no selection",
        "Confirms MoveNext returns false when no backend hosts are configured.")]
    public void EmptyHostList_MoveNextReturnsFalse()
    {
        var iterator = new RoundRobinHostIterator(new List<BaseHostHealth>());

        Assert.IsFalse(iterator.MoveNext(), "MoveNext on empty list should return false.");
    }

    [TestMethod]
    [RegressionTestCase(
        "round-robin-iterator",
        "A single-host pass stops after one selection",
        "Confirms a single configured backend is returned once and the iterator then completes.")]
    public void SingleHost_AlwaysReturnsSameHost()
    {
        var hosts = TestHostFactory.CreateHosts(1);
        var iterator = new RoundRobinHostIterator(hosts);

        Assert.IsTrue(iterator.MoveNext());
        Assert.AreEqual(hosts[0].Host, iterator.Current.Host);
        // SinglePass with 1 host: second MoveNext should return false
        Assert.IsFalse(iterator.MoveNext(), "Should stop after visiting the only host.");
    }

    [TestMethod]
    [RegressionTestCase(
        "round-robin-iterator",
        "Single-pass iteration is bounded by host count",
        "Confirms a three-host single pass yields exactly three selections.")]
    public void SinglePass_DoesNotExceedHostCount()
    {
        var hosts = TestHostFactory.CreateHosts(3);
        var iterator = new RoundRobinHostIterator(hosts);

        int count = 0;
        while (iterator.MoveNext()) count++;

        Assert.AreEqual(3, count, "SinglePass should yield exactly hostCount elements.");
    }

    // ──────────────────────────────────────────────────────────────
    //  MultiPass Mode (via BaseIterator — pass/attempt control lives there now)
    // ──────────────────────────────────────────────────────────────

    [TestMethod]
    [RegressionTestCase(
        "round-robin-iterator",
        "Multi-pass iteration counts attempted hosts",
        "Confirms a circuit-breaker-skipped host does not consume the configured backend attempt budget.")]
    public void MultiPass_RespectsMaxAttempts()
    {
        var hosts = TestHostFactory.CreateHosts(3);
        int maxAttempts = 7;
        var iterator = new RoundRobinHostIterator(hosts);
        var state = new IterationState(IterationModeEnum.MultiPass, maxAttempts);

        int selectedHostCount = 0;
        int attemptCount = 0;
        while (iterator.TryGet(state, out var host) && host != null)
        {
            selectedHostCount++;
            if (selectedHostCount == 1)
                continue; // Simulate a host skipped by the circuit breaker

            iterator.RecordResult(state, host, success: false);
            attemptCount++;
        }

        Assert.AreEqual(maxAttempts, attemptCount,
            "MultiPass should stop after the configured number of actual attempts.");
        Assert.AreEqual(maxAttempts + 1, selectedHostCount,
            "A skipped host should not consume the attempt budget.");
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [RegressionTestCase(
        "round-robin-iterator",
        "MaxAttempts {0} leaves multi-pass iteration unbounded",
        "Confirms a nonpositive maximum does not stop iteration after repeated host passes. Inputs: MaxAttempts={0}.")]
    public void MultiPass_MaxAttemptsLessThanOne_DisablesLimit(int disabledMaxAttempts)
    {
        var hosts = TestHostFactory.CreateHosts(3);
        var iterator = new RoundRobinHostIterator(hosts);
        var state = new IterationState(IterationModeEnum.MultiPass, disabledMaxAttempts);

        Assert.AreEqual(disabledMaxAttempts, state.MaxAttempts);

        int attemptsBeyondOnePass = hosts.Count * 3;
        for (int attempt = 0; attempt < attemptsBeyondOnePass; attempt++)
        {
            Assert.IsTrue(iterator.TryGet(state, out var host) && host != null,
                $"MaxAttempts={disabledMaxAttempts} should remain enabled beyond one host pass.");
            iterator.RecordResult(state, host!, success: false);
        }

        Assert.IsTrue(iterator.TryGet(state, out var extraHost) && extraHost != null,
            $"MaxAttempts={disabledMaxAttempts} should not stop after {attemptsBeyondOnePass} attempts.");
    }

    [TestMethod]
    [RegressionTestCase(
        "round-robin-iterator",
        "Multi-pass iteration revisits hosts",
        "Confirms a two-host iterator with six attempts continues beyond its first host pass.")]
    public void MultiPass_CyclesThroughHostsMultipleTimes()
    {
        var hosts = TestHostFactory.CreateHosts(2);
        int maxAttempts = 6;
        var iterator = new RoundRobinHostIterator(hosts);
        var state = new IterationState(IterationModeEnum.MultiPass, maxAttempts);

        var visited = new List<BaseHostHealth>();
        while (iterator.TryGet(state, out var host) && host != null)
        {
            visited.Add(host);
            iterator.RecordResult(state, host, success: false);
        }

        // With 2 hosts and 6 attempts, both hosts should appear multiple times
        Assert.IsTrue(visited.Count > 2,
            "MultiPass with maxAttempts=6 and 2 hosts should visit more than 2 hosts total.");
    }

    // ──────────────────────────────────────────────────────────────
    //  HostCount Property
    // ──────────────────────────────────────────────────────────────

    [TestMethod]
    [RegressionTestCase(
        "round-robin-iterator",
        "HostCount reflects configured backends",
        "Confirms HostCount reports five when five backends are configured.")]
    public void HostCount_ReflectsActualHostListSize()
    {
        var hosts = TestHostFactory.CreateHosts(5);
        var iterator = new RoundRobinHostIterator(hosts);

        Assert.AreEqual(5, iterator.HostCount);
    }

    [TestMethod]
    [RegressionTestCase(
        "round-robin-iterator",
        "HostCount is zero without backends",
        "Confirms HostCount reports zero for an empty backend list.")]
    public void HostCount_ZeroForEmptyList()
    {
        var iterator = new RoundRobinHostIterator(new List<BaseHostHealth>());

        Assert.AreEqual(0, iterator.HostCount);
    }

    // ──────────────────────────────────────────────────────────────
    //  Concurrency
    // ──────────────────────────────────────────────────────────────

    [TestMethod]
    [RegressionTestCase(
        "round-robin-iterator",
        "Concurrent iterators preserve even distribution",
        "Runs 100 parallel one-request iterators across four hosts and confirms every request is assigned with equal distribution.")]
    public void ConcurrentIterators_NoHostMissedOrDuplicated()
    {
        // Arrange — 4 hosts, 100 parallel iterators each taking 1 host
        var hosts = TestHostFactory.CreateHosts(4);
        var bag = new System.Collections.Concurrent.ConcurrentBag<string>();
        int totalRequests = 100;

        // Act
        Parallel.For(0, totalRequests, _ =>
        {
            var it = new RoundRobinHostIterator(hosts);
            if (it.MoveNext())
            {
                bag.Add(it.Current.Host);
            }
        });

        // Assert — every host should be selected, distribution should be roughly even
        Assert.AreEqual(totalRequests, bag.Count, "Every request should select a host.");
        var grouped = bag.GroupBy(h => h).ToDictionary(g => g.Key, g => g.Count());
        Assert.AreEqual(4, grouped.Count, "All 4 hosts should appear.");
        foreach (var kvp in grouped)
        {
            Assert.AreEqual(25, kvp.Value,
                $"Host {kvp.Key} expected 25 hits out of 100, got {kvp.Value}.");
        }
    }

    [TestMethod]
    [RegressionTestCase(
        "round-robin-iterator",
        "Concurrent full passes visit every host",
        "Runs 50 parallel single-pass iterators and confirms each one drains all three configured hosts.")]
    public void ConcurrentDrain_AllHostsVisitedInEachIterator()
    {
        // Arrange — multiple concurrent iterators, each fully drained
        var hosts = TestHostFactory.CreateHosts(3);
        int parallelism = 50;
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();

        // Act
        Parallel.For(0, parallelism, i =>
        {
            var it = new RoundRobinHostIterator(hosts);
            var visited = Drain(it);
            if (visited.Count != 3)
            {
                errors.Add($"Iterator {i}: expected 3 hosts, got {visited.Count}");
            }
        });

        // Assert
        Assert.AreEqual(0, errors.Count,
            $"Concurrent drain failures:\n{string.Join("\n", errors)}");
    }

    // ──────────────────────────────────────────────────────────────
    //  Reset
    // ──────────────────────────────────────────────────────────────

    [TestMethod]
    [RegressionTestCase(
        "round-robin-iterator",
        "Reset starts a fresh complete pass",
        "Drains and resets an iterator, then confirms the next pass visits all configured hosts.")]
    public void Reset_AllowsReIteration()
    {
        var hosts = TestHostFactory.CreateHosts(3);
        var iterator = new RoundRobinHostIterator(hosts);

        // Drain fully
        while (iterator.MoveNext()) { }

        // Reset and drain again
        iterator.Reset();
        var visited = Drain(iterator);

        Assert.AreEqual(3, visited.Count, "After Reset, should visit all hosts again.");
    }

    // ──────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────

    private static List<BaseHostHealth> Drain(RoundRobinHostIterator iterator)
    {
        var result = new List<BaseHostHealth>();
        while (iterator.MoveNext())
        {
            result.Add(iterator.Current);
            iterator.RecordResult(iterator.Current, success: false);
        }
        return result;
    }
}
