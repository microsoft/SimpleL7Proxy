namespace SimpleL7Proxy;

public interface IRequestsConsumerService
{
    Task StopAsync(CancellationToken cancellationToken);
}