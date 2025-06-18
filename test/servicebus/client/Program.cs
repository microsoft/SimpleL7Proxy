using Azure.Messaging.ServiceBus;

var serviceBusConnectionString = Environment.GetEnvironmentVariable("SERVICEBUS_CONNECTIONSTRING");
var serviceBusTopicName = Environment.GetEnvironmentVariable("SERVICEBUS_TOPICNAME");
var serviceBusSubscriptionName = Environment.GetEnvironmentVariable("SERVICEBUS_SUBSCRIPTIONNAME");

if (string.IsNullOrEmpty(serviceBusConnectionString) ||
    string.IsNullOrEmpty(serviceBusTopicName) ||
    string.IsNullOrEmpty(serviceBusSubscriptionName))
{
    Console.WriteLine("Please set the SERVICEBUS_CONNECTIONSTRING, SERVICEBUS_TOPICNAME, and SERVICEBUS_SUBSCRIPTIONNAME environment variables.");
    return;
}

var client = new ServiceBusClient(serviceBusConnectionString);
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
    Console.WriteLine($"Received job status: {jobStatus}");
    await args.CompleteMessageAsync(message);
}

async Task ErrorHandler(ProcessErrorEventArgs args)
{
    //Console.WriteLine($"Error occurred: {args.Exception}");
}