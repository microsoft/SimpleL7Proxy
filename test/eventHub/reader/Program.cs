// Read events from an Event Hub.  Define these three environment variables
// 1. EVENTHUB_CONNECTIONSTRING
// 2. EVENTHUB_NAME
// 3. EVENTHUB_CONSUMER_GROUP

using System;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;

// https://docs.microsoft.com/en-us/azure/event-hubs/event-hubs-dotnet-standard-getstarted-receive-eph

bool summaryMode = args.Contains("-s");

//Read the environment variables
string? connectionString = Environment.GetEnvironmentVariable("EVENTHUB_CONNECTIONSTRING");
string? eventHubName = Environment.GetEnvironmentVariable("EVENTHUB_NAME");
string? consumerGroup = Environment.GetEnvironmentVariable("EVENTHUB_CONSUMER_GROUP");
string? eventHubNamespace = Environment.GetEnvironmentVariable("EVENTHUB_NAMESPACE");

// Default consumer group if not specified
if (string.IsNullOrEmpty(consumerGroup))
{
    consumerGroup = EventHubConsumerClient.DefaultConsumerGroupName;
    Console.WriteLine($"Using default consumer group: {consumerGroup}");
}

// Ensure namespace is fully qualified domain name
if (!string.IsNullOrEmpty(eventHubNamespace) && !eventHubNamespace.Contains("."))
{
    eventHubNamespace = $"{eventHubNamespace}.servicebus.windows.net";
    Console.WriteLine($"Using fully qualified namespace: {eventHubNamespace}");
}

// either provide connection string and event hub name  or   the Event Hub namespace and event hub name
if ( string.IsNullOrEmpty(connectionString) &&
    ( string.IsNullOrEmpty(eventHubNamespace) || string.IsNullOrEmpty(eventHubName) ) )
{
    Console.WriteLine("EVENTHUB_CONNECTIONSTRING is not set and either EVENTHUB_NAMESPACE or EVENTHUB_NAME is not set.");
    return;
}

if (!string.IsNullOrEmpty(connectionString) &&
    ( string.IsNullOrEmpty(eventHubName) || string.IsNullOrEmpty(consumerGroup) ) )
{
    Console.WriteLine(" EVENTHUB_CONNECTIONSTRING is set, but either EVENTHUB_NAME or EVENTHUB_CONSUMER_GROUP is not set.");
    return;
}

// Create a consumer client for the event hub.

// Read events from the event hub.
Console.WriteLine("Reading events...");
EventHubConsumerClient? consumerClient = null;

// Configure client options with appropriate timeouts
var clientOptions = new EventHubConsumerClientOptions
{
    ConnectionOptions = new EventHubConnectionOptions
    {
        TransportType = EventHubsTransportType.AmqpTcp
    },
    RetryOptions = new EventHubsRetryOptions
    {
        MaximumRetries = 3,
        TryTimeout = TimeSpan.FromSeconds(60),
        Delay = TimeSpan.FromMilliseconds(800),
        MaximumDelay = TimeSpan.FromSeconds(10),
        Mode = EventHubsRetryMode.Exponential
    }
};

try {
    if (!string.IsNullOrEmpty(connectionString))
        consumerClient = new EventHubConsumerClient(consumerGroup, connectionString, eventHubName, clientOptions);
    else
        consumerClient = new EventHubConsumerClient(consumerGroup, eventHubNamespace, eventHubName, new Azure.Identity.DefaultAzureCredential(), clientOptions);
} 
catch (Exception ex) {
    Console.WriteLine($"Error creating EventHubConsumerClient: {ex.Message}");
    return;
}


var partitionIds = await consumerClient.GetPartitionIdsAsync();

if (summaryMode)
{
    var typeCounts = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var cts = new CancellationTokenSource();

    // Timer that prints summary every 10 seconds
    var timerTask = Task.Run(async () =>
    {
        while (!cts.Token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cts.Token).ConfigureAwait(false);
            var snapshot = typeCounts.ToArray();
            if (snapshot.Length == 0)
            {
                continue;
            }

            Console.WriteLine($"\n--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");
            foreach (var kvp in snapshot.OrderBy(k => k.Key))
            {
                Console.WriteLine($"  {kvp.Key}: {kvp.Value}");
            }

            // Reset counts for next interval
            typeCounts.Clear();
        }
    }, cts.Token);

    // Read events across all partitions
    try
    {
        await foreach (PartitionEvent partitionEvent in consumerClient.ReadEventsAsync(cts.Token))
        {
            var eventBody = Encoding.UTF8.GetString(partitionEvent.Data.Body.ToArray());
            try
            {
                using var doc = JsonDocument.Parse(eventBody);
                var typeValue = "(unknown)";
                if (doc.RootElement.TryGetProperty("Type", out var typeProp))
                {
                    typeValue = typeProp.GetString() ?? "(null)";
                }
                typeCounts.AddOrUpdate(typeValue, 1, (_, count) => count + 1);
            }
            catch (JsonException)
            {
                typeCounts.AddOrUpdate("(parse-error)", 1, (_, count) => count + 1);
            }
        }
    }
    catch (OperationCanceledException) { }

    cts.Cancel();
}
else
{
    while (true)
    {
        foreach (var partitionId in partitionIds)
        {
            await foreach (PartitionEvent partitionEvent in consumerClient.ReadEventsFromPartitionAsync(
                partitionId,
                EventPosition.Latest))
            {
                var eventBody = Encoding.UTF8.GetString(partitionEvent.Data.Body.ToArray());
                Console.WriteLine(eventBody);
            }
        }
    }
}