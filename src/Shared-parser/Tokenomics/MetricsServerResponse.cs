using System.Text.Json.Serialization;

namespace SimpleL7Proxy.Tokenomics;

/// <summary>
/// Shape of the MetricsServer's tokenomics upload response, used to acknowledge processed
/// batches and observe replica-side batch state.
/// </summary>
public sealed class MetricsServerResponse
{
    /// <summary>Batch id echoed back for the request that produced this response, or null when omitted.</summary>
    [JsonPropertyName("BatchId")]
    public string? BatchId { get; set; }

    /// <summary>
    /// Batch ids for the sending replica that the MetricsServer has received but not yet
    /// processed, in FIFO order (oldest first).
    /// </summary>
    [JsonPropertyName("PendingBatches")]
    public List<string>? PendingBatches { get; set; }

    /// <summary>
    /// Most recently processed batch ids for the sending replica, newest first, capped at 10.
    /// Batch ids reported here are acknowledged (removed) from this collector's tracking.
    /// </summary>
    [JsonPropertyName("ProcessedBatches")]
    public List<string>? ProcessedBatches { get; set; }
}