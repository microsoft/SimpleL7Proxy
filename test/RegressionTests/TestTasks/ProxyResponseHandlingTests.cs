using System.Net;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Proxy;

namespace SimpleL7Proxy.Test;

[TestClass]
public sealed class ProxyResponseHandlingTests : IRegressionTestMetadata
{
    public IReadOnlyDictionary<string, RegressionFeature> RegressionFeatures { get; } =
        new Dictionary<string, RegressionFeature>
        {
            ["backend-requeue"] = new(
                "Reliability & Capacity",
                "429 retry scheduling",
                "Ensures only explicitly retryable throttling responses are requeued and that backend retry timing is interpreted consistently."),
            ["backend-exhaustion"] = new(
                "Reliability & Capacity",
                "Exhausted backend response",
                "Preserves the most useful terminal status and ordered attempt evidence when every backend attempt is exhausted.")
        };

    [DataTestMethod]
    [RegressionTestCase(
        "backend-requeue",
        "Only opted-in 429 responses are requeued",
        "A response must be HTTP 429 with S7PREQUEUE=true before the proxy schedules another queue attempt.")]
    [DataRow(200, "true", false, 0, "Pass through")]
    [DataRow(429, null, false, 0, "Pass through")]
    [DataRow(429, "false", false, 0, "Process 429")]
    [DataRow(429, "TRUE", true, 1000, "Process 429")]
    public void RequeueResponse_RequiresOptedInTooManyRequests(
        int statusCode,
        string? requeueHeader,
        bool expectedShouldRequeue,
        int expectedRetryMilliseconds,
        string expectedState)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)statusCode);
        if (requeueHeader != null)
        {
            response.Headers.TryAddWithoutValidation("S7PREQUEUE", requeueHeader);
        }

        var requestAttempt = new ProxyEvent();
        var requestState = "Pass through";

        var result = ProxyWorker.CheckRequeueResponse(
            response,
            statusCode,
            requestAttempt,
            ref requestState);

        Assert.AreEqual(expectedShouldRequeue, result.shouldRequeue);
        Assert.AreEqual(expectedRetryMilliseconds, result.retryMs);
        Assert.AreEqual(expectedState, requestState);
    }

    [DataTestMethod]
    [RegressionTestCase(
        "backend-requeue",
        "Retry delay headers use defined precedence",
        "retry-after-ms must win over retry-after seconds, while missing or invalid values use the one-second default.")]
    [DataRow("250", "9", 250)]
    [DataRow(null, "3", 3000)]
    [DataRow("invalid", "4", 4000)]
    [DataRow("invalid", "invalid", 1000)]
    [DataRow(null, null, 1000)]
    public void RequeueResponse_UsesRetryHeaderPrecedence(
        string? retryAfterMilliseconds,
        string? retryAfterSeconds,
        int expectedRetryMilliseconds)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("S7PREQUEUE", "true");
        if (retryAfterMilliseconds != null)
        {
            response.Headers.TryAddWithoutValidation("retry-after-ms", retryAfterMilliseconds);
        }
        if (retryAfterSeconds != null)
        {
            response.Headers.TryAddWithoutValidation("retry-after", retryAfterSeconds);
        }

        var requestAttempt = new ProxyEvent();
        var requestState = "Pass through";

        var result = ProxyWorker.CheckRequeueResponse(
            response,
            (int)HttpStatusCode.TooManyRequests,
            requestAttempt,
            ref requestState);

        Assert.IsTrue(result.shouldRequeue);
        Assert.AreEqual(expectedRetryMilliseconds, result.retryMs);
        Assert.AreEqual("Process 429", requestState);
    }

    [TestMethod]
    [RegressionTestCase(
        "backend-requeue",
        "Inspected throttling headers remain observable",
        "A 429 response copies backend diagnostic headers into attempt telemetry while excluding restricted transport headers.")]
    public void RequeueResponse_CopiesDiagnosticHeadersAndExcludesRestrictedHeaders()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("S7PREQUEUE", "false");
        response.Headers.TryAddWithoutValidation("backendLog", "backend-a throttled");
        response.Headers.Date = DateTimeOffset.UtcNow;

        var requestAttempt = new ProxyEvent();
        var requestState = "Pass through";

        var result = ProxyWorker.CheckRequeueResponse(
            response,
            (int)HttpStatusCode.TooManyRequests,
            requestAttempt,
            ref requestState);

        Assert.IsFalse(result.shouldRequeue);
        Assert.AreEqual("backend-a throttled", requestAttempt["backendLog"]);
        Assert.AreEqual("false", requestAttempt["S7PREQUEUE"]);
        Assert.IsFalse(requestAttempt.ContainsKey("Date"));
    }

    [DataTestMethod]
    [RegressionTestCase(
        "backend-exhaustion",
        "Exhausted attempts select the terminal status",
        "The final response defaults to 503, preserves matching statuses, and uses the latest timeout or throttling-family status.")]
    [DataRow("", 503, true)]
    [DataRow("500,500", 500, true)]
    [DataRow("408,412,429", 429, true)]
    [DataRow("500,503", 503, false)]
    [DataRow("invalid,429", 429, true)]
    [DataRow("invalid", 503, true)]
    public void GenerateErrorMessage_SelectsExpectedStatus(
        string statuses,
        int expectedStatusCode,
        bool expectedStatusMatches)
    {
        var attempts = statuses.Length == 0
            ? []
            : statuses
                .Split(',')
                .Select(status => new Dictionary<string, string> { ["Status"] = status })
                .ToList();

        ProxyHelperUtils.GenerateErrorMessage(
            attempts,
            out var message,
            out var statusMatches,
            out var statusCode);

        Assert.AreEqual(expectedStatusCode, statusCode);
        Assert.AreEqual(expectedStatusMatches, statusMatches);
        StringAssert.StartsWith(message.ToString(), "Request Summary:");
    }

    [TestMethod]
    [RegressionTestCase(
        "backend-exhaustion",
        "Exhausted attempt evidence retains order",
        "The generated request summary must retain every backend attempt in execution order for diagnostics.")]
    public void GenerateErrorMessage_PreservesAttemptOrder()
    {
        var attempts = new List<Dictionary<string, string>>
        {
            new() { ["Status"] = "500", ["Backend"] = "backend-a" },
            new() { ["Status"] = "429", ["Backend"] = "backend-b" }
        };

        ProxyHelperUtils.GenerateErrorMessage(
            attempts,
            out var message,
            out _,
            out _);

        var output = message.ToString();
        var firstAttempt = output.IndexOf("\"Attempt-1\"", StringComparison.Ordinal);
        var secondAttempt = output.IndexOf("\"Attempt-2\"", StringComparison.Ordinal);

        Assert.IsTrue(firstAttempt >= 0);
        Assert.IsTrue(secondAttempt > firstAttempt);
        StringAssert.Contains(output, "backend-a");
        StringAssert.Contains(output, "backend-b");
    }
}