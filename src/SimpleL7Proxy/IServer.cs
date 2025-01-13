
using System.Collections.Concurrent;

public interface IServer
{
    Task Run();
    BlockingPriorityQueue<RequestData> Start(CancellationToken cancellationToken);
    BlockingPriorityQueue<RequestData> Queue();
}