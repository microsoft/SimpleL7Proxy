public class PriorityQueueItem<T> : IComparable<PriorityQueueItem<T>>
{
    public T Item { get; }
    private int Priority { get; set; }
    private int Priority2 { get; }
    private bool ignorePriority2 { get; set; }

    private DateTime Timestamp { get; }

    public PriorityQueueItem(T item, int priority, int priority2, DateTime timestamp)
    {
        Item = item;
        Priority = priority;
        Priority2 = priority2;
        Timestamp = timestamp;
        ignorePriority2 = false;
    }

    public void UpdateForLookup(int priority)
    {
        Priority = priority;
        ignorePriority2 = true;
    }

    public int CompareTo(PriorityQueueItem<T>? other)
    {
        if (ReferenceEquals(this, other)) return 0;
        if (other == null) return 1;

        // Compare Priority first
        int priorityResult = Priority.CompareTo(other.Priority);
        if (priorityResult != 0) return priorityResult;

        if (ignorePriority2 || other.ignorePriority2)
        {
            return Timestamp.CompareTo(other.Timestamp);
        }

        // If Priority is equal, compare Priority2 (1 is more important)
        int priority2Result = other.Priority2.CompareTo(Priority2);
        if (priority2Result != 0) return priority2Result;

        // Finally compare Timestamp
        return Timestamp.CompareTo(other.Timestamp);
    }
}

