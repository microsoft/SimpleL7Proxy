using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleL7Proxy;
using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Backend.Iterators;
using SimpleL7Proxy.Config;
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
            ["backend-route-configuration"] = new(
                "Traffic Routing",
                "Backend route configuration validation",
                "Confirms malformed host and Path configurations are rejected without replacing the last known-good snapshot.")
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

        var iterator = new TimeToFirstByteHostIterator(hosts, IterationModeEnum.SinglePass, maxAttempts: 1);

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
        var iterator = new TimeToFirstByteHostIterator(hosts, IterationModeEnum.MultiPass, maxAttempts: 4);

        int attempts = 0;

        // Act
        while (iterator.MoveNext())
        {
            iterator.RecordResult(iterator.Current, success: false);
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

    [TestMethod]
    [RegressionTestCase(
        "time-to-first-byte-iterator",
        "Host routing fields preserve legacy defaults",
        "Parses accepted priorities, priority group, and via while confirming omitted fields accept all priorities in group one.")]
    public void HostConfig_ParsesRoutingFieldsAndPreservesDefaults()
    {
        var routed = new HostConfig(
            "host=https://ptu.example.com;mode=indirect;acceptablePriorities=3:1:3;priorityGroup=2;via=Host_apim",
            configKey: "Host_ptu");

        CollectionAssert.AreEqual(new[] { 1, 3 }, routed.AcceptablePriorities.ToArray());
        Assert.AreEqual(HostModeEnum.Indirect, routed.Mode);
        Assert.IsTrue(routed.IndirectMode);
        Assert.AreEqual(2, routed.PriorityGroup);
        Assert.AreEqual("Host_apim", routed.Via);
        Assert.IsTrue(routed.AcceptsPriority(1));
        Assert.IsFalse(routed.AcceptsPriority(2));

        var legacy = new HostConfig("host=https://legacy.example.com;mode=direct", configKey: "Host1");
        Assert.AreEqual(0, legacy.AcceptablePriorities.Count);
        Assert.AreEqual(1, legacy.PriorityGroup);
        Assert.AreEqual(string.Empty, legacy.Via);
        Assert.IsTrue(legacy.AcceptsPriority(999));
        Assert.IsTrue(ConfigParser.IsBackendHostConfigName("Path_openai"));

        var omittedMode = new HostConfig(
            "host=https://legacy-apim.example.com;probe=/status",
            configKey: "Host_apim");
        Assert.AreEqual(HostModeEnum.Apim, omittedMode.Mode);
        Assert.IsFalse(omittedMode.DirectMode);
        Assert.IsFalse(omittedMode.IndirectMode);

        Assert.ThrowsException<UriFormatException>(() => new HostConfig(
            "host=https://invalid.example.com;mode=direct;acceptablePriorities=0"));
        Assert.ThrowsException<UriFormatException>(() => new HostConfig(
            "host=https://invalid.example.com;mode=direct;priorityGroup=0"));
        Assert.ThrowsException<UriFormatException>(() => new HostConfig(
            "host=https://invalid.example.com;mode=unknown"));
        Assert.ThrowsException<UriFormatException>(() => new HostConfig(
            "host=https://invalid.example.com;mode=indirect"));
        Assert.ThrowsException<UriFormatException>(() => new HostConfig(
            "host=https://invalid.example.com;mode=direct;via=Host_apim"));

        var differentGroup = new HostConfig(
            "host=https://legacy.example.com;mode=direct;priorityGroup=2",
            configKey: "Host1");
        legacy.FreezeHash();
        differentGroup.FreezeHash();
        Assert.AreNotEqual(legacy.FrozenHash, differentGroup.FrozenHash);
    }

    [DataTestMethod]
    [RegressionTestCase(
        "backend-route-configuration",
        "Malformed host settings are rejected",
        "Parses independently named invalid host strings and requires each one to fail before snapshot activation.")]
    [DataRow("unknown-mode", "host=https://invalid.example.com;mode=unknown")]
    [DataRow("indirect-without-via", "host=https://invalid.example.com;mode=indirect")]
    [DataRow("direct-with-via", "host=https://invalid.example.com;mode=direct;via=Host_apim")]
    [DataRow("apim-with-via", "host=https://invalid.example.com;mode=apim;via=Host_apim")]
    [DataRow("zero-priority", "host=https://invalid.example.com;mode=direct;acceptablePriorities=0")]
    [DataRow("non-numeric-priority", "host=https://invalid.example.com;mode=direct;acceptablePriorities=1:high")]
    [DataRow("zero-priority-group", "host=https://invalid.example.com;mode=direct;priorityGroup=0")]
    [DataRow("non-numeric-priority-group", "host=https://invalid.example.com;mode=direct;priorityGroup=primary")]
    [DataRow("unknown-field", "host=https://invalid.example.com;mode=direct;unsupported=true")]
    [DataRow("missing-host-field", "mode=direct;path=/api")]
    public void HostConfig_InvalidSettingThrows(string scenario, string hostValue)
    {
        Exception? exception = null;
        try
        {
            _ = new HostConfig(hostValue, configKey: "Host_invalid");
        }
        catch (Exception caught)
        {
            exception = caught;
        }

        Assert.IsNotNull(exception, $"Scenario '{scenario}' was accepted.");
        Assert.IsTrue(
            exception is UriFormatException or ArgumentException,
            $"Scenario '{scenario}' produced unexpected exception type {exception.GetType().Name}.");
    }

    [TestMethod]
    [RegressionTestCase(
        "time-to-first-byte-iterator",
        "Named route filters priority before group-ordered TTFB selection",
        "Confirms the longest named prefix rewrites the path and a faster fallback group cannot precede an eligible primary group.")]
    public void NamedRoute_FiltersPriorityAndOrdersGroupBeforeTimeToFirstByte()
    {
        var primary = new HostConfig(
            "host=https://primary.example.com;mode=direct;acceptablePriorities=1;priorityGroup=1",
            configKey: "Host_primary");
        var fallback = new HostConfig(
            "host=https://fallback.example.com;mode=direct;acceptablePriorities=1;priorityGroup=2",
            configKey: "Host_fallback");
        var medium = new HostConfig(
            "host=https://medium.example.com;mode=direct;acceptablePriorities=2;priorityGroup=1",
            configKey: "Host_medium");
        var snapshot = HostCollectionSnapshot.Build(
            [primary, fallback, medium],
            [new PathRouteDefinition("Path_api", "/api", ["Host_primary", "Host_fallback", "Host_medium"], true)],
            NullLogger.Instance);

        snapshot.Hosts.Single(host => host.Config.ConfigKey == "Host_primary").TimeToFirstByteMs = 300;
        snapshot.Hosts.Single(host => host.Config.ConfigKey == "Host_fallback").TimeToFirstByteMs = 10;
        snapshot.Hosts.Single(host => host.Config.ConfigKey == "Host_medium").TimeToFirstByteMs = 5;
        snapshot.Hosts.Single(host => host.Config.ConfigKey == "Host_primary").AverageLatencyMs = 300;
        snapshot.Hosts.Single(host => host.Config.ConfigKey == "Host_fallback").AverageLatencyMs = 10;
        snapshot.Hosts.Single(host => host.Config.ConfigKey == "Host_medium").AverageLatencyMs = 5;
        var backendService = new StubEndpointMonitorService(snapshot);

        foreach (var mode in new[] { Constants.TimeToFirstByte, Constants.Latency, Constants.RoundRobin, Constants.Random })
        {
            var iterator = IteratorFactory.CreateSinglePassIterator(
                backendService,
                mode,
                "/api/chat?stream=true",
                requestPriority: 1,
                out var modifiedPath);
            var visited = Drain(iterator);

            Assert.AreEqual("/chat?stream=true", modifiedPath);
            CollectionAssert.AreEqual(
                new[] { "Host_primary", "Host_fallback" },
                visited.Select(host => host.Config.ConfigKey).ToArray(),
                $"Mode '{mode}' must exhaust group 1 before group 2.");
        }

        var mediumIterator = IteratorFactory.CreateSinglePassIterator(
            backendService,
            Constants.TimeToFirstByte,
            "/api/chat",
            requestPriority: 2,
            out _);
        CollectionAssert.AreEqual(
            new[] { "Host_medium" },
            Drain(mediumIterator).Select(host => host.Config.ConfigKey).ToArray());
    }

    [TestMethod]
    [RegressionTestCase(
        "time-to-first-byte-iterator",
        "Longest route does not fall through on priority miss",
        "Matches the most specific path and returns no candidates when that route rejects the request priority, even if a broader route accepts it.")]
    public void NamedRoute_LongestPrefixPriorityMissDoesNotFallThrough()
    {
        var broad = new HostConfig(
            "host=https://broad.example.com;mode=direct;acceptablePriorities=1",
            configKey: "Host_broad");
        var specific = new HostConfig(
            "host=https://specific.example.com;mode=direct;acceptablePriorities=2",
            configKey: "Host_specific");
        var snapshot = HostCollectionSnapshot.Build(
            [broad, specific],
            [
                new PathRouteDefinition("Path_api", "/api", ["Host_broad"], true),
                new PathRouteDefinition("Path_chat", "/api/chat", ["Host_specific"], true)
            ],
            NullLogger.Instance);
        var backendService = new StubEndpointMonitorService(snapshot);

        var rejected = IteratorFactory.CreateSinglePassIterator(
            backendService,
            Constants.Latency,
            "/api/chat/completions",
            requestPriority: 1,
            out var modifiedPath);

        Assert.AreEqual("/completions", modifiedPath);
        Assert.AreEqual(0, rejected.HostCount);

        var accepted = IteratorFactory.CreateSinglePassIterator(
            backendService,
            Constants.Latency,
            "/api/chat/completions",
            requestPriority: 2,
            out _);
        CollectionAssert.AreEqual(
            new[] { "Host_specific" },
            Drain(accepted).Select(host => host.Config.ConfigKey).ToArray());
    }

    [TestMethod]
    [RegressionTestCase(
        "time-to-first-byte-iterator",
        "Via routes select one gateway transport",
        "Resolves logical backends through their shared gateway and rejects mixed direct and gateway hosts in one route.")]
    public void NamedRoute_ResolvesViaGatewayAndRejectsMixedTransport()
    {
        var gateway = new HostConfig(
            "host=https://gateway.example.com;mode=apim;probe=/status",
            configKey: "Host_apim");
        var ptu = new HostConfig(
            "host=https://ptu.example.com;mode=indirect;via=Host_apim;acceptablePriorities=1;priorityGroup=1",
            configKey: "Host_ptu");
        var paygo = new HostConfig(
            "host=https://paygo.example.com;mode=indirect;via=Host_apim;acceptablePriorities=1:2:3;priorityGroup=2",
            configKey: "Host_paygo");
        gateway.FreezeHash();
        ptu.FreezeHash();
        paygo.FreezeHash();
        var route = new PathRouteDefinition("Path_api", "/api", ["Host_ptu", "Host_paygo"], true);
        var snapshot = HostCollectionSnapshot.Build([gateway, ptu, paygo], [route], NullLogger.Instance);
        var backendService = new StubEndpointMonitorService(snapshot);

        Assert.AreEqual(1, snapshot.Hosts.Count);
        Assert.AreEqual("Host_apim", snapshot.Hosts[0].Config.ConfigKey);

        var iterator = IteratorFactory.CreateSinglePassIterator(
            backendService,
            Constants.Latency,
            "/api/chat",
            requestPriority: 1,
            out _);
        CollectionAssert.AreEqual(
            new[] { "Host_apim" },
            Drain(iterator).Select(host => host.Config.ConfigKey).ToArray());

        var direct = new HostConfig("host=https://direct.example.com;mode=direct", configKey: "Host_direct");
        Assert.ThrowsException<InvalidOperationException>(() => HostCollectionSnapshot.Build(
            [gateway, ptu, direct],
            [new PathRouteDefinition("Path_mixed", "/mixed", ["Host_ptu", "Host_direct"], true)],
            NullLogger.Instance));

        var invalidGateway = new HostConfig(
            "host=https://direct-gateway.example.com;mode=direct",
            configKey: "Host_direct_gateway");
        var invalidIndirect = new HostConfig(
            "host=https://logical.example.com;mode=indirect;via=Host_direct_gateway",
            configKey: "Host_logical");
        Assert.ThrowsException<InvalidOperationException>(() => HostCollectionSnapshot.Build(
            [invalidGateway, invalidIndirect],
            [new PathRouteDefinition("Path_invalid_gateway", "/invalid", ["Host_logical"], true)],
            NullLogger.Instance));
    }

    [TestMethod]
    [RegressionTestCase(
        "time-to-first-byte-iterator",
        "Legacy hosts remain catch-all when named routes are absent",
        "Builds Host1 and Host2 without any new fields and confirms both remain eligible for an arbitrary request priority.")]
    public void LegacyHosts_RemainCatchAllWithoutNewConfiguration()
    {
        var first = new HostConfig("host=https://legacy-one.example.com;mode=direct", configKey: "Host1");
        var second = new HostConfig("host=https://legacy-two.example.com;mode=direct", configKey: "Host2");
        var snapshot = HostCollectionSnapshot.Build([first, second], [], NullLogger.Instance);
        var backendService = new StubEndpointMonitorService(snapshot);

        var iterator = IteratorFactory.CreateSinglePassIterator(
            backendService,
            Constants.Latency,
            "/anything",
            requestPriority: 42,
            out var modifiedPath);

        Assert.AreEqual("/anything", modifiedPath);
        Assert.AreEqual(2, Drain(iterator).Count);
    }

    [TestMethod]
    [RegressionTestCase(
        "time-to-first-byte-iterator",
        "Configuration bootstrap discovers named routes and retains a valid snapshot",
        "Loads Host and Path entries through ConfigFactory, then confirms an invalid update does not replace the active route snapshot.")]
    public void ConfigFactory_LoadsNamedRoutesAndRetainsLastKnownGoodSnapshot()
    {
        var manager = new HostCollectionManager(NullLogger<HostCollectionManager>.Instance);
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Host_apim"] = "host=https://gateway.example.com;mode=apim;probe=/status",
            ["Host_ptu"] = "host=https://ptu.example.com;mode=indirect;via=Host_apim;acceptablePriorities=1;priorityGroup=1",
            ["Host_paygo"] = "host=https://paygo.example.com;mode=indirect;via=Host_apim;acceptablePriorities=1:2:3;priorityGroup=2",
            ["Path_openai"] = "prefix=/api;hosts=Host_ptu:Host_paygo;stripprefix=true"
        };

        ConfigFactory.RegisterBackends(new ProxyConfig(), appConfigSettings: settings, hostCollection: manager);

        Assert.AreEqual(1, manager.Current.PathRoutes.Count);
        Assert.AreEqual("Host_apim", manager.Current.PathRoutes[0].GatewayHost?.Config.ConfigKey);
        var activeVersion = manager.Current.Version;

        settings["Path_openai"] = "prefix=/api;hosts=Host_missing;stripprefix=true";
        ConfigFactory.RegisterBackends(new ProxyConfig(), appConfigSettings: settings, hostCollection: manager);

        Assert.AreEqual(activeVersion, manager.Current.Version);
        Assert.AreEqual("Host_apim", manager.Current.PathRoutes[0].GatewayHost?.Config.ConfigKey);
    }

    [DataTestMethod]
    [RegressionTestCase(
        "backend-route-configuration",
        "Invalid candidate snapshots retain the active configuration",
        "Applies malformed host, gateway, and Path variants and confirms the exact last known-good snapshot remains active.")]
    [DataRow("invalid-host-mode")]
    [DataRow("missing-route-prefix")]
    [DataRow("missing-route-hosts")]
    [DataRow("invalid-route-field")]
    [DataRow("invalid-strip-prefix")]
    [DataRow("invalid-prefix-query")]
    [DataRow("missing-host-reference")]
    [DataRow("duplicate-prefix")]
    [DataRow("mixed-direct-indirect")]
    [DataRow("multiple-gateways")]
    [DataRow("missing-gateway")]
    [DataRow("gateway-not-apim")]
    [DataRow("orphan-indirect")]
    public void ConfigFactory_InvalidCandidateRetainsLastKnownGoodSnapshot(string scenario)
    {
        var manager = new HostCollectionManager(NullLogger<HostCollectionManager>.Instance);
        var validSettings = CreateValidIndirectRouteSettings();
        ConfigFactory.RegisterBackends(
            new ProxyConfig(),
            appConfigSettings: validSettings,
            hostCollection: manager);

        var activeSnapshot = manager.Current;
        Assert.AreEqual(1, activeSnapshot.PathRoutes.Count, "Valid setup did not activate its route.");

        var invalidSettings = new Dictionary<string, string>(validSettings, StringComparer.OrdinalIgnoreCase);
        MakeConfigurationInvalid(invalidSettings, scenario);
        ConfigFactory.RegisterBackends(
            new ProxyConfig(),
            appConfigSettings: invalidSettings,
            hostCollection: manager);

        Assert.AreSame(
            activeSnapshot,
            manager.Current,
            $"Scenario '{scenario}' replaced the last known-good snapshot.");
    }

    [TestMethod]
    [RegressionTestCase(
        "time-to-first-byte-iterator",
        "Configuration bootstrap keeps legacy Host1 behavior without new variables",
        "Loads only Host1 and Host2 through ConfigFactory and confirms both remain catch-all hosts with no named routes.")]
    public void ConfigFactory_LoadsLegacyHostsWithoutNewVariables()
    {
        var manager = new HostCollectionManager(NullLogger<HostCollectionManager>.Instance);
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Host1"] = "host=https://legacy-one.example.com;mode=direct",
            ["Host2"] = "host=https://legacy-two.example.com;mode=direct"
        };

        ConfigFactory.RegisterBackends(new ProxyConfig(), appConfigSettings: settings, hostCollection: manager);

        Assert.AreEqual(0, manager.Current.PathRoutes.Count);
        Assert.AreEqual(2, manager.Current.CatchAllHosts.Count);
        Assert.IsTrue(manager.Current.Configs.All(config => config.AcceptablePriorities.Count == 0));
        Assert.IsTrue(manager.Current.Configs.All(config => config.PriorityGroup == 1));
        Assert.IsTrue(manager.Current.Configs.All(config => string.IsNullOrEmpty(config.Via)));
    }

    private static Dictionary<string, string> CreateValidIndirectRouteSettings() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Host_apim"] = "host=https://gateway.example.com;mode=apim;probe=/status",
            ["Host_ptu"] = "host=https://ptu.example.com;mode=indirect;via=Host_apim;acceptablePriorities=1;priorityGroup=1",
            ["Host_paygo"] = "host=https://paygo.example.com;mode=indirect;via=Host_apim;acceptablePriorities=1:2:3;priorityGroup=2",
            ["Path_openai"] = "prefix=/api;hosts=Host_ptu:Host_paygo;stripprefix=true"
        };

    private static void MakeConfigurationInvalid(Dictionary<string, string> settings, string scenario)
    {
        switch (scenario)
        {
            case "invalid-host-mode":
                settings["Host_ptu"] = "host=https://ptu.example.com;mode=unknown";
                break;
            case "missing-route-prefix":
                settings["Path_openai"] = "hosts=Host_ptu:Host_paygo;stripprefix=true";
                break;
            case "missing-route-hosts":
                settings["Path_openai"] = "prefix=/api;stripprefix=true";
                break;
            case "invalid-route-field":
                settings["Path_openai"] = "prefix=/api;hosts=Host_ptu:Host_paygo;unsupported=true";
                break;
            case "invalid-strip-prefix":
                settings["Path_openai"] = "prefix=/api;hosts=Host_ptu:Host_paygo;stripprefix=sometimes";
                break;
            case "invalid-prefix-query":
                settings["Path_openai"] = "prefix=/api?version=1;hosts=Host_ptu:Host_paygo";
                break;
            case "missing-host-reference":
                settings["Path_openai"] = "prefix=/api;hosts=Host_missing";
                break;
            case "duplicate-prefix":
                settings["Path_duplicate"] = "prefix=/api;hosts=Host_ptu";
                break;
            case "mixed-direct-indirect":
                settings["Host_direct"] = "host=https://direct.example.com;mode=direct";
                settings["Path_openai"] = "prefix=/api;hosts=Host_ptu:Host_direct";
                break;
            case "multiple-gateways":
                settings["Host_apim_secondary"] = "host=https://secondary.example.com;mode=apim;probe=/status";
                settings["Host_secondary"] = "host=https://secondary-backend.example.com;mode=indirect;via=Host_apim_secondary";
                settings["Path_openai"] = "prefix=/api;hosts=Host_ptu:Host_secondary";
                break;
            case "missing-gateway":
                settings["Host_ptu"] = "host=https://ptu.example.com;mode=indirect;via=Host_missing";
                break;
            case "gateway-not-apim":
                settings["Host_apim"] = "host=https://gateway.example.com;mode=direct";
                break;
            case "orphan-indirect":
                settings.Remove("Path_openai");
                break;
            default:
                Assert.Fail($"Unknown invalid configuration scenario '{scenario}'.");
                break;
        }
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

    private static List<BaseHostHealth> Drain(IHostIterator iterator)
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
        private readonly HostCollectionSnapshot? _snapshot;

        public StubEndpointMonitorService(List<BaseHostHealth> hosts)
        {
            _hosts = hosts;
        }

        public StubEndpointMonitorService(HostCollectionSnapshot snapshot)
        {
            _snapshot = snapshot;
            _hosts = snapshot.Hosts;
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
        public PathRouteMatch? MatchRoute(string requestPath) => _snapshot?.MatchRoute(requestPath);
    }
}
