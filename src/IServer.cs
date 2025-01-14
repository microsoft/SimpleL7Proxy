
using System.Collections.Concurrent;

public interface IServer
{
    Task Run();
    //BlockingCollection<RequestData> Start(CancellationToken cancellationToken);
    ConcurrentPriQueue<RequestData> Start(CancellationToken cancellationToken);
    ConcurrentPriQueue<RequestData> Queue();
}