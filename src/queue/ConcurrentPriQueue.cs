public class ConcurrentPriQueue<T>
{
    private readonly PriorityQueue<T> _priorityQueue = new PriorityQueue<T>();
    private readonly SemaphoreSlim _enqueueEvent = new SemaphoreSlim(0);
    private readonly object _lock = new object(); // Lock object for synchronization
    private ConcurrentSignal<T> _taskSignaler = new ConcurrentSignal<T>();
    //private int insertions = 0;
    //private int extractions = 0;
  

    public int MaxQueueLength { get; set; }

    public async Task StopAsync()
    {
        while (true)
        {
            // Wait until the queue is empty
            if (thrdSafeCount == 0)
            {
                break;
            }
            Console.WriteLine($"SignalWorker: Draining, Queue count: {thrdSafeCount}");
            await Task.Delay(500).ConfigureAwait(false); // Check every 500ms
        }
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

    private string enqueue_status = "Not started";

    public bool Enqueue(T item, int priority, int priority2, DateTime timestamp, bool allowOverflow = false)
    {
        // Priority 0 is reserved for the probe requests, get the probe worker.  If not available, enqueue the request.
        if (priority == 0) {
            var t = _taskSignaler.GetNextProbeTask();
            if (t != null)
            {
                t.TaskCompletionSource.SetResult(item);
                return true;
            }
        }

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
        // Continue draining after cancellation so StopAsync can complete cleanly
        while (!cancellationToken.IsCancellationRequested || _priorityQueue.Count > 0)
        {
            // 40 seems good,  no timeout or 80ms gives reduced performance
            try
            {
                await _enqueueEvent.WaitAsync(TimeSpan.FromMilliseconds(40), cancellationToken).ConfigureAwait(false); // Wait for an item to be added
            }
            catch (OperationCanceledException)
            {
                // Token fired — keep looping to drain any remaining items before exiting
                if (_priorityQueue.Count == 0)
                    break;
            }

            while (_priorityQueue.Count > 0 && _taskSignaler.HasWaitingTasks())
            {
                //Console.WriteLine("SignalWorker: Woke up .. getting task");
                var nextWorker = _taskSignaler.GetNextTask();
                if (nextWorker == null)
                {
                    continue;
                }
                try {
                    lock (_lock)
                    {
                        nextWorker.TaskCompletionSource.SetResult( _priorityQueue.Dequeue(nextWorker.Priority) );                    
                    }
                } catch (InvalidOperationException) {
                    // This should never happen. It means that the queue is empty after we checked that the count was > 0
                    // put the worker back in the queue   
                    Console.WriteLine("SignalWorker: InvalidOperationException - requeuing task  Priority: " + nextWorker.Priority);  
                    _taskSignaler.ReQueueTask(nextWorker);               
                }
            }
        }

        Console.WriteLine("SignalWorker: Exiting - queue is empty: " + (_priorityQueue.Count == 0));

        // Shutdown
        _taskSignaler.CancelAllTasks();
    }

    public async Task<T> DequeueAsync(int preferredPriority)
    {
        // Register this worker's wait and nudge the signaler in case items already exist
        var waitTask = _taskSignaler.WaitForSignalAsync(preferredPriority);
        _enqueueEvent.Release(); // wake SignalWorker for potential item->worker pairing
        return await waitTask.ConfigureAwait(false);
    }

    //public string Counters => $"Ins: {insertions} Ext: {extractions}";
    public string EnqueueStatus => enqueue_status;
    public string SignalWorkerStatus => sigwrkr_status;

}