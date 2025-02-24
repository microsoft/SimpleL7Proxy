public class ConcurrentPriQueue<T>
{
    private readonly PriorityQueue<T> _priorityQueue = new PriorityQueue<T>();
    private readonly SemaphoreSlim _enqueueEvent = new SemaphoreSlim(0);
    private readonly object _lock = new object(); // Lock object for synchronization
    private ConcurrentSignal<T> _taskSignaler = new ConcurrentSignal<T>();
    //private int insertions = 0;
    //private int extractions = 0;
  

    public int MaxQueueLength { get; set; }

    public void Stop()
    {
        // Shutdown
        _taskSignaler.CancelAllTasks();
    }

    public void StartSignaler(CancellationToken cancellationToken)
    {
        Task.Run(() => SignalWorker(cancellationToken), cancellationToken);
    }

    // Thread-safe Count property
    public int thrdSafeCount => _priorityQueue.Count;

    public int Count
    {
        get
        {
            return _priorityQueue.Count;
        }
    }
    public List<PriorityQueueItem<T>> getItems()
    {
        return _priorityQueue._items;
    }

    private string enqueue_status = "Not started";

    public bool Enqueue(T item, int priority, int priority2, DateTime timestamp, bool allowOverflow = false)
    {
        var queueItem = new PriorityQueueItem<T>(item, priority, priority2, timestamp);

        if (!allowOverflow && _priorityQueue.Count >= MaxQueueLength)
        {
            return false;
        }

        lock (_lock)
        {
            _priorityQueue.Enqueue(queueItem);
        }

        //Interlocked.Increment(ref insertions);
        _enqueueEvent.Release(); // Signal that an item has been added

        return true;
    }
    public bool Requeue(T item, int priority, int priority2, DateTime timestamp)
    {
        return Enqueue(item, priority, priority2, timestamp, true);
    }

    private string sigwrkr_status = "Not started";
    public async Task SignalWorker(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // 40 seems good,  no timeout or 80ms gives reduced performance
            await _enqueueEvent.WaitAsync(TimeSpan.FromMilliseconds(40), cancellationToken).ConfigureAwait(false); // Wait for an item to be added

            while (_priorityQueue.Count > 0 && _taskSignaler.HasWaitingTasks())
            {
                //Console.WriteLine("SignalWorker: Woke up .. getting task");
                var nextWorker = _taskSignaler.GetNextTask();
                if (nextWorker == null)
                {
                    continue;
                }
                lock (_lock)
                {
                    nextWorker.TaskCompletionSource.SetResult( _priorityQueue.Dequeue(nextWorker.Priority) );                    
                }
            }
        }

        Console.WriteLine("SignalWorker: Canceled");

        // Shutdown
        _taskSignaler.CancelAllTasks();
    }

    public async Task<T> DequeueAsync(int preferredPriority)
    {
        try
        {
            //Console.WriteLine("DequeueAsync: waiting for signal with priority " + preferredPriority);
            var parameter = await _taskSignaler.WaitForSignalAsync(preferredPriority).ConfigureAwait(false);
            return parameter;
        }
        catch (TaskCanceledException)
        {
            throw;
        }
    }

    //public string Counters => $"Ins: {insertions} Ext: {extractions}";
    public string EnqueueStatus => enqueue_status;
    public string SignalWorkerStatus => sigwrkr_status;

}