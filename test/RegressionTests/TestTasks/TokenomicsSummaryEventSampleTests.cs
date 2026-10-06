using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Tokenomics;
using SimpleL7Proxy.Tokenomics.Llm;

namespace SimpleL7Proxy.Test;

/// <summary>
/// Generates a disk-backed sample of successful outcomes and synthetic policy decisions.
/// </summary>
[TestClass]
public sealed class TokenomicsSummaryEventSampleTests : IRegressionTestMetadata {
    public IReadOnlyDictionary<string, RegressionFeature> RegressionFeatures { get; } =
        new Dictionary<string, RegressionFeature> {
            ["tokenomics-sample"] = new(
                "Tokenomics",
                "Sample summary events",
                "Produces 1,000 NDJSON events across 20 users, four tenants, and four models with all policy actions represented.")
        };

    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Writes 950 HTTP 200 outcomes and 50 policy decisions to an attached NDJSON artifact.
    /// </summary>
    [TestMethod]
    [RegressionTestCase("tokenomics-sample", "Generate 1,000 Tokenomics sample events",
        "Writes and verifies a 95% HTTP 200 sample with the remaining events covering every policy action.")]
    public async Task GenerateSampleEvents_Writes1000EventsWithAllActions() {
        string[] models = ["gpt-4o", "gpt-4o-mini", "gpt-5", "gpt-5-mini"];
        var actions = Enum.GetValues<TokenActionEnum>();
        var events = new List<TokenomicsSummaryEvent>(1000);
        var random = new Random(42);

        for (int i = 0; i < 1000; i++) {
            int userIndex = i % 20;
            int modelIndex = (i / 20 + userIndex) % models.Length;
            var action = i < 950 ? TokenActionEnum.Bypass : actions[(i - 950) % actions.Length];
            var summary = new TokenomicsSummaryEvent(
                requestId: $"sample-request-{i + 1:D4}",
                evaluationSequence: 1,
                userId: $"sample-user-{userIndex + 1:D2}",
                tenant: $"sample-tenant-{userIndex / 5 + 1}",
                requestedModel: models[modelIndex],
                modelBefore: models[modelIndex],
                priorityBefore: 2,
                policyCondition: i < 950 ? "CapacityAvailable" : $"Sample-{action}",
                policyAction: action) {
                Method = "POST"
            };

            if (i < 950) {
                summary.PrepForFinalStats(
                    HttpStatusCode.OK,
                    TimeSpan.FromMilliseconds(random.Next(50, 2001)),
                    $"backend-{i % 4 + 1}.example.test",
                    models[modelIndex],
                    backendAttempts: 1,
                    lifetimeBackendAttempts: 1,
                    usage: new LLMStats {
                        InputTokens = random.Next(500, 2001),
                        CachedTokens = random.Next(0, 501),
                        OutputTokens = random.Next(50, 501)
                    });
            } else {
                // These are schema samples, not executions of the policy engine.
                switch (action) {
                    case TokenActionEnum.Reject:
                        summary.SetDecision(TokenDecisionEnum.Rejected, false);
                        summary.SetLocalStatus(403);
                        break;
                    case TokenActionEnum.Throttle:
                        summary.SetDecision(TokenDecisionEnum.Throttled, false);
                        summary.SetLocalStatus(429);
                        break;
                    case TokenActionEnum.Delay:
                    case TokenActionEnum.WaitForReset:
                        summary.SetDecision(TokenDecisionEnum.Delayed, false);
                        summary.SetRetryAfter(1000);
                        break;
                    case TokenActionEnum.Requeue:
                        summary.SetDecision(TokenDecisionEnum.Requeued, false);
                        break;
                    case TokenActionEnum.IncreasePriority:
                    case TokenActionEnum.DecreasePriority:
                        summary.SetPriority(action == TokenActionEnum.IncreasePriority ? 1 : 3);
                        summary.SetDecision(TokenDecisionEnum.Requeued, false);
                        break;
                    case TokenActionEnum.ChangeModel:
                    case TokenActionEnum.UpgradeModel:
                    case TokenActionEnum.DowngradeModel:
                        int offset = action == TokenActionEnum.DowngradeModel ? 3 : 1;
                        summary.SetModel(models[(modelIndex + offset) % models.Length]);
                        summary.SetDecision(TokenDecisionEnum.AllowedWithChanges, true);
                        break;
                    case TokenActionEnum.IncreaseLimit:
                    case TokenActionEnum.DecreaseLimit:
                    case TokenActionEnum.Cap:
                        summary.SetDecision(TokenDecisionEnum.AllowedWithChanges, true);
                        break;
                    case TokenActionEnum.None:
                    case TokenActionEnum.Bypass:
                        summary.SetDecision(TokenDecisionEnum.Allowed, true);
                        break;
                    default:
                        Assert.Fail($"No sample decision defined for action {action}.");
                        break;
                }
            }

            events.Add(summary);
        }

        var shuffledEvents = events.ToArray();
        random.Shuffle(shuffledEvents);
        string artifactDirectory = Path.Combine(
            TestContext.TestResultsDirectory ?? Path.GetTempPath(),
            $"tokenomics-sample-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifactDirectory);
        string artifactPath = Path.Combine(artifactDirectory, "tokenomics-summary-events.ndjson");
        await File.WriteAllLinesAsync(
            artifactPath,
            shuffledEvents.Select(summary => ProxyEvent.ConvertToJson(summary, new Dictionary<string, string> {
                ["Type"] = "S7P-" + summary.Type,
                ["MID"] = summary.MID!,
                ["Status"] = ((int)summary.Status).ToString(CultureInfo.InvariantCulture),
                ["Method"] = summary.Method!
            })),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        TestContext.AddResultFile(artifactPath);
        TestContext.WriteLine($"Tokenomics sample artifact: {artifactPath}");

        var lines = await File.ReadAllLinesAsync(artifactPath);
        Assert.AreEqual(1000, lines.Length);
        var records = lines.Select(line => JsonSerializer.Deserialize<Dictionary<string, string>>(line)!).ToArray();
        Assert.AreEqual(1000, records.Select(record => record["MID"]).Distinct().Count());
        Assert.AreEqual(950, records.Count(record => record["Status"] == "200"));
        Assert.AreEqual(950, records.Count(record =>
            record["RecordKind"] == "RequestOutcome" && record["Success"] == "true" && record["UsageAvailable"] == "true"));
        Assert.AreEqual(50, records.Count(record => record["RecordKind"] == "PolicyDecision"));
        Assert.AreEqual(20, records.Select(record => record["UserId"]).Distinct().Count());
        Assert.IsTrue(records.GroupBy(record => record["UserId"]).All(group => group.Count() == 50));
        Assert.AreEqual(4, records.Select(record => record["Tenant"]).Distinct().Count());
        Assert.IsTrue(records.GroupBy(record => record["Tenant"]).All(group => group.Count() == 250));
        CollectionAssert.AreEquivalent(models, records.Select(record => record["RequestedModel"]).Distinct().ToArray());
        Assert.IsTrue(records.GroupBy(record => record["RequestedModel"]).All(group => group.Count() == 250));
        CollectionAssert.AreEquivalent(
            actions.Select(action => action.ToString()).ToArray(),
            records.Where(record => record["RecordKind"] == "PolicyDecision")
                .Select(record => record["PolicyAction"]).Distinct().ToArray());
        Assert.IsTrue(records.Where(record => record["RecordKind"] == "PolicyDecision")
            .All(record => record["Decision"] != "Pending"));
        Assert.IsTrue(records.All(record => record["Type"] == "S7P-Tokenomics"));
    }
}
