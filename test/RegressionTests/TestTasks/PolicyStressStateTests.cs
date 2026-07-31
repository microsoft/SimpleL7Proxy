using Company.Function;

namespace SimpleL7Proxy.Test;

[TestClass]
public sealed class PolicyStressStateTests : IRegressionTestMetadata
{
    public IReadOnlyDictionary<string, RegressionFeature> RegressionFeatures { get; } =
        new Dictionary<string, RegressionFeature>
        {
            ["rate-limit-simulation"] = new(
                "Reliability & Capacity",
                "Rate-limit simulation",
                "Keeps stress-test throttle and capacity evidence accurate, isolated, and repeatable.")
        };

    private static readonly DateTime s_windowStartUtc =
        new(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    [RegressionTestCase(
        "rate-limit-simulation",
        "Each backend receives an independent token budget",
        "Exhausting one simulated backend must not reduce the token budget available to another backend.")]
    public void EachEndpointHasAnIndependentOneHundredThousandTokenBudget()
    {
        var state = new PolicyStressState();

        for (var request = 0; request < 100; request++)
        {
            Assert.IsTrue(state.TryConsume("run", "endpoint-a", s_windowStartUtc).Accepted);
        }

        var throttled = state.TryConsume("run", "endpoint-a", s_windowStartUtc.AddSeconds(30));
        var independent = state.TryConsume("run", "endpoint-b", s_windowStartUtc.AddSeconds(30));
        var snapshot = state.GetSnapshot("run", s_windowStartUtc.AddSeconds(30));

        Assert.IsFalse(throttled.Accepted);
        Assert.AreEqual(30_000, throttled.RetryAfterMilliseconds);
        Assert.IsTrue(independent.Accepted);
        Assert.AreEqual(2, snapshot.Endpoints.Count);

        var endpointA = snapshot.Endpoints.Single(endpoint => endpoint.EndpointId == "endpoint-a");
        var endpointB = snapshot.Endpoints.Single(endpoint => endpoint.EndpointId == "endpoint-b");
        Assert.AreEqual(100_000, endpointA.CurrentMinute.TokensReturned);
        Assert.AreEqual(100, endpointA.CurrentMinute.Accepted);
        Assert.AreEqual(1, endpointA.CurrentMinute.Throttled);
        Assert.AreEqual(1_000, endpointB.CurrentMinute.TokensReturned);
        Assert.AreEqual(1, endpointB.CurrentMinute.Accepted);
        Assert.AreEqual(0, endpointB.CurrentMinute.Throttled);
    }

    [TestMethod]
    [RegressionTestCase(
        "rate-limit-simulation",
        "Token budgets reset at UTC minute boundaries",
        "The simulator must start a fresh budget each minute while retaining the completed minute for diagnostics.")]
    public void WindowRollsAtTheUtcMinuteBoundaryAndRetainsCompletedStats()
    {
        var state = new PolicyStressState();
        Assert.IsTrue(state.TryConsume("run", "endpoint-a", s_windowStartUtc.AddSeconds(59)).Accepted);
        Assert.IsTrue(state.TryConsume("run", "endpoint-a", s_windowStartUtc.AddMinutes(1)).Accepted);

        var endpoint = state
            .GetSnapshot("run", s_windowStartUtc.AddMinutes(1))
            .Endpoints.Single();

        Assert.AreEqual(s_windowStartUtc.AddMinutes(1), endpoint.CurrentMinute.WindowStartUtc);
        Assert.AreEqual(1, endpoint.CurrentMinute.Accepted);
        Assert.AreEqual(1_000, endpoint.CurrentMinute.TokensReturned);
        Assert.AreEqual(1, endpoint.CompletedMinutes.Count);
        Assert.AreEqual(s_windowStartUtc, endpoint.CompletedMinutes[0].WindowStartUtc);
        Assert.AreEqual(1, endpoint.CompletedMinutes[0].Accepted);
        Assert.AreEqual(2, endpoint.Totals.Accepted);
        Assert.AreEqual(2_000, endpoint.Totals.TokensReturned);
    }

    [TestMethod]
    [RegressionTestCase(
        "rate-limit-simulation",
        "Concurrent requests cannot exceed capacity",
        "Concurrent callers must share one atomic backend budget so accepted tokens never exceed the configured limit.")]
    public void ConcurrentRequestsCannotExceedTheEndpointBudget()
    {
        var state = new PolicyStressState();
        var decisions = new TokenDecision[500];

        Parallel.For(0, decisions.Length, index =>
        {
            decisions[index] = state.TryConsume("run", "endpoint-a", s_windowStartUtc.AddSeconds(1));
        });

        Assert.AreEqual(100, decisions.Count(decision => decision.Accepted));
        Assert.AreEqual(400, decisions.Count(decision => !decision.Accepted));

        var endpoint = state.GetSnapshot("run", s_windowStartUtc.AddSeconds(1)).Endpoints.Single();
        Assert.AreEqual(100_000, endpoint.CurrentMinute.TokensReturned);
        Assert.AreEqual(100, endpoint.CurrentMinute.Accepted);
        Assert.AreEqual(400, endpoint.CurrentMinute.Throttled);
        Assert.AreEqual(500, endpoint.CurrentMinute.Requests);
    }

    [TestMethod]
    [RegressionTestCase(
        "rate-limit-simulation",
        "Reset clears only the selected stress run",
        "Cleaning up one test run must not remove statistics or budgets belonging to another run.")]
    public void ResetRemovesOnlyTheRequestedRun()
    {
        var state = new PolicyStressState();
        state.TryConsume("run-a", "endpoint-a", s_windowStartUtc);
        state.TryConsume("run-a", "endpoint-b", s_windowStartUtc);
        state.TryConsume("run-b", "endpoint-a", s_windowStartUtc);

        Assert.AreEqual(2, state.Reset("run-a"));
        Assert.AreEqual(0, state.GetSnapshot("run-a", s_windowStartUtc).Endpoints.Count);
        Assert.AreEqual(1, state.GetSnapshot("run-b", s_windowStartUtc).Endpoints.Count);
    }
}