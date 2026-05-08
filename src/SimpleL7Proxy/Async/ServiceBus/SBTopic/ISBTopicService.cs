using SimpleL7Proxy;

namespace SimpleL7Proxy.Async.ServiceBus
{

    public interface ISBTopicService
    {
        Task StopAsync(CancellationToken cancellationToken);
        bool updateStatus(RequestData message);
        (int totalMessages, int totalBatches, int queueDepth, bool isEnabled, string? connectionInfo) GetStatistics();
    }
}