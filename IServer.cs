
using System.Collections.Concurrent;

public interface IServer
{
    Task Run();
    //BlockingCollection<RequestData> Start(CancellationToken cancellationToken);
    BlockingPriorityQueue<RequestData> Start(CancellationToken cancellationToken);
}