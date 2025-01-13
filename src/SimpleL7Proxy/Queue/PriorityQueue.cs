namespace SimpleL7Proxy.Queue;

public class PriorityQueue<T>
{
    private readonly List<PriorityQueueItem<T>> _items = [];
    private static readonly PriorityQueueItemComparer<T> Comparer = new();

    public int Count => _items.Count;

    public void Enqueue(PriorityQueueItem<T> queueItem)
    {
        // inserting into a sorted list is best with binary search:  O(n)
        int index = _items.BinarySearch(queueItem, Comparer);
        if (index < 0) index = ~index; // If not found, BinarySearch returns the bitwise complement of the index of the next element that is larger.
        _items.Insert(index, queueItem);
    }

    public string GetItemsAsCommaSeparatedString()
        => string.Join(", ", _items.Select(i => $"{i.Priority} "));

    public T Dequeue()
    {
        if (_items.Count == 0)
            throw new InvalidOperationException("The queue is empty.");

        var item = _items[0]; // Get the first item
        _items.RemoveAt(0); // Remove the first item

        return item.Item;
    }
}
