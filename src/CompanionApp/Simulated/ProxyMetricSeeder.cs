using CompanionApp.Components.Shared;
using CompanionApp.Components.Shared.EventHub;

namespace CompanionApp.Simulated;

public static class ProxyMetricsSeeder
{
    public static void Seed(ProxyMetricsCatalog proxyMetricsCatalog)
    {
        proxyMetricsCatalog.Publish(new List<ParsedEventRecord>
        {
            // Server + fleet health (drives the Server and Backends metric groups).
            new(string.Empty, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Type"] = "S7P-Backend",
                ["Date"] = DateTimeOffset.UtcNow.ToString("O"),
                ["Timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
                ["Ver"] = "9.0.0-preview-ui",
                ["LoadBalanceMode"] = "latency",
                ["ActiveHostsCount"] = "3",
                ["CPU-Usage"] = "38%",
                ["Memory-Usage"] = "1.2 GB",
                ["Open-Connections"] = "642",
                ["ThreadPoolSaturation"] = "41%",
                ["Response-Content-Length"] = "1984",
                ["1-Host"] = "https://backend-a.contoso.net",
                ["1-Status"] = "active",
                ["1-Latency"] = "112",
                ["2-Host"] = "https://backend-b.contoso.net",
                ["2-Status"] = "active",
                ["2-Latency"] = "127",
                ["3-Host"] = "https://backend-c.contoso.net",
                ["3-Status"] = "throttled",
                ["3-Latency"] = "249",
            }),
            // Endpoint sample (drives the Endpoints metric group).
            new(string.Empty, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Method"] = "POST",
                ["Path"] = "/chat/completions",
                ["Uri"] = "https://proxy.contoso.net/chat/completions",
                ["RequestType"] = "chat",
                ["RequestHost"] = "proxy.contoso.net",
                ["Total-Latency"] = "285",
                ["Request-Queue-Duration"] = "18",
                ["Connection-Establishment-Time"] = "14",
            }),
            // Sample request events (drive the Request and Models metric groups).
            SeedRequest(
                "mid-001", "S7P-ProxyRequest", 200, "/chat/completions",
                "Model", "gpt-4o-mini", "https://backend-a.contoso.net"),
            SeedRequest(
                "mid-002", "S7P-ProxyRequest", 200, "/chat/completions",
                "DeploymentName", "gpt-4o", "https://backend-b.contoso.net"),
            SeedRequest(
                "mid-003", "S7P-ProxyRequest", 429, "/embeddings",
                "Model", "text-embedding-3-large", "https://backend-c.contoso.net"),
            SeedRequest(
                "mid-004", "S7P-ProxyRequestRequeued", 503, "/responses",
                "ModelDeployment", "gpt-4.1-mini", "https://backend-c.contoso.net"),
            SeedRequest(
                "mid-005", "S7P-CircuitBreakerError", 503, "/chat/completions",
                "Model", "gpt-4o-mini", "https://backend-b.contoso.net"),
        });
    }

    private static ParsedEventRecord SeedRequest(
        string mid,
        string type,
        int status,
        string path,
        string modelKey,
        string model,
        string backendHost)
    {
        return new ParsedEventRecord(
            string.Empty,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Type"] = type,
                ["MID"] = mid,
                ["Status"] = status.ToString(),
                ["Path"] = path,
                [modelKey] = model,
                ["Backend-Host"] = backendHost,
            });
    }
}