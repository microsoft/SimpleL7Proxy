namespace SimpleL7Proxy.Queue;
public interface IConcurrentPriQueue<T>
{
    int MaxQueueLength { get; }

    public Task StopAsync();

    public void StartSignaler(CancellationToken cancellationToken);

    // Thread-safe Count property
    public int thrdSafeCount { get; }
    
    public bool Enqueue(T item, int priority, int priority2, DateTime timestamp, bool allowOverflow = false);

    public bool Requeue(T item, int priority, int priority2, DateTime timestamp);
    public Task SignalWorker(CancellationToken cancellationToken);
    public Task<T> DequeueAsync(int preferredPriority);
    
}