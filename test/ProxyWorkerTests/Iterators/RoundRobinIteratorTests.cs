using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Backend.Iterators;
using Tests.Helpers;

namespace Tests.Iterators;

[TestClass]
public class RoundRobinIteratorTests
{
    [ClassInitialize]
    public static void ClassInit(TestContext _) => TestHostFactory.EnsureInitialized();

    // ──────────────────────────────────────────────────────────────
    //  Basic Distribution
    // ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void SinglePass_VisitsEveryHostExactlyOnce()
    {
        // Arrange
        var hosts = TestHostFactory.CreateHosts(3);
        var iterator = new RoundRobinHostIterator(hosts, IterationModeEnum.SinglePass, maxAttempts: 1);

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
    public void EvenDistribution_AcrossMultipleIterators()
    {
        // Arrange — 3 hosts, 30 sequential iterators each doing SinglePass
        var hosts = TestHostFactory.CreateHosts(3);
        var hitCounts = new Dictionary<string, int>();
        foreach (var h in hosts) hitCounts[h.Host] = 0;

        // Act — each iterator gets one host via the global counter
        for (int i = 0; i < 30; i++)
        {
            var iterator = new RoundRobinHostIterator(hosts, IterationModeEnum.SinglePass, maxAttempts: 1);
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
    public void GlobalCounter_DistributesAcrossIndependentIterators()
    {
        // Arrange
        var hosts = TestHostFactory.CreateHosts(4);
        var selectedHosts = new List<string>();

        // Act — create 8 separate iterators, take first host from each
        for (int i = 0; i < 8; i++)
        {
            var it = new RoundRobinHostIterator(hosts, IterationModeEnum.SinglePass, maxAttempts: 1);
            Assert.IsTrue(it.MoveNext());
            selectedHosts.Add(it.Current.Host);
        }

        // Assert — should cycle through all 4 hosts twice: 0,1,2,3,0,1,2,3
        for (int i = 0; i < selectedHosts.Count; i++)
        {
            Assert.AreEqual(hosts[((i + 1) % 4)].Host, selectedHosts[i],
                $"Request {i} should hit host index {(i + 1) % 4} but hit {selectedHosts[i]}.");
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  Edge Cases
    // ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void EmptyHostList_MoveNextReturnsFalse()
    {
        var iterator = new RoundRobinHostIterator(
            new List<BaseHostHealth>(), IterationModeEnum.SinglePass, maxAttempts: 1);

        Assert.IsFalse(iterator.MoveNext(), "MoveNext on empty list should return false.");
    }

    [TestMethod]
    public void SingleHost_AlwaysReturnsSameHost()
    {
        var hosts = TestHostFactory.CreateHosts(1);
        var iterator = new RoundRobinHostIterator(hosts, IterationModeEnum.SinglePass, maxAttempts: 1);

        Assert.IsTrue(iterator.MoveNext());
        Assert.AreEqual(hosts[0].Host, iterator.Current.Host);
        // SinglePass with 1 host: second MoveNext should return false
        Assert.IsFalse(iterator.MoveNext(), "Should stop after visiting the only host.");
    }

    [TestMethod]
    public void SinglePass_DoesNotExceedHostCount()
    {
        var hosts = TestHostFactory.CreateHosts(3);
        var iterator = new RoundRobinHostIterator(hosts, IterationModeEnum.SinglePass, maxAttempts: 1);

        int count = 0;
        while (iterator.MoveNext()) count++;

        Assert.AreEqual(3, count, "SinglePass should yield exactly hostCount elements.");
    }

    // ──────────────────────────────────────────────────────────────
    //  MultiPass Mode
    // ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void MultiPass_RespectsMaxAttempts()
    {
        var hosts = TestHostFactory.CreateHosts(3);
        int maxAttempts = 7;
        var iterator = new RoundRobinHostIterator(hosts, IterationModeEnum.MultiPass, maxAttempts);

        int count = 0;
        while (iterator.MoveNext()) count++;

        Assert.IsTrue(count <= maxAttempts,
            $"MultiPass should not exceed maxAttempts ({maxAttempts}). Got {count}.");
        Assert.IsTrue(count >= hosts.Count,
            $"MultiPass should visit at least all hosts once ({hosts.Count}). Got {count}.");
    }

    [TestMethod]
    public void MultiPass_CyclesThroughHostsMultipleTimes()
    {
        var hosts = TestHostFactory.CreateHosts(2);
        int maxAttempts = 6;
        var iterator = new RoundRobinHostIterator(hosts, IterationModeEnum.MultiPass, maxAttempts);

        var visited = Drain(iterator);

        // With 2 hosts and 6 attempts, both hosts should appear multiple times
        Assert.IsTrue(visited.Count > 2,
            "MultiPass with maxAttempts=6 and 2 hosts should visit more than 2 hosts total.");
    }

    // ──────────────────────────────────────────────────────────────
    //  HostCount Property
    // ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void HostCount_ReflectsActualHostListSize()
    {
        var hosts = TestHostFactory.CreateHosts(5);
        var iterator = new RoundRobinHostIterator(hosts, IterationModeEnum.SinglePass, maxAttempts: 1);

        Assert.AreEqual(5, iterator.HostCount);
    }

    [TestMethod]
    public void HostCount_ZeroForEmptyList()
    {
        var iterator = new RoundRobinHostIterator(
            new List<BaseHostHealth>(), IterationModeEnum.SinglePass, maxAttempts: 1);

        Assert.AreEqual(0, iterator.HostCount);
    }

    // ──────────────────────────────────────────────────────────────
    //  Concurrency
    // ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void ConcurrentIterators_NoHostMissedOrDuplicated()
    {
        // Arrange — 4 hosts, 100 parallel iterators each taking 1 host
        var hosts = TestHostFactory.CreateHosts(4);
        var bag = new System.Collections.Concurrent.ConcurrentBag<string>();
        int totalRequests = 100;

        // Act
        Parallel.For(0, totalRequests, _ =>
        {
            var it = new RoundRobinHostIterator(hosts, IterationModeEnum.SinglePass, maxAttempts: 1);
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
    public void ConcurrentDrain_AllHostsVisitedInEachIterator()
    {
        // Arrange — multiple concurrent iterators, each fully drained
        var hosts = TestHostFactory.CreateHosts(3);
        int parallelism = 50;
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();

        // Act
        Parallel.For(0, parallelism, i =>
        {
            var it = new RoundRobinHostIterator(hosts, IterationModeEnum.SinglePass, maxAttempts: 1);
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
    public void Reset_AllowsReIteration()
    {
        var hosts = TestHostFactory.CreateHosts(3);
        var iterator = new RoundRobinHostIterator(hosts, IterationModeEnum.SinglePass, maxAttempts: 1);

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
        }
        return result;
    }
}
