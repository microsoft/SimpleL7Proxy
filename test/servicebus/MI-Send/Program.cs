// sends a sample message into the Service Bus queue for testing purposes
using System;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.Messaging.ServiceBus;


var serviceBusNamespace = "https://nvmtrsbsrvr.servicebus.windows.net/";
var queueName = "requeststatus";

var clientOptions = new ServiceBusClientOptions
{
    TransportType = ServiceBusTransportType.AmqpWebSockets
};

var client = new ServiceBusClient(serviceBusNamespace, new DefaultAzureCredential(), clientOptions);
var sender = client.CreateSender(queueName);



while (true)
{

    // Guid guid = Guid.NewGuid();
    var guid = "36aad2d5-41b4-476a-ab1f-b6d3a2568211";

    var text = @"{""createdAt"":""2025-09-10T18:35:50.5064794Z"",""guid"":""" + 
        guid.ToString() + @""",""id"":""" + 
        guid.ToString() + @""",""isAsync"":true,""priority1"":1,""priority2"":0,""status"":""Completed"",""userID"":""123456""}";
    var message = new ServiceBusMessage(text);


    await sender.SendMessageAsync(message);
    Console.WriteLine($"Sent message to Service Bus queue '{queueName}' in namespace '{serviceBusNamespace}'.");
    Console.WriteLine("Message sent successfully. Press E key to exit, any other to repeat.");
    var key = Console.ReadKey();
    if (key.Key == ConsoleKey.E)
    {
        break;
    }
}

await sender.DisposeAsync();
await client.DisposeAsync();
