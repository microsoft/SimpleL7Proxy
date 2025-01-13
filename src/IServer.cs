using SimpleL7Proxy.Queue;

public interface IServer
{
    Task Run();
    void Start(CancellationToken cancellationToken);
}