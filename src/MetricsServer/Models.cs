using System.Text.Json.Serialization;

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
[JsonSerializable(typeof(IngestResponse))]
[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(SeriesResponse))]
[JsonSerializable(typeof(NamesResponse))]
[JsonSerializable(typeof(StatsResponse))]
[JsonSerializable(typeof(ErrorResponse))]
public partial class MetricsJsonContext : JsonSerializerContext
{
}
