using Microsoft.Extensions.Logging.Abstractions;
using SimpleL7Proxy.Config;
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
                            { "name": "green-cohort", "if": { "field": "Hash:UserID", "match": "equals", "value": "20" }, "then": { "route": "green" } },
                            { "name": "non-matching-rule", "if": { "field": "UserID", "match": "equals", "value": "other" }, "then": { "fallback": "no" }, "else": { "fallback": "yes" } }
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
        CollectionAssert.AreEqual(new[] { "green-cohort", "non-matching-rule-else" }, matchedRuleNames);
    }

    private sealed class StubUserProfileService(UserProfileSnapshot snapshot) : IUserProfileService
    {
        public UserProfileSnapshot? GetUserProfileSnapshot(string userId) => snapshot;

        public (Dictionary<string, string> profile, bool isSoftDeleted, bool isStale) GetUserProfile(string userId)
            => (new Dictionary<string, string>(), false, false);

        public bool IsUserSuspended(string userId) => false;

        public bool IsAuthAppIDValid(string authAppId) => false;

        public AsyncClientInfo? GetAsyncParams(string userId) => null;
    }
}
