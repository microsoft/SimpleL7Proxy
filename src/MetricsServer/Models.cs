using System.Text.Json.Serialization;
using SimpleL7Proxy.Tokenomics;

namespace MetricsServer;

/// <summary>
/// A single rollup record published by another service.
/// Counters are additive; the server never stores individual requests.
/// </summary>
public sealed class RollupRecord
{
    /// <summary>User the rollup belongs to. Empty values are recorded as "unknown".</summary>
    [JsonPropertyName("user")]
    public string? User { get; set; }

    /// <summary>Model the rollup belongs to. Empty values are recorded as "unknown".</summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>Number of requests represented by this rollup.</summary>
    [JsonPropertyName("requests")]
    public long Requests { get; set; }

    /// <summary>Number of successful requests represented by this rollup.</summary>
    [JsonPropertyName("successes")]
    public long Successes { get; set; }

    /// <summary>Number of failed requests represented by this rollup.</summary>
    [JsonPropertyName("failures")]
    public long Failures { get; set; }

    /// <summary>Sum of request latencies, in milliseconds.</summary>
    [JsonPropertyName("latencyMsTotal")]
    public long LatencyMsTotal { get; set; }

    /// <summary>Largest observed request latency, in milliseconds.</summary>
    [JsonPropertyName("latencyMsMax")]
    public long LatencyMsMax { get; set; }

    /// <summary>Prompt tokens consumed.</summary>
    [JsonPropertyName("promptTokens")]
    public long PromptTokens { get; set; }

    /// <summary>Completion tokens produced.</summary>
    [JsonPropertyName("completionTokens")]
    public long CompletionTokens { get; set; }

    /// <summary>Unix timestamp, in seconds, the rollup applies to. Defaults to now when zero.</summary>
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}

/// <summary>
/// Batch envelope accepted by the rollup endpoint in addition to a bare JSON array.
/// </summary>
public sealed class RollupBatch
{
    /// <summary>Rollup records contained in the batch.</summary>
    [JsonPropertyName("records")]
    public List<RollupRecord>? Records { get; set; }
}

/// <summary>
/// A single token/spend delta entry parsed from one CSV data row of the tokenomics rollups
/// endpoint. Not JSON-serialized: the request body is CSV text with a header row, not JSON.
/// </summary>
public sealed class TokenomicsRollupEntry
{
    /// <summary>User the delta belongs to.</summary>
    public string? UserId { get; set; }

    /// <summary>Model the delta belongs to.</summary>
    public string? Model { get; set; }

    /// <summary>UTC day the delta applies to (e.g. "2024-01-31").</summary>
    public string? DayUtc { get; set; }

    /// <summary>Input (prompt) tokens consumed.</summary>
    public long InputTokens { get; set; }

    /// <summary>Output (completion) tokens produced.</summary>
    public long OutputTokens { get; set; }

    /// <summary>Cost of the delta, in US dollars.</summary>
    public double CostUsd { get; set; }
}

/// <summary>
/// Result of a tokenomics rollups enqueue call.
/// </summary>
public sealed class TokenomicsRollupResponse
{
    /// <summary>Batch id supplied by the client for this request, or null when omitted.</summary>
    [JsonPropertyName("batchId")]
    public string? BatchId { get; set; }

    /// <summary>
    /// Total number of batches the background processor has completed since process start.
    /// Reflects processor progress at response time, not necessarily including this request's
    /// batch, since parsing happens asynchronously on the next processor tick.
    /// </summary>
    [JsonPropertyName("batchesProcessed")]
    public long BatchesProcessed { get; set; }

    /// <summary>
    /// Most recently enqueued batch ids for the replica that sent this request, newest first,
    /// capped at 10. Each ACA replica maintains its own independent batch history.
    /// </summary>
    [JsonPropertyName("recentBatches")]
    public List<string> RecentBatches { get; set; } = new();

    /// <summary>
    /// Batch ids for the replica that sent this request which have been received but not yet
    /// processed, in FIFO order (oldest first). Merging into the in-memory store happens
    /// asynchronously on the next processor tick, so this request's own batch id is typically
    /// included here rather than in <see cref="RecentBatches"/>.
    /// </summary>
    [JsonPropertyName("pendingBatches")]
    public List<string> PendingBatches { get; set; } = new();
}

/// <summary>
/// Result of a tokenomics daily or monthly token balance lookup.
/// </summary>
public sealed class TokenBalanceResponse
{
    /// <summary>User the balance applies to.</summary>
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    /// <summary>Input + output tokens consumed in the queried UTC period.</summary>
    [JsonPropertyName("tokens")]
    public long Tokens { get; set; }
}

/// <summary>
/// Result of a tokenomics daily or monthly budget usage lookup.
/// </summary>
public sealed class BudgetUsageResponse
{
    /// <summary>User the spend applies to.</summary>
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    /// <summary>USD spend in the queried UTC period.</summary>
    [JsonPropertyName("costUsd")]
    public decimal CostUsd { get; set; }
}

/// <summary>
/// Result of a rollup ingest call.
/// </summary>
public sealed class IngestResponse
{
    /// <summary>Number of records merged into the in-memory store.</summary>
    [JsonPropertyName("accepted")]
    public int Accepted { get; set; }

    /// <summary>Number of records rejected, for example when the series limit is reached.</summary>
    [JsonPropertyName("rejected")]
    public int Rejected { get; set; }
}

/// <summary>
/// Aggregated status for a user, a model, or a user/model pair.
/// </summary>
public sealed class StatusResponse
{
    /// <summary>User filter applied to the query, or "*" when unfiltered.</summary>
    [JsonPropertyName("user")]
    public string User { get; set; } = "*";

    /// <summary>Model filter applied to the query, or "*" when unfiltered.</summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = "*";

    /// <summary>Length of the aggregation window, in seconds.</summary>
    [JsonPropertyName("windowSeconds")]
    public int WindowSeconds { get; set; }

    /// <summary>Inclusive start of the aggregation window, as a Unix timestamp in seconds.</summary>
    [JsonPropertyName("windowStart")]
    public long WindowStart { get; set; }

    /// <summary>Exclusive end of the aggregation window, as a Unix timestamp in seconds.</summary>
    [JsonPropertyName("windowEnd")]
    public long WindowEnd { get; set; }

    /// <summary>Number of series that contributed to the aggregate.</summary>
    [JsonPropertyName("seriesCount")]
    public int SeriesCount { get; set; }

    /// <summary>Total requests in the window.</summary>
    [JsonPropertyName("requests")]
    public long Requests { get; set; }

    /// <summary>Total successes in the window.</summary>
    [JsonPropertyName("successes")]
    public long Successes { get; set; }

    /// <summary>Total failures in the window.</summary>
    [JsonPropertyName("failures")]
    public long Failures { get; set; }

    /// <summary>Fraction of requests that succeeded, between 0 and 1.</summary>
    [JsonPropertyName("successRate")]
    public double SuccessRate { get; set; }

    /// <summary>Mean latency across the window, in milliseconds.</summary>
    [JsonPropertyName("averageLatencyMs")]
    public double AverageLatencyMs { get; set; }

    /// <summary>Largest latency observed in the window, in milliseconds.</summary>
    [JsonPropertyName("maxLatencyMs")]
    public long MaxLatencyMs { get; set; }

    /// <summary>Prompt tokens consumed in the window.</summary>
    [JsonPropertyName("promptTokens")]
    public long PromptTokens { get; set; }

    /// <summary>Completion tokens produced in the window.</summary>
    [JsonPropertyName("completionTokens")]
    public long CompletionTokens { get; set; }

    /// <summary>Most recent ingest time for the matched series, as a Unix timestamp in seconds.</summary>
    [JsonPropertyName("lastUpdate")]
    public long LastUpdate { get; set; }

    /// <summary>Rolled up status: unknown, healthy, degraded, or unhealthy.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "unknown";
}

/// <summary>
/// A single retained time bucket.
/// </summary>
public sealed class BucketResponse
{
    /// <summary>Inclusive start of the bucket, as a Unix timestamp in seconds.</summary>
    [JsonPropertyName("start")]
    public long Start { get; set; }

    /// <summary>Requests recorded in the bucket.</summary>
    [JsonPropertyName("requests")]
    public long Requests { get; set; }

    /// <summary>Successes recorded in the bucket.</summary>
    [JsonPropertyName("successes")]
    public long Successes { get; set; }

    /// <summary>Failures recorded in the bucket.</summary>
    [JsonPropertyName("failures")]
    public long Failures { get; set; }

    /// <summary>Mean latency for the bucket, in milliseconds.</summary>
    [JsonPropertyName("averageLatencyMs")]
    public double AverageLatencyMs { get; set; }

    /// <summary>Largest latency observed in the bucket, in milliseconds.</summary>
    [JsonPropertyName("maxLatencyMs")]
    public long MaxLatencyMs { get; set; }

    /// <summary>Prompt tokens recorded in the bucket.</summary>
    [JsonPropertyName("promptTokens")]
    public long PromptTokens { get; set; }

    /// <summary>Completion tokens recorded in the bucket.</summary>
    [JsonPropertyName("completionTokens")]
    public long CompletionTokens { get; set; }
}

/// <summary>
/// Bucketed time series for the requested filters.
/// </summary>
public sealed class SeriesResponse
{
    /// <summary>User filter applied to the query, or "*" when unfiltered.</summary>
    [JsonPropertyName("user")]
    public string User { get; set; } = "*";

    /// <summary>Model filter applied to the query, or "*" when unfiltered.</summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = "*";

    /// <summary>Width of each bucket, in seconds.</summary>
    [JsonPropertyName("bucketSeconds")]
    public int BucketSeconds { get; set; }

    /// <summary>Buckets ordered from oldest to newest.</summary>
    [JsonPropertyName("buckets")]
    public List<BucketResponse> Buckets { get; set; } = new();
}

/// <summary>
/// List of known user or model names.
/// </summary>
public sealed class NamesResponse
{
    /// <summary>Number of returned names.</summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }

    /// <summary>Names, ordered alphabetically.</summary>
    [JsonPropertyName("names")]
    public List<string> Names { get; set; } = new();
}

/// <summary>
/// Server counters describing ingest volume and memory footprint.
/// </summary>
public sealed class StatsResponse
{
    /// <summary>Server version.</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = Constants.VERSION;

    /// <summary>Seconds elapsed since process start.</summary>
    [JsonPropertyName("uptimeSeconds")]
    public long UptimeSeconds { get; set; }

    /// <summary>Number of user/model series currently held in memory.</summary>
    [JsonPropertyName("seriesCount")]
    public int SeriesCount { get; set; }

    /// <summary>Configured maximum number of series.</summary>
    [JsonPropertyName("maxSeries")]
    public int MaxSeries { get; set; }

    /// <summary>Distinct users seen within the retention window.</summary>
    [JsonPropertyName("userCount")]
    public int UserCount { get; set; }

    /// <summary>Distinct models seen within the retention window.</summary>
    [JsonPropertyName("modelCount")]
    public int ModelCount { get; set; }

    /// <summary>Width of each bucket, in seconds.</summary>
    [JsonPropertyName("bucketSeconds")]
    public int BucketSeconds { get; set; }

    /// <summary>Number of buckets retained per series.</summary>
    [JsonPropertyName("bucketCount")]
    public int BucketCount { get; set; }

    /// <summary>Total retention window, in seconds.</summary>
    [JsonPropertyName("retentionSeconds")]
    public int RetentionSeconds { get; set; }

    /// <summary>Rollup records merged since process start.</summary>
    [JsonPropertyName("recordsIngested")]
    public long RecordsIngested { get; set; }

    /// <summary>Rollup records dropped since process start.</summary>
    [JsonPropertyName("recordsDropped")]
    public long RecordsDropped { get; set; }
}

/// <summary>
/// Error payload returned for rejected requests.
/// </summary>
public sealed class ErrorResponse
{
    /// <summary>Human readable error description.</summary>
    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;
}

/// <summary>
/// Source-generated serialization context. Avoids reflection-based JSON on the hot path.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(RollupRecord))]
[JsonSerializable(typeof(RollupRecord[]))]
[JsonSerializable(typeof(RollupBatch))]
[JsonSerializable(typeof(TokenomicsRollupResponse))]
[JsonSerializable(typeof(MetricsServerResponse))]
[JsonSerializable(typeof(TokenBalanceResponse))]
[JsonSerializable(typeof(BudgetUsageResponse))]
[JsonSerializable(typeof(IngestResponse))]
[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(SeriesResponse))]
[JsonSerializable(typeof(NamesResponse))]
[JsonSerializable(typeof(StatsResponse))]
[JsonSerializable(typeof(ErrorResponse))]
public partial class MetricsJsonContext : JsonSerializerContext
{
}
