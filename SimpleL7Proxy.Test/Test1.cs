using Microsoft.Extensions.Logging.Abstractions;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.DTO;
using SimpleL7Proxy.Proxy;
using SimpleL7Proxy.Rules;
using SimpleL7Proxy.User;

namespace SimpleL7Proxy.Test;

[TestClass]
public sealed class Test1
{
    [TestInitialize]
    public void TestInit()
    {
        // This method is called before each test method.
    }

    [TestCleanup]
    public void TestCleanup()
    {
        // This method is called after each test method.
    }

    [TestMethod]
    public void ProfileEnricher_AppliesSnapshotHeadersAndRules()
    {
        var options = new ProxyConfig
        {
            UseProfiles = true,
            UserProfileHeader = "X-UserProfile",
            UniqueUserHeaders = ["X-Tenant"]
        };
        var ruleConfig = RuleConfigParser.ParseRules(
                        """
                        [
                            { "name": "green-cohort", "if": { "name": "user-hash", "field": "Hash:UserID", "match": "equals", "value": "20" }, "then": { "name": "green-route", "set": { "route": "green" } } },
                            { "name": "non-matching-rule", "if": { "name": "other-user", "field": "UserID", "match": "equals", "value": "other" }, "then": { "name": "fallback-no", "set": { "fallback": "no" } }, "else": { "name": "fallback-yes", "set": { "fallback": "yes" } } }
                        ]
                        """);
        var snapshot = new UserProfileSnapshot(
            new Dictionary<string, string> { ["X-Tenant"] = "a" },
            ruleConfig,
            new RuleProcessor(ruleConfig),
            false,
            null);
        var service = new StubUserProfileService(snapshot);
        var enricher = new ProfileEnricher(options, service, NullLogger<ProfileEnricher>.Instance);
        using var request = new RequestData
        {
            Path = "/v1/chat/completions",
            Method = "POST"
        };
        request.Headers["X-UserProfile"] = "profile-a";

        var matchedRuleNames = enricher.Enrich(request);

        Assert.AreEqual("profile-a", request.profileUserId);
        Assert.AreEqual("a", request.Headers["X-Tenant"]);
        Assert.AreEqual("a", request.UserID);
        Assert.AreEqual("green", request.Headers["route"]);
        Assert.AreEqual("yes", request.Headers["fallback"]);
        CollectionAssert.AreEqual(
            new[] { "green-cohort/user-hash/green-route", "non-matching-rule/fallback-yes" },
            matchedRuleNames);
    }

    [TestMethod]
    public void RequestDataDtoV1_S7PHash_RoundTripsThroughJsonAndPopulate()
    {
        using var source = new RequestData
        {
            Guid = Guid.NewGuid(),
            Path = "/v1/chat/completions",
            Method = "POST",
            S7PHash = 73
        };
        source.Headers["X-Test"] = "value";

        var json = new RequestDataDtoV1(source).Serialize();
        var deserialized = RequestDataDtoV1.Deserialize(json);
        using var restored = new RequestData();

        Assert.IsNotNull(deserialized);
        deserialized.PopulateInto(restored);
        Assert.AreEqual((short)73, deserialized.S7PHash);
        Assert.AreEqual((short)73, restored.S7PHash);
        Assert.AreEqual(source.Guid, restored.Guid);
        Assert.AreEqual("value", restored.Headers["X-Test"]);
    }

    [TestMethod]
    public void RequestDataDtoV1_OldPayloadWithoutS7PHash_DefaultsToZero()
    {
        var deserialized = RequestDataDtoV1.Deserialize("{}");
        using var restored = new RequestData { S7PHash = 99 };

        Assert.IsNotNull(deserialized);
        deserialized.PopulateInto(restored);
        Assert.AreEqual((short)0, deserialized.S7PHash);
        Assert.AreEqual((short)0, restored.S7PHash);
    }

    [TestMethod]
    public void ProfileEnricher_ProfilesDisabled_ComputesUserIdWithoutSnapshot()
    {
        var options = new ProxyConfig
        {
            UseProfiles = false,
            UniqueUserHeaders = ["X-User"]
        };
        var enricher = new ProfileEnricher(
            options,
            new StubUserProfileService(null),
            NullLogger<ProfileEnricher>.Instance);
        using var request = new RequestData();
        request.Headers["X-User"] = "user-a";

        var matchedRuleNames = enricher.Enrich(request);

        Assert.AreEqual("user-a", request.UserID);
        Assert.AreEqual(0, matchedRuleNames.Length);
    }

    [TestMethod]
    public void ProfileEnricher_RequiredProfileHeaderMissing_ThrowsUnknownProfile()
    {
        var options = new ProxyConfig
        {
            UseProfiles = true,
            UserConfigRequired = true,
            UserProfileHeader = "X-UserProfile"
        };
        var enricher = new ProfileEnricher(
            options,
            new StubUserProfileService(null),
            NullLogger<ProfileEnricher>.Instance);
        using var request = new RequestData();

        var exception = Assert.ThrowsException<ProxyErrorException>(() => enricher.Enrich(request));

        Assert.AreEqual(ProxyErrorException.ErrorType.UnknownProfile, exception.Type);
        Assert.AreEqual(System.Net.HttpStatusCode.Forbidden, exception.StatusCode);
    }

    [TestMethod]
    public void ProfileEnricher_ProfileSnapshotMissing_ThrowsUnknownProfile()
    {
        var options = new ProxyConfig
        {
            UseProfiles = true,
            UserProfileHeader = "X-UserProfile"
        };
        var enricher = new ProfileEnricher(
            options,
            new StubUserProfileService(null),
            NullLogger<ProfileEnricher>.Instance);
        using var request = new RequestData();
        request.Headers["X-UserProfile"] = "missing-profile";

        var exception = Assert.ThrowsException<ProxyErrorException>(() => enricher.Enrich(request));

        Assert.AreEqual(ProxyErrorException.ErrorType.UnknownProfile, exception.Type);
        StringAssert.Contains(exception.Message, "missing-profile");
    }

    [TestMethod]
    public void ProfileEnricher_RulelessSnapshot_AppliesHeadersAndReturnsNoNames()
    {
        var options = new ProxyConfig
        {
            UseProfiles = true,
            UserProfileHeader = "X-UserProfile",
            UniqueUserHeaders = ["X-Tenant"]
        };
        var snapshot = new UserProfileSnapshot(
            new Dictionary<string, string> { ["X-Tenant"] = "tenant-a" },
            null,
            null,
            false,
            null);
        var enricher = new ProfileEnricher(
            options,
            new StubUserProfileService(snapshot),
            NullLogger<ProfileEnricher>.Instance);
        using var request = new RequestData();
        request.Headers["X-UserProfile"] = "profile-a";

        var matchedRuleNames = enricher.Enrich(request);

        Assert.AreEqual("tenant-a", request.Headers["X-Tenant"]);
        Assert.AreEqual("tenant-a", request.UserID);
        Assert.AreEqual(0, matchedRuleNames.Length);
    }

    [TestMethod]
    public void ProfileEnricher_RulesReceiveRequestMetadataAndCanOverrideProfileHeaders()
    {
        var options = new ProxyConfig
        {
            UseProfiles = true,
            UserProfileHeader = "X-UserProfile",
            UniqueUserHeaders = ["X-Tenant"]
        };
        var ruleConfig = RuleConfigParser.ParseRules(
            """
            [
              { "name": "path-rule", "if": { "name": "path-match", "field": "Path", "match": "equals", "value": "/test" }, "then": { "name": "path-set", "set": { "path-seen": "yes" } } },
              { "name": "method-rule", "if": { "name": "method-match", "field": "Method", "match": "equals", "value": "POST" }, "then": { "name": "method-set", "set": { "method-seen": "yes" } } },
              { "name": "user-rule", "if": { "name": "user-match", "field": "UserID", "match": "equals", "value": "tenant-a" }, "then": { "name": "user-set", "set": { "user-seen": "yes" } } },
              { "name": "profile-rule", "if": { "name": "profile-match", "field": "ProfileUserID", "match": "equals", "value": "profile-a" }, "then": { "name": "profile-set", "set": { "profile-seen": "yes" } } },
              { "name": "hash-rule", "if": { "name": "hash-match", "field": "S7PHash", "match": "equals", "value": "7" }, "then": { "name": "hash-set", "set": { "hash-seen": "yes" } } },
              { "name": "override-rule", "if": { "name": "route-match", "field": "Route", "match": "equals", "value": "profile" }, "then": { "name": "route-set", "set": { "Route": "rule" } } }
            ]
            """);
        var snapshot = new UserProfileSnapshot(
            new Dictionary<string, string>
            {
                ["X-Tenant"] = "tenant-a",
                ["Route"] = "profile"
            },
            ruleConfig,
            new RuleProcessor(ruleConfig),
            false,
            null);
        var enricher = new ProfileEnricher(
            options,
            new StubUserProfileService(snapshot),
            NullLogger<ProfileEnricher>.Instance);
        using var request = new RequestData
        {
            Path = "/test",
            Method = "POST",
            S7PHash = 7
        };
        request.Headers["X-UserProfile"] = "profile-a";

        var matchedRuleNames = enricher.Enrich(request);

        Assert.AreEqual("yes", request.Headers["path-seen"]);
        Assert.AreEqual("yes", request.Headers["method-seen"]);
        Assert.AreEqual("yes", request.Headers["user-seen"]);
        Assert.AreEqual("yes", request.Headers["profile-seen"]);
        Assert.AreEqual("yes", request.Headers["hash-seen"]);
        Assert.AreEqual("rule", request.Headers["Route"]);
        Assert.AreEqual(6, matchedRuleNames.Length);
    }

    private sealed class StubUserProfileService(UserProfileSnapshot? snapshot) : IUserProfileService
    {
        public UserProfileSnapshot? GetUserProfileSnapshot(string userId) => snapshot;

        public (Dictionary<string, string> profile, bool isSoftDeleted, bool isStale) GetUserProfile(string userId)
            => (new Dictionary<string, string>(), false, false);

        public bool IsUserSuspended(string userId) => false;

        public bool IsAuthAppIDValid(string authAppId) => false;

        public AsyncClientInfo? GetAsyncParams(string userId) => null;
    }
}
