using EventHubToStorage;
using Microsoft.Extensions.Logging.Abstractions;

// Load configuration from environment variables
var config = EventHubConfiguration.LoadFromEnvironment();

// Create clients with managed identity
var consumerClient = EventHubClientFactory.CreateConsumerClient(
    config.EventHubName, 
    config.EventHubNamespace, 
    config.ConsumerGroup);

var blobWriter = BlobWriter.CreateWithManagedIdentity(
    config.BlobStorageAccountUri, 
    NullLogger.Instance);

// Create and start the event processor
var processor = new EventProcessor(consumerClient, blobWriter, NullLogger.Instance);
await processor.ProcessEventsAsync();

