public class ConcurrentPriQueue<T>
{
    private readonly PriorityQueue<T> _priorityQueue = new PriorityQueue<T>();
    private readonly SemaphoreSlim _enqueueEvent = new SemaphoreSlim(0);
    private readonly object _lock = new object(); // Lock object for synchronization
    private ConcurrentSignal<T> _taskSignaler = new ConcurrentSignal<T>();
    private int insertions = 0;
    private int extractions = 0;

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
            lock (_lock)
            {
                return _priorityQueue.Count;
            }
        }
    }

    private string enqueue_status = "Not started";

    public bool Enqueue(T item, int priority, int priority2, DateTime timestamp, bool allowOverflow = false)
    {
        var queueItem = new PriorityQueueItem<T>(item, priority, priority2, timestamp);
        bool enqueued = false;

//        enqueue_status = "waiting lock";
        lock (_lock)
        {
            if (!allowOverflow && _priorityQueue.Count >= MaxQueueLength)
            {
//                enqueue_status = "waiting lock overflow";
                return false;
            }
            
            Interlocked.Increment(ref insertions);
//            enqueue_status = "waiting lock overflow enqueue";
            _priorityQueue.Enqueue(queueItem);
            enqueued = true;
//            enqueue_status = "waiting lock overflow enqueue lock end";
        }

        if (enqueued) {
//            enqueue_status = "waiting lock signal";
            _enqueueEvent.Release(); // Signal that an item has been added
        }
//        enqueue_status = "completed";
        return enqueued;
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
            await _enqueueEvent.WaitAsync(TimeSpan.FromMilliseconds(40), cancellationToken).ConfigureAwait(false); // Wait for an item to be added

            lock (_lock)
            {
                while (_priorityQueue.Count > 0 && _taskSignaler.HasWaitingTasks())
                {
                    //Console.WriteLine("SignalWorker: Woke up .. getting task");
                    var nextWorker = _taskSignaler.GetNextTask();
                    if (nextWorker == null)
                    {
                        continue;
                    }
//Console.WriteLine("SignalWorker: Getting item from queue ... Task priority is " + nextWorker.Priority);
                    var queueItem = _priorityQueue.Dequeue(nextWorker.Priority);
                    Interlocked.Increment(ref extractions);

                    nextWorker.TaskCompletionSource.SetResult(queueItem);
                    //_taskSignaler.SignalNextTask(queueItem);
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

    public string Counters => $"Ins: {insertions} Ext: {extractions}";
    public string EnqueueStatus => enqueue_status;
    public string SignalWorkerStatus => sigwrkr_status;

}