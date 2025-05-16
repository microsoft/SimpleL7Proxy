using SimpleL7Proxy;

namespace SimpleL7Proxy.ServiceBus
{

    public interface IServiceBusRequestService
    {
        Task StopAsync(CancellationToken cancellationToken);
        bool IsRunning { get; }
        bool updateStatus(RequestData message);
    }
}