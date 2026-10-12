using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SimpleL7Proxy;
using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Backend.Iterators;
using SimpleL7Proxy.Test;
using Tests.Helpers;

namespace Tests.Iterators;

[TestClass]
[DoNotParallelize]
public class TimeToFirstByteIteratorTests : IRegressionTestMetadata
{
    public IReadOnlyDictionary<string, RegressionFeature> RegressionFeatures { get; } =
        new Dictionary<string, RegressionFeature>
        {
            ["time-to-first-byte-iterator"] = new(
                "Traffic Routing",
                "Time-to-first-byte iterator",
                "Confirms TTFB-based host selection prefers the fastest backend and retains bounded iteration behavior."),
            ["priority-group-iterator"] = new(
                "Traffic Routing",
                "Priority-group iterator",
                "Confirms priority groups are exhausted in order, peers are shuffled inside a group, and AcceptHost skips do not consume attempts.")
        };

    [ClassInitialize]
    public static void ClassInit(TestContext _) => TestHostFactory.EnsureInitialized();

    [TestMethod]
    [RegressionTestCase(
        "time-to-first-byte-iterator",
        "Single-pass iteration orders hosts by TTFB",
        "Creates three hosts with distinct TTFB values and confirms the iterator yields them from fastest to slowest.")]
    public void SinglePass_OrdersHostsByTimeToFirstByte()
    {
        // Arrange
        var hosts = TestHostFactory.CreateHosts(3);
        hosts[0].TimeToFirstByteMs = 320;
        hosts[1].TimeToFirstByteMs = 90;
        hosts[2].TimeToFirstByteMs = 180;

        var iterator = new PerRequestHostIterator(hosts, Constants.TimeToFirstByte);

        // Act
        var visited = Drain(iterator);

        // Assert
        var expected = new[] { hosts[1].Host, hosts[2].Host, hosts[0].Host };
        CollectionAssert.AreEqual(expected, visited.Select(h => h.Host).ToArray());
    }

    [TestMethod]
    [RegressionTestCase(
        "time-to-first-byte-iterator",
        "Multi-pass iteration respects max attempts",
        "Confirms the TTFB iterator stops after the configured retry budget even while cycling through hosts.")]
    public void MultiPass_RespectsMaxAttempts()
    {
        // Arrange
        var hosts = TestHostFactory.CreateHosts(3);
        var iterator = new PerRequestHostIterator(hosts, Constants.TimeToFirstByte);
        var state = new IterationState(IterationModeEnum.MultiPass, maxAttempts: 4);

        int attempts = 0;

        // Act
        while (iterator.TryGet(state, out var host) && host != null)
        {
            iterator.RecordResult(state, host, success: false);
            attempts++;
        }

        // Assert
        Assert.AreEqual(4, attempts,
            "MultiPass should stop after the configured number of actual attempts.");
    }

    [TestMethod]
    [RegressionTestCase(
        "time-to-first-byte-iterator",
        "Factory applies time-to-first-byte ordering",
        "Confirms the load-balance mode string makes the factory-created iterator select the fastest host first.")]
    public void Factory_CreateSinglePassIterator_AppliesTimeToFirstByteOrdering()
    {
        // Arrange
        var hosts = TestHostFactory.CreateHosts(2);
        hosts[0].TimeToFirstByteMs = 50;
        hosts[1].TimeToFirstByteMs = 200;
        var backendService = new StubEndpointMonitorService(hosts);

        // Act
        var iterator = IteratorFactory.CreateSinglePassIterator(
            backendService,
            Constants.TimeToFirstByte,
            "/openai/v1/chat",
            out _);

        // Assert
        var state = new IterationState(IterationModeEnum.SinglePass, maxAttempts: 1);
        Assert.IsTrue(iterator.TryGet(state, out var selectedHost));
        Assert.AreSame(hosts[0], selectedHost);
    }

    [TestMethod]
    [RegressionTestCase(
        "priority-group-iterator",
        "Lower priority groups are exhausted before higher groups",
        "Creates mixed-group hosts and confirms both group-1 hosts are visited before the group-2 host.")]
    public void PriorityGroup_ExhaustsLowerGroupBeforeHigher()
    {
        var group1A = TestHostFactory.CreateHost("host=https://g1a.example.com;mode=direct;priorityGroup=1");
        var group2 = TestHostFactory.CreateHost("host=https://g2.example.com;mode=direct;priorityGroup=2");
        var group1B = TestHostFactory.CreateHost("host=https://g1b.example.com;mode=direct;priorityGroup=1");
        var hosts = new List<BaseHostHealth> { group2, group1A, group1B };

        var iterator = new PerRequestHostIterator(hosts, Constants.PriorityGroup);
        var visited = Drain(iterator);

        Assert.AreEqual(3, visited.Count);
        Assert.AreEqual(1, visited[0].Config.PriorityGroup);
        Assert.AreEqual(1, visited[1].Config.PriorityGroup);
        Assert.AreEqual(2, visited[2].Config.PriorityGroup);
        Assert.AreEqual(group2.Host, visited[2].Host);
    }

    [TestMethod]
    [RegressionTestCase(
        "priority-group-iterator",
        "Hosts inside a priority group are shuffled",
        "Repeats group-1 selection until both peer orderings have been observed.")]
    public void PriorityGroup_ShufflesHostsInsideGroup()
    {
        var group1A = TestHostFactory.CreateHost("host=https://g1a.example.com;mode=direct;priorityGroup=1");
        var group1B = TestHostFactory.CreateHost("host=https://g1b.example.com;mode=direct;priorityGroup=1");
        var group2 = TestHostFactory.CreateHost("host=https://g2.example.com;mode=direct;priorityGroup=2");
        var hosts = new List<BaseHostHealth> { group2, group1A, group1B };

        var sawAFirst = false;
        var sawBFirst = false;
        for (var attempt = 0; attempt < 40 && !(sawAFirst && sawBFirst); attempt++)
        {
            var iterator = new PerRequestHostIterator(hosts, Constants.PriorityGroup);
            Assert.IsTrue(iterator.MoveNext());
            if (iterator.Current.Host == group1A.Host)
                sawAFirst = true;
            else if (iterator.Current.Host == group1B.Host)
                sawBFirst = true;
        }

        Assert.IsTrue(sawAFirst && sawBFirst, "Both group-1 hosts should appear first across shuffles.");
    }

    [TestMethod]
    [RegressionTestCase(
        "priority-group-iterator",
        "AcceptHost skips do not consume attempt budget",
        "Rejects one host via AcceptHost and confirms TryGet returns the others without counting the skip.")]
    public void TryGet_AcceptHostSkipDoesNotConsumeAttempts()
    {
        var hosts = TestHostFactory.CreateHosts(3);
        var iterator = new PerRequestHostIterator(hosts, loadBalanceMode: null);
        var state = new IterationState(IterationModeEnum.SinglePass, maxAttempts: 10)
        {
            AcceptHost = host => !ReferenceEquals(host, hosts[1])
        };

        var visited = new List<BaseHostHealth>();
        while (iterator.TryGet(state, out var host) && host != null)
        {
            visited.Add(host);
            iterator.RecordResult(state, host, success: false);
        }

        Assert.AreEqual(2, visited.Count);
        CollectionAssert.DoesNotContain(
            visited.Select(host => host.Host).ToList(),
            hosts[1].Host);
        Assert.AreEqual(2, state.TotalAttempts);
    }

    private static List<BaseHostHealth> Drain(PerRequestHostIterator iterator)
    {
        var result = new List<BaseHostHealth>();
        while (iterator.MoveNext())
        {
            result.Add(iterator.Current);
        }
        return result;
    }

    private sealed class StubEndpointMonitorService : IEndpointMonitorService
    {
        private readonly List<BaseHostHealth> _hosts;

        public StubEndpointMonitorService(List<BaseHostHealth> hosts)
        {
            _hosts = hosts;
        }

        public List<BaseHostHealth> GetHosts() => _hosts;
        public List<BaseHostHealth> GetActiveHosts() => _hosts;
        public int ActiveHostCount() => _hosts.Count;
        public string HostStatus => string.Empty;
        public int EMSGetBackpressureDelay() => 0;
        public Task WaitForStartupAsync() => Task.CompletedTask;
        public Task Stop() => Task.CompletedTask;
        // TestHostFactory hosts have no specific path pattern, so they are catch-all hosts.
        public List<BaseHostHealth> GetSpecificPathHosts() => new();
        public List<BaseHostHealth> GetCatchAllHosts() => _hosts;
    }
}
