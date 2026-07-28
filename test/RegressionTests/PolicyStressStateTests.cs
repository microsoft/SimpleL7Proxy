using Company.Function;

namespace SimpleL7Proxy.Test;

[TestClass]
public sealed class PolicyStressStateTests
{
    private static readonly DateTime s_windowStartUtc =
        new(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
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