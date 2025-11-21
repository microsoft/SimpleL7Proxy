namespace EventHubToStorage;

/// <summary>
/// Configuration for Event Hub and Blob Storage connection
/// </summary>
public class EventHubConfiguration
{
    public string EventHubName { get; init; }
    public string EventHubNamespace { get; init; }
    public string ConsumerGroup { get; init; }
    public string BlobStorageAccountUri { get; init; }

    private EventHubConfiguration(string eventHubName, string eventHubNamespace, string consumerGroup, string blobStorageAccountUri)
    {
        EventHubName = eventHubName;
        EventHubNamespace = eventHubNamespace;
        ConsumerGroup = consumerGroup;
        BlobStorageAccountUri = blobStorageAccountUri;
    }

    /// <summary>
    /// Loads configuration from environment variables
    /// </summary>
    public static EventHubConfiguration LoadFromEnvironment()
    {
        var eventHubName = Environment.GetEnvironmentVariable("EVENTHUB_NAME");
        var eventHubNamespace = Environment.GetEnvironmentVariable("EVENTHUB_NAMESPACE");
        var consumerGroup = Environment.GetEnvironmentVariable("EVENTHUB_CONSUMER_GROUP");
        var blobStorageAccountUri = Environment.GetEnvironmentVariable("BLOBSTORAGE_ACCOUNT_URI");

        ValidateConfiguration(eventHubName, eventHubNamespace, consumerGroup, blobStorageAccountUri);

        return new EventHubConfiguration(eventHubName!, eventHubNamespace!, consumerGroup!, blobStorageAccountUri!);
    }

    private static void ValidateConfiguration(string? eventHubName, string? eventHubNamespace, string? consumerGroup, string? blobStorageAccountUri)
    {
        if (string.IsNullOrEmpty(eventHubName) || string.IsNullOrEmpty(eventHubNamespace) || string.IsNullOrEmpty(consumerGroup))
        {
            Console.WriteLine("Please set the environment variables EVENTHUB_NAME, EVENTHUB_NAMESPACE, and EVENTHUB_CONSUMER_GROUP.");
            Environment.Exit(1);
        }

        if (string.IsNullOrEmpty(blobStorageAccountUri) || !blobStorageAccountUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Please set the environment variable BLOBSTORAGE_ACCOUNT_URI with a valid Blob Storage account URI.");
            Environment.Exit(1);
        }
    }
}
