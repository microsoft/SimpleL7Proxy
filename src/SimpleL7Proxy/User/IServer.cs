namespace SimpleL7Proxy;

public interface IServer
{
    Task StopAsync(CancellationToken cancellationToken);
}