
using System.Collections.Concurrent;

public interface IServer
{
    Task Run();
    ConcurrentPriQueue<RequestData> Start(CancellationToken cancellationToken);
    ConcurrentPriQueue<RequestData> Queue();
}