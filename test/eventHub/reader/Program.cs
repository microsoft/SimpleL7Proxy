// Read events from an Event Hub.  Define these three environment variables
// 1. EVENTHUB_CONNECTIONSTRING
// 2. EVENTHUB_NAME
// 3. EVENTHUB_CONSUMER_GROUP

using System;
using System.Text;
using System.Threading.Tasks;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;

// https://docs.microsoft.com/en-us/azure/event-hubs/event-hubs-dotnet-standard-getstarted-receive-eph

//Read the environment variables
string? connectionString = Environment.GetEnvironmentVariable("EVENTHUB_CONNECTIONSTRING");
string? eventHubName = Environment.GetEnvironmentVariable("EVENTHUB_NAME");
string? consumerGroup = Environment.GetEnvironmentVariable("EVENTHUB_CONSUMER_GROUP");

if (string.IsNullOrEmpty(connectionString) || string.IsNullOrEmpty(eventHubName) || string.IsNullOrEmpty(consumerGroup))
{
    Console.WriteLine("Please set the environment variables EVENTHUB_CONNECTIONSTRING, EVENTHUB_NAME and EVENTHUB_CONSUMER_GROUP.");
    return;
}

// Create a consumer client for the event hub.

// Read events from the event hub.
Console.WriteLine("Reading events...");

var consumerClient = new EventHubConsumerClient(consumerGroup, connectionString, eventHubName);

var partitionIds = await consumerClient.GetPartitionIdsAsync();


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