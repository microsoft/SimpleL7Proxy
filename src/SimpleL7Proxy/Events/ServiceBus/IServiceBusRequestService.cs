using SimpleL7Proxy;

namespace SimpleL7Proxy.ServiceBus
{

    public interface IServiceBusRequestService
    {
        Task StopAsync(CancellationToken cancellationToken);
        bool updateStatus(RequestData message);
    }
}