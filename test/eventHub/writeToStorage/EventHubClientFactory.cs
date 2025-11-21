using Azure.Identity;
using Azure.Messaging.EventHubs.Consumer;

namespace EventHubToStorage;

/// <summary>
/// Factory for creating Event Hub clients with managed identity
/// </summary>
public static class EventHubClientFactory
{
    /// <summary>
    /// Creates an EventHubConsumerClient using managed identity authentication
    /// </summary>
    public static EventHubConsumerClient CreateConsumerClient(string eventHubName, string eventHubNamespace, string consumerGroup)
    {
        try
        {
            var credential = new DefaultAzureCredential();
            var consumerClient = new EventHubConsumerClient(consumerGroup, eventHubNamespace, eventHubName, credential);
            Console.WriteLine("[INIT] ✓ EventHubConsumerClient created successfully.");
            return consumerClient;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to create EventHubConsumerClient: {ex.Message}");
            throw;
        }
    }
}
