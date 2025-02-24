
using System.Data.Common;

public class PriorityQueue<T>
{
    public readonly List<PriorityQueueItem<T>> _items = new List<PriorityQueueItem<T>>();
    //private static readonly PriorityQueueItemComparer<T> Comparer = new PriorityQueueItemComparer<T>();

    //public int Count => _items.Count;
    public int Count => itemCounter;

    public static int itemCounter =0; 

    public void Enqueue(PriorityQueueItem<T> queueItem)
    {
        int index = _items.BinarySearch(queueItem);//, Comparer);
        if (index < 0) index = ~index; // If not found, BinarySearch returns the bitwise complement of the index of the next element that is larger.
        _items.Insert(index, queueItem);

        Interlocked.Increment(ref itemCounter);
        //Console.WriteLine($"Enqueue:  Priority: {queueItem.Priority}-{queueItem.Priority2}   length: {_items.Count}  index: {index} : {GetItemsAsCommaSeparatedString()}");
    }

    // public string GetItemsAsCommaSeparatedString()
    // {
    //     return string.Join(", ", _items.Select(i => $"{i.Priority} "));
    // }

    static PriorityQueueItem<T> refItem = new PriorityQueueItem<T>(default!, 0, 1, DateTime.MinValue );
    public T Dequeue(int priority)
    {
        if (_items.Count == 0)
            throw new InvalidOperationException("The queue is empty.");

        int index=0;

        if (priority != Constants.AnyPriority)
        {
            // Use binary search to find the smallest item with the specified priority
            // This is an item only used for searching in the list.  It's okey if item is null.
            refItem.UpdateForLookup( priority ); // Important, only set this value for searching.
            index = _items.BinarySearch(refItem);

            if (index < 0)
            {
                // If no exact match is found, BinarySearch returns a negative number that is the bitwise complement of the index of the next element that is larger.
                index = ~index; // If not found, BinarySearch returns the bitwise complement of the index of the next element that is larger.
                if (index >= _items.Count)
                {
                    index = _items.Count - 1;
                }
            } 
        }

        // Get the item at the found index
        var item = _items[index];

        // Remove the item from the list
        _items.RemoveAt(index);

        Interlocked.Decrement(ref itemCounter);

        return item.Item;
    }
}