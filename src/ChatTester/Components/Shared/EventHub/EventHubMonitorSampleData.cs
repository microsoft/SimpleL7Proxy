namespace chat_tester.Components.Shared;

/// <summary>
/// Temporary stand-in for the server-side Event Hub reader (not built yet). Seeds the
/// <see cref="EventHubMonitorStore"/> with representative backend health, fleet info, and a
/// batch of requests so the monitor UI renders while the top-down design is fleshed out.
/// Delete this once the real reader pushes live data into the store.
/// </summary>
public static class EventHubMonitorSampleData
{
    public static void Seed(EventHubMonitorStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        store.UpdateBackends(new[]
        {
            new BackendHealthSnapshot { Name = "APIM backend", Url = "https://nvmtr2apim.azure-api.net", Status = "\u2713 Active", LatencyMs = 474.794, SuccessRate = 100, Calls = 128, Errors = 0, Css = "healthy" },
            new BackendHealthSnapshot { Name = "PAYGO", Url = "https://nvm2.openai.azure.com", Status = "\u2713 Active", LatencyMs = 312.4, SuccessRate = 98, Calls = 96, Errors = 2, Css = "healthy" },
            new BackendHealthSnapshot { Name = "BAD-PAYGO", Url = "https://nvm2sd.openai.azure.com", Status = "\u26A0 Throttled", LatencyMs = 6779.0, SuccessRate = 61, Calls = 40, Errors = 15, Css = "degraded" },
        });

        store.UpdateFleet(new FleetInfoSnapshot
        {
            ActiveHosts = 1,
            TotalHosts = 1,
            ProbeLatencyMs = 474.794,
            LoadBalancingMode = "latency",
            PrimaryBackend = "APIM",
            ProxyVersion = "2.2.13",
        });

        store.AddRequests(BuildSampleRequests());
    }

    private static IEnumerable<MultiRequestStatusItem> BuildSampleRequests()
    {
        // Each entry represents one request assembled from its lifecycle events:
        // S7P-ProxyRequestEnqueued (queue), S7P-BackendRequest (attempt + backendLog),
        // and S7P-ProxyRequest (final status + total latency).
        var samples = new (int Status, double QueueMs, double TotalMs, string User, string BackendLog, string Response)[]
        {
            (200, 8.4, 1524.0, "nina", SuccessLog, "{ \"output\": \"Sure, here's a joke...\" }"),
            (200, 5.1, 968.0, "amir", SuccessLog, "{ \"output\": \"The mitochondria...\" }"),
            (500, 9.7, 7571.8, "nina", FailureLog, "{ \"error\": { \"message\": \"No active hosts were able to handle the request\" } }"),
            (200, 6.7, 742.0, "sara", SuccessLog, "{ \"output\": \"Certainly!\" }"),
            (429, 0.4, 15.0, "amir", ThrottleLog, "{ \"error\": { \"message\": \"No active hosts\", \"retryAfter\": 15000 } }"),
            (200, 12.2, 1140.0, "nina", SuccessLog, "{ \"output\": \"Done.\" }"),
            (408, 4.0, 12040.0, "sara", TimeoutLog, "{ \"error\": { \"message\": \"Request timed out\" } }"),
            (200, 3.9, 812.0, "amir", SuccessLog, "{ \"output\": \"Here you go.\" }"),
            (500, 10.1, 6810.0, "nina", FailureLog, "{ \"error\": { \"message\": \"Backend proxy status code: 500\" } }"),
            (200, 7.3, 1301.0, "sara", SuccessLog, "{ \"output\": \"Absolutely.\" }"),
        };

        foreach (var (status, queueMs, totalMs, user, backendLog, response) in samples)
        {
            var failed = status >= 400;
            var guid = Guid.NewGuid();
            yield return new MultiRequestStatusItem
            {
                Status = failed ? "Failed" : "Completed",
                StatusMessage = $"{status} {(failed ? "error" : "OK")}",
                StatusCode = status,
                ContentType = "application/json; charset=utf-8",
                TimeToFirstByte = TimeSpan.FromMilliseconds(queueMs),
                Duration = TimeSpan.FromMilliseconds(totalMs),
                Chunks = failed ? 0 : 1,
                TotalBytes = response.Length,
                RequestHeadersText = string.Join('\n', new[]
                {
                    "POST /openai/v1/chat/completions",
                    "Host: localhost:8000",
                    "Content-Type: application/json; charset=utf-8",
                    "Content-Length: 277",
                    "S7P-Priority: 1",
                    $"UserID: {user}",
                    $"x-ms-request-id: {guid}",
                }),
                ResponseHeadersText = string.Join('\n', new[]
                {
                    $"HTTP/1.1 {status}",
                    "Content-Type: application/json; charset=utf-8",
                    $"x-ms-request-id: {guid}",
                    $"backendLog: {backendLog}",
                }),
                RequestBodyDisplay = "{ \"model\": \"gpt-4o\", \"messages\": [ { \"role\": \"user\", \"content\": \"...\" } ] }",
                ResponseBody = response,
                IsComplete = true,
                IsFailed = failed,
            };
        }
    }

    private const string SuccessLog =
        "Proxy-Queue-Duration: 8.4 | Proxy-Process-Duration: 3.2 | Priority: 1 | 0.001s Begin | " +
        "0.001s THROTTLED: (none) | 0.001s RETRIES LEFT: 2 CYCLE: 1 INDEX: 0 Unthrottled Backends: 2 | " +
        "0.021s Using PAYGO URL: https://nvm2.openai.azure.com/openai LIMIT: off | " +
        "1.524s StatusCode: 200 - Success | 1.524s CALL SUCCESSFUL";

    private const string FailureLog =
        "Proxy-Queue-Duration: 9.7093 | Proxy-Process-Duration: 4.1614 | Priority: 1 | 0.385s Begin | " +
        "0.443s THROTTLED: (none) | 0.455s RETRIES LEFT: 1 CYCLE: 1 INDEX: 0 Unthrottled Backends: 1 | " +
        "0.464s Using PAYGO URL: https://nvm2.openai.azure.com/openai LIMIT: off | " +
        "6.779s Error status 500 after 6.3s | 6.817s RETRIES LEFT: exhausted CYCLE: 2 INDEX: 0 Unthrottled Backends: 1 | " +
        "6.898s THROTTLED: PAYGO Retry-After: 00:10 | 6.899s CALL INCOMPLETE, Unthrottled Backends: 0";

    private const string ThrottleLog =
        "Priority: 2 | 0.001s Begin | 0.001s ActiveHosts: 0 | 0.001s No active hosts | " +
        "0.001s Retry-After: 15000 | 0.001s ENQUEUE FAILED";

    private const string TimeoutLog =
        "Proxy-Queue-Duration: 4.0 | Priority: 1 | 0.001s Begin | " +
        "0.010s Using PAYGO URL: https://nvm2.openai.azure.com/openai LIMIT: off | " +
        "12.001s Error status 408 after 12.0s | 12.040s CALL INCOMPLETE, request timed out";
}
