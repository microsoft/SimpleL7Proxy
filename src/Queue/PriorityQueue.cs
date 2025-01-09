using System.Collections.Concurrent;

namespace SimpleL7Proxy.Queue;

public class PriorityQueue<T>
{
    private readonly List<PriorityQueueItem<T>> _items = new List<PriorityQueueItem<T>>();
    private static readonly PriorityQueueItemComparer<T> Comparer = new PriorityQueueItemComparer<T>();

    public int Count => _items.Count;

    public void Enqueue(PriorityQueueItem<T> queueItem)
    {
        //var queueItem = new PriorityQueueItem<T>(item, priority);

        // inserting into a sorted list is best with binary search:  O(n)
        int index = _items.BinarySearch(queueItem, Comparer);
        if (index < 0) index = ~index; // If not found, BinarySearch returns the bitwise complement of the index of the next element that is larger.
        _items.Insert(index, queueItem);

        //Console.WriteLine($"Enqueue:  Priority: {priority}  length: {_items.Count}  index: {index} : {GetItemsAsCommaSeparatedString()}");
    }

    public string GetItemsAsCommaSeparatedString()
    {
        return string.Join(", ", _items.Select(i => $"{i.Priority} "));
    }

    public T Dequeue()
    {
        if (_items.Count == 0)
            throw new InvalidOperationException("The queue is empty.");

        // var item = _items[_items.Count - 1]; // Get the last item
        // _items.RemoveAt(_items.Count - 1); // Remove the last item
        var item = _items[0]; // Get the first item
        _items.RemoveAt(0); // Remove the first item

        return item.Item;
    }
}

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
        if (priorityComparison == 0)
        {
            // If priorities are equal, sort by timestamp (older items are "bigger")
            //return y.Timestamp.CompareTo(x.Timestamp);
            return x.Timestamp.CompareTo(y.Timestamp);
        }
        return priorityComparison;
    }
}


