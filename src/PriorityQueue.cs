using System.Collections.Concurrent;


public class PriorityQueueItem<T>
{
    public T Item { get; }
    public int Priority { get; }
    public DateTime Timestamp { get; }

    public PriorityQueueItem(T item, int priority, DateTime timestamp)
    {
        Item = item;
        Priority = priority;
        Timestamp = timestamp;
    }
}

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

public class BlockingPriorityQueue<T>
{
    private readonly PriorityQueue<T> _priorityQueue = new PriorityQueue<T>();
    private readonly object _lock = new object();
    private readonly ManualResetEventSlim _enqueueEvent = new ManualResetEventSlim(false);

    public PriorityQueueItem<RequestData> NULL_REQUEST = new PriorityQueueItem<RequestData>(null, 0, DateTime.MinValue);

    private TaskSignaler<T> _taskSignaler = new TaskSignaler<T>();

    public int MaxQueueLength { get; set; }

    public void StartSignaler(CancellationToken cancellationToken)
    {
        Task.Run(() => SignalWorker(cancellationToken), cancellationToken);
    }
    public void Stop()
    {
        // Shutdown
        _taskSignaler.CancelAllTasks();
    }

    // Thread-safe Count property
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _priorityQueue.Count;
            }
        }
    }

    public bool Enqueue(T item, int priority, DateTime timestamp)
    {
        lock (_lock)
        {
            //Console.WriteLine($"Count: {_priorityQueue.Count} , Max: {MaxQueueLength}");
            if (_priorityQueue.Count >= MaxQueueLength)
            {
                return false;
            }
            //_priorityQueue.Enqueue(item, priority);
            var queueItem = new PriorityQueueItem<T>(item, priority, timestamp);
            _priorityQueue.Enqueue(queueItem);
            _enqueueEvent.Set(); // Signal that an item has been added

            //Monitor.Pulse(_lock); // Signal that an item has been added
        }

        return true;
    }

    public bool Requeue(T item, int priority, DateTime timestamp)
    {
        lock (_lock)
        {
            var queueItem = new PriorityQueueItem<T>(item, priority, timestamp);
            _priorityQueue.Enqueue(queueItem);
            _enqueueEvent.Set(); // Signal that an item has been added

            //Monitor.Pulse(_lock); // Signal that an item has been added
        }

        return true;
    }

    public async Task SignalWorker(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            _enqueueEvent.Wait(cancellationToken); // Wait for an item to be added
            lock (_lock)
            {
                if (_priorityQueue.Count > 0)
                {
                    var queueItem = _priorityQueue.Dequeue();
                    if (_priorityQueue.Count == 0)
                    {
                        _enqueueEvent.Reset(); // Reset the event if the queue is empty
                    }

                    _taskSignaler.SignalRandomTask(queueItem);
                }
            }
        }
        Console.WriteLine("SignalWorker: Canceled");

        // Shutdown
        _taskSignaler.CancelAllTasks();
    }

    public async Task<T> Dequeue(CancellationToken cancellationToken, string id)
    {
        try {
            var parameter = await _taskSignaler.WaitForSignalAsync(id);
            return parameter;
        } catch (TaskCanceledException) {
            throw ;
        }
    }
    
}

public class TaskSignaler<T>
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<T>> _taskCompletionSources = new ConcurrentDictionary<string, TaskCompletionSource<T>>();
    private readonly Random _random = new Random();

    public Task<T> WaitForSignalAsync(string taskId)
    {
        var tcs = new TaskCompletionSource<T>();
        _taskCompletionSources[taskId] = tcs;
        return tcs.Task;
    }

    public void SignalTask(string taskId, T parameter)
    {
        if (_taskCompletionSources.TryRemove(taskId, out var tcs))
        {
            tcs.SetResult(parameter);
        }
    }

    public void SignalRandomTask(T parameter)
    {
        var taskIds = _taskCompletionSources.Keys.ToList();
        if (taskIds.Count > 0)
        {
            var randomTaskId = taskIds[_random.Next(taskIds.Count)];
            SignalTask(randomTaskId, parameter);
        }
    }

    public void CancelAllTasks()
    {
        foreach (var key in _taskCompletionSources.Keys.ToList())
        {
            if (_taskCompletionSources.TryRemove(key, out var tcs))
            {
                tcs.TrySetCanceled();
            }
        }
    }
}
