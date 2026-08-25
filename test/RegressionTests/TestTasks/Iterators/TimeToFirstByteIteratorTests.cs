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
                "Confirms TTFB-based host selection prefers the fastest backend and retains bounded iteration behavior.")
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

        var iterator = new TimeToFirstByteHostIterator(hosts);

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
        var iterator = new TimeToFirstByteHostIterator(hosts);
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
        "Factory dispatches timetofirstbyte mode to the TTFB iterator",
        "Confirms the load-balance mode string resolves to the correct iterator type.")]
    public void Factory_CreateSinglePassIterator_UsesTimeToFirstByteIterator()
    {
        // Arrange
        var hosts = TestHostFactory.CreateHosts(2);
        var backendService = new StubEndpointMonitorService(hosts);

        // Act
        var iterator = IteratorFactory.CreateSinglePassIterator(
            backendService,
            Constants.TimeToFirstByte,
            "/openai/v1/chat",
            out _);

        // Assert
        Assert.IsInstanceOfType(iterator, typeof(TimeToFirstByteHostIterator));
    }

    private static List<BaseHostHealth> Drain(TimeToFirstByteHostIterator iterator)
    {
        var result = new List<BaseHostHealth>();
        while (iterator.MoveNext())
        {
            result.Add(iterator.Current);
            iterator.RecordResult(iterator.Current, success: false);
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
