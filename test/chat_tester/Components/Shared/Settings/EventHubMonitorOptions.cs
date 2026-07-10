namespace chat_tester.Components.Shared;

/// <summary>
/// Strongly typed Event Hub monitor settings, bound from the <c>EventHubMonitor</c>
/// configuration section. These are shared/server-wide values and are configured only in
/// appsettings.json (no UI). Property initializers act as the defaults when a key is absent.
/// </summary>
public sealed class EventHubMonitorOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "EventHubMonitor";

    /// <summary>Event Hub namespace connection string used by the server-side reader.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Event Hub (topic) name to consume.</summary>
    public string EventHubName { get; set; } = string.Empty;

    /// <summary>Optional Event Hub namespace used with managed identity authentication.</summary>
    public string EventHubNamespace { get; set; } = string.Empty;

    /// <summary>Consumer group to read from.</summary>
    public string ConsumerGroup { get; set; } = "$Default";

    /// <summary>Optional blob storage connection string for checkpointing.</summary>
    public string CheckpointStorage { get; set; } = string.Empty;

    /// <summary>Start position for a new consumer: "latest" or "earliest".</summary>
    public string StartPosition { get; set; } = "latest";

    /// <summary>How often the UI pulls a fresh snapshot, in seconds.</summary>
    public int RefreshSeconds { get; set; } = 5;
}
