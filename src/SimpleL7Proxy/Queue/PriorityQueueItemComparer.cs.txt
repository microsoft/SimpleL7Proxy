namespace SimpleL7Proxy.Queue;

public class PriorityQueueItemComparer<T> : IComparer<PriorityQueueItem<T>>
{
    public int Compare(PriorityQueueItem<T>? x, PriorityQueueItem<T>? y)
    {
        if (x == null)
        {
            return y == null ? 0 : -1; // If x is null and y is not, x is considered smaller
        }
        if (y == null)
        {
            return 1; // If y is null and x is not, x is considered larger
        }

        int priorityComparison = x.Priority.CompareTo(y.Priority);
        return priorityComparison == 0
            ? x.Timestamp.CompareTo(y.Timestamp)
            : priorityComparison;
    }
}


