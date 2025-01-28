
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

    public string GetItemsAsCommaSeparatedString()
    {
        return string.Join(", ", _items.Select(i => $"{i.Priority} "));
    }

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


// public class PriorityQueueItemComparer<T> : IComparer<PriorityQueueItem<T>>
// {
//     public int Compare(PriorityQueueItem<T>? x, PriorityQueueItem<T>? y)
//     {
//         if (ReferenceEquals(x, y)) return 0;
//         if (x == null) return -1;
//         if (y == null) return 1;

//         // Compare Priority first
//         int priorityResult = x.Priority.CompareTo(y.Priority);
//         if (priorityResult != 0) return priorityResult;

//         // If Priority is equal, compare Priority2 (1 is more important)
//         int priority2Result = y.Priority2.CompareTo(x.Priority2);
//         if (priority2Result != 0) return priority2Result;

//         // Finally compare Timestamp
//         return x.Timestamp.CompareTo(y.Timestamp);
//     }
// }

// public class BlockingPriorityQueue<T>
// {
//     // private readonly PriorityQueue<T> _priorityQueue = new PriorityQueue<T>();
//     // private readonly object _lock = new object();
//     // private readonly ManualResetEventSlim _enqueueEvent = new ManualResetEventSlim(false);
//     // private TaskSignaler<T> _taskSignaler = new TaskSignaler<T>();
//     private readonly ConcurrentQueue<PriorityQueueItem<T>> _queue = new ConcurrentQueue<PriorityQueueItem<T>>();
//     private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);

//     public int MaxQueueLength { get; set; }

//     public void StartSignaler(CancellationToken cancellationToken)
//     {
//        // Task.Run(() => SignalWorker(cancellationToken), cancellationToken);
//     }
//     public void Stop()
//     {
//         // Shutdown
//        // _taskSignaler.CancelAllTasks();
//     }

//     // public List<PriorityQueueItem<T>> GetItems() {
//     //     // lock (_lock)
//     //     // {
//     //         return new List<PriorityQueueItem<T>>(_priorityQueue._items);
//     //     //}
//     // }
//     public IEnumerable<PriorityQueueItem<T>> GetItems()
//     {
//         var a =  _queue.ToArray();
//         Console.WriteLine($"Get Items:   Count: {a.Count} , items: {a._items.Count}");
//         return a;
//     }
//     // Thread-safe Count property
//     // public int Count
//     // {
//     //     get
//     //     {
//     //         lock (_lock)
//     //         {
//     //             return _priorityQueue.Count;
//     //         }
//     //     }
//     // }
//     public int Count => _queue.Count;

//     // Call with allowOverflow = true to allow the queue to exceed MaxQueueLength <---> requeue()
//     // public bool EnqueueOrRequeue(T item, int priority, int priority2, DateTime timestamp, bool allowOverflow = false)
//     // {
//     //     lock (_lock)
//     //     {
//     //         if (!allowOverflow && _priorityQueue.Count >= MaxQueueLength)
//     //         {
//     //             return false;
//     //         }
//     //         var queueItem = new PriorityQueueItem<T>(item, priority, priority2, timestamp);
//     //         _priorityQueue.Enqueue(queueItem);
//     //         _enqueueEvent.Set(); // Signal that an item has been added
//     //     }
//     //     return true;
//     // }

//     // Call with allowOverflow = true to allow the queue to exceed MaxQueueLength <---> requeue()
//     public async Task<bool> EnqueueOrRequeueAsync(T item, int priority, int priority2, DateTime timestamp, bool allowOverflow = false)
//     {
//         if (!allowOverflow && _priorityQueue.Count >= MaxQueueLength)
//         {
//             return false;
//         }

//         var queueItem = new PriorityQueueItem<T>(item, priority, priority2, timestamp);
//         _queue.Enqueue(queueItem);
//         _signal.Release();
//         return await Task.FromResult(true);
//     }

//     // public void SignalWorker(CancellationToken cancellationToken)
//     // {
//     //     while (!cancellationToken.IsCancellationRequested)
//     //     {
//     //         _enqueueEvent.Wait(cancellationToken); // Wait for an item to be added
//     //         lock (_lock)
//     //         {
//     //             if (_priorityQueue.Count > 0 && _taskSignaler.HasWaitingTasks())
//     //             {
//     //                 var queueItem = _priorityQueue.Dequeue();
//     //                 if (_priorityQueue.Count == 0)
//     //                 {
//     //                     _enqueueEvent.Reset(); // Reset the event if the queue is empty
//     //                 }

//     //                 // Signal a random task or requeue the item if no tasks are waiting
//     //                 _taskSignaler.SignalRandomTask(queueItem);
//     //             } 
//     //             else {
//     //                 Task.Delay(10).Wait(); // Wait for 10 ms for a Task Worker to be ready
//     //             }
//     //         }
//     //     }
//     //     Console.WriteLine("SignalWorker: Canceled");

//     //     // Shutdown
//     //     _taskSignaler.CancelAllTasks();
//     // }

//     // public async Task<T> DequeueAsync(string id)
//     // {
//     //     try {
//     //         var parameter = await _taskSignaler.WaitForSignalAsync(id);
//     //         return parameter;
//     //     } catch (TaskCanceledException) {
//     //         throw ;
//     //     }
//     // }
//     public async Task<T> DequeueAsync(string taskIdentifier)
//     {
//         await _signal.WaitAsync();
//         if (_queue.TryDequeue(out var item))
//         {
//             Console.WriteLine($"{taskIdentifier} dequeued item: {((testData)(object)item.Item).userId}");
//             return item;
//         }
//         return null;
//     }
    
// }

// public class TaskSignaler<T>
// {
//     private readonly ConcurrentDictionary<string, TaskCompletionSource<T>> _taskCompletionSources = new ConcurrentDictionary<string, TaskCompletionSource<T>>();
//     private readonly Random _random = new Random();

//     public Task<T> WaitForSignalAsync(string taskId)
//     {
//         var tcs = new TaskCompletionSource<T>();
//         _taskCompletionSources[taskId] = tcs;
//         return tcs.Task;
//     }

//     public void SignalTask(string taskId, T parameter)
//     {
//         if (_taskCompletionSources.TryRemove(taskId, out var tcs))
//         {
//             tcs.SetResult(parameter);
//         }
//     }

//     public bool SignalRandomTask(T parameter)
//     {
//         var taskIds = _taskCompletionSources.Keys.ToList();
//         if (taskIds.Count > 0)
//         {
//             var randomTaskId = taskIds[_random.Next(taskIds.Count)];
//             SignalTask(randomTaskId, parameter);

//             return true;
//         }

//         return false;
//     }

//     public void CancelAllTasks()
//     {
//         foreach (var key in _taskCompletionSources.Keys.ToList())
//         {
//             if (_taskCompletionSources.TryRemove(key, out var tcs))
//             {
//                 tcs.TrySetCanceled();
//             }
//         }
//     }

//     public bool HasWaitingTasks()
//     {
//         return !_taskCompletionSources.IsEmpty;
//     }
// }
