namespace SimpleL7Proxy.Queue;

public interface IBlockingPriorityQueue<T>
{
  int MaxQueueLength { get; }
  void StartSignaler(CancellationToken cancellationToken);
  void Stop();
  int Count { get; }
  bool Enqueue(T item, int priority, DateTime timestamp);
  bool Requeue(T item, int priority, DateTime timestamp);
  Task<T> Dequeue(CancellationToken cancellationToken, string id);
}
