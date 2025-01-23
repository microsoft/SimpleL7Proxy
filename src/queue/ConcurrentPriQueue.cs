public class ConcurrentPriQueue<T>
{
    private readonly PriorityQueue<T> _priorityQueue = new PriorityQueue<T>();
    private readonly SemaphoreSlim _enqueueEvent = new SemaphoreSlim(0);
    private readonly object _lock = new object(); // Lock object for synchronization
    //private TaskSignaler<T> _taskSignaler = new TaskSignaler<T>();
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

    public IEnumerable<PriorityQueueItem<T>> getItems()
    {
        lock (_lock)
        {
            // Return a snapshot to prevent external modification
            return new List<PriorityQueueItem<T>>(_priorityQueue._items);
        }
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
        bool shouldwait = false;
        while (!cancellationToken.IsCancellationRequested)
        {
//            sigwrkr_status = "waiting";
            await _enqueueEvent.WaitAsync(TimeSpan.FromMilliseconds(40), cancellationToken).ConfigureAwait(false); // Wait for an item to be added

//            sigwrkr_status = "waiting lock";
            lock (_lock)
            {
                while (_priorityQueue.Count > 0 && _taskSignaler.HasWaitingTasks())
                {
//                    sigwrkr_status = "waiting lock dequeue";

                    var queueItem = _priorityQueue.Dequeue();
                    Interlocked.Increment(ref extractions);

//                    sigwrkr_status = "waiting lock dequeue signal";

                    // Signal a random task or requeue the item if no tasks are waiting
                    _taskSignaler.SignalNextTask(queueItem);
                    shouldwait = false;
                }
                // else
                // {
                //     shouldwait = true;
                //     //Console.WriteLine("SignalWorker: No tasks waiting");
                // }
//                sigwrkr_status = "waiting lock end";
            }

//             if (shouldwait) {
// //                sigwrkr_status = "waiting wait";
//                 Task.Delay(10).Wait(); // Wait for 10 ms for a Task Worker to be ready
//             }
            
        }
        Console.WriteLine("SignalWorker: Canceled");

        // Shutdown
        _taskSignaler.CancelAllTasks();
    }

    public async Task<T> DequeueAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            var parameter = await _taskSignaler.WaitForSignalAsync(id).ConfigureAwait(false);
            return parameter;
        }
        catch (TaskCanceledException)
        {
            throw;
        }
    }

    public async Task<T> dequeueAsync2(string id, CancellationToken cancellationToken)
    {

        while (true)
        {

            // Return an item if available
            lock (_lock)
            {
                if (_priorityQueue.Count > 0)
                {
                    return _priorityQueue.Dequeue();
                }
            }

            // Wait for either:
            // 1. An enqueue event (with cancellation support)
            // 2. A 1-second timeout
            try
            {
                await _enqueueEvent.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Handle cancellation gracefully
                Console.WriteLine($"Dequeue operation canceled for consumer {id}.");
                throw;
            }

            //            await _enqueueEvent.WaitAsync(cancellationToken); // Wait for an item to be added
        }

    }

    public string Counters => $"Ins: {insertions} Ext: {extractions}";
    public string EnqueueStatus => enqueue_status;
    public string SignalWorkerStatus => sigwrkr_status;

}