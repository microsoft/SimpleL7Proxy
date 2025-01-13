namespace SimpleL7Proxy.Queue;

public class PriorityQueueItem<T>(T item, int priority, DateTime timestamp)
{
    public T Item { get; } = item;
    public int Priority { get; } = priority;
    public DateTime Timestamp { get; } = timestamp;
}
