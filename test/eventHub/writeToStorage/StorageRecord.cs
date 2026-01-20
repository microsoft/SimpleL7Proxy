using System.Text.Json;
using System.Text.Json.Serialization;

namespace EventHubToStorage;

/// <summary>
/// Record structure for storing Event Hub data optimized for Azure Data Explorer ingestion
/// </summary>
public class StorageRecord
{
    /// <summary>
    /// Timestamp when the record was generated (ADX standard field)
    /// </summary>
    [JsonPropertyName("TimeGenerated")]
    public DateTimeOffset TimeGenerated { get; set; }

    /// <summary>
    /// Cloud role instance identifier (ADX standard field for distributed systems)
    /// </summary>
    [JsonPropertyName("CloudRoleInstance")]
    public string CloudRoleInstance { get; set; } = string.Empty;

    /// <summary>
    /// Event Hub partition ID from which this event originated
    /// </summary>
    [JsonPropertyName("PartitionId")]
    public string PartitionId { get; set; } = string.Empty;

    /// <summary>
    /// Event Hub sequence number for ordering and deduplication
    /// </summary>
    [JsonPropertyName("SequenceNumber")]
    public long SequenceNumber { get; set; }

    /// <summary>
    /// Time when the event was enqueued in Event Hub
    /// </summary>
    [JsonPropertyName("EnqueuedTime")]
    public DateTimeOffset EnqueuedTime { get; set; }

    /// <summary>
    /// Event body/payload
    /// </summary>
    [JsonPropertyName("EventBody")]
    public object EventBody { get; set; } = new();
    /// <summary>
    /// Creates a StorageRecord from EventData
    /// </summary>
    public static StorageRecord FromEventData(EventData eventData, string cloudRoleInstance)
    {
        // Parse EventBody as JSON object if possible, otherwise keep as string
        object parsedBody;
        try
        {
            parsedBody = JsonSerializer.Deserialize<object>(eventData.Body) ?? eventData.Body;
        }
        catch
        {
            parsedBody = eventData.Body;
        }

        return new StorageRecord
        {
            TimeGenerated = DateTimeOffset.UtcNow,
            CloudRoleInstance = cloudRoleInstance,
            PartitionId = eventData.PartitionId,
            SequenceNumber = eventData.SequenceNumber,
            EnqueuedTime = eventData.EnqueuedTime,
            EventBody = parsedBody
        };
    }

    /// <summary>
    /// Creates a batch of StorageRecords from a collection of EventData
    /// </summary>
    public static List<StorageRecord> FromEventDataBatch(IEnumerable<EventData> events, string cloudRoleInstance)
    {
        return events.Select(e => FromEventData(e, cloudRoleInstance)).ToList();
    }
}
