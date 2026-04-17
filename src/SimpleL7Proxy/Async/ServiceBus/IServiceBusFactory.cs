using Azure.Messaging.ServiceBus;

namespace SimpleL7Proxy.Async.ServiceBus
{
    public interface IServiceBusFactory
    {
        string InitStatus { get; set; }
        ServiceBusSender GetSender(string topicName);
        ServiceBusSender GetQueueSender(string queueName);
        ServiceBusProcessor GetQueueProcessor(string queueName, ServiceBusProcessorOptions? options = null);
        string? GetConnectionInfo();
    }
}
