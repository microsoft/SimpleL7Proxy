using Azure.Messaging.ServiceBus;
using Azure.Identity;


var serviceBusConnectionString = Environment.GetEnvironmentVariable("SERVICEBUS_CONNECTIONSTRING");
var serviceBusTopicName = Environment.GetEnvironmentVariable("SERVICEBUS_TOPICNAME");
var serviceBusSubscriptionName = Environment.GetEnvironmentVariable("SERVICEBUS_SUBSCRIPTIONNAME");
var serviceBusNamespace = Environment.GetEnvironmentVariable("SERVICEBUS_NAMESPACE");

// check for Managed Identity usage:
var useManagedIdentity = Environment.GetEnvironmentVariable("USE_MI")?.ToLower() == "true";

ServiceBusClient client;

if (useManagedIdentity)
{
    if (string.IsNullOrEmpty(serviceBusNamespace) ||
        string.IsNullOrEmpty(serviceBusTopicName) ||
        string.IsNullOrEmpty(serviceBusSubscriptionName))
    {
        Console.WriteLine("Please set the SERVICEBUS_NAMESPACE, SERVICEBUS_TOPICNAME, and SERVICEBUS_SUBSCRIPTIONNAME environment variables when using managed identity.");
        return;
    }

    Console.WriteLine("Using Managed Identity authentication for Service Bus");
    var credential = new DefaultAzureCredential();
    client = new ServiceBusClient(serviceBusNamespace, credential);
}
else
{
    if (string.IsNullOrEmpty(serviceBusConnectionString) ||
        string.IsNullOrEmpty(serviceBusTopicName) ||
        string.IsNullOrEmpty(serviceBusSubscriptionName))
    {
        Console.WriteLine("Please set the SERVICEBUS_CONNECTIONSTRING, SERVICEBUS_TOPICNAME, and SERVICEBUS_SUBSCRIPTIONNAME environment variables.");
        return;
    }

    Console.WriteLine("Using connection string authentication for Service Bus");
    client = new ServiceBusClient(serviceBusConnectionString);
}
var processor = client.CreateProcessor(serviceBusTopicName,serviceBusSubscriptionName );

processor.ProcessMessageAsync += MessageHandler;
processor.ProcessErrorAsync += ErrorHandler;

await processor.StartProcessingAsync();

Console.WriteLine("Press any key to stop the processor...");
Console.ReadKey(); // Keeps the program running until a key is pressed

await processor.StopProcessingAsync();
await processor.DisposeAsync();
await client.DisposeAsync();

async Task MessageHandler(ProcessMessageEventArgs args)
{
    var message = args.Message;
    var jobStatus = message.Body.ToString();
    Console.WriteLine($"{jobStatus}");
    await args.CompleteMessageAsync(message);
}

async Task ErrorHandler(ProcessErrorEventArgs args)
{
    //Console.WriteLine($"Error occurred: {args.Exception}");
}