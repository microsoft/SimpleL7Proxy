using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

using SimpleL7Proxy.Config;

namespace SimpleL7Proxy.Queue;
public class ConcurrentPriQueue<T> : IConcurrentPriQueue<T>
{
    private readonly PriorityQueue<T> _priorityQueue = new PriorityQueue<T>();
    private readonly SemaphoreSlim _enqueueEvent = new SemaphoreSlim(0);
    private readonly object _lock = new object(); // Lock object for synchronization
    private ConcurrentSignal<T> _taskSignaler = new ConcurrentSignal<T>();
    private readonly ILogger<ConcurrentPriQueue<T>> _logger;
    //private int insertions = 0;
    //private int extractions = 0;

    private readonly BackendOptions _options;

  
    public ConcurrentPriQueue(IOptions<BackendOptions> backendOptions, ILogger<ConcurrentPriQueue<T>> logger)
    {
        ArgumentNullException.ThrowIfNull(backendOptions);
        _options = backendOptions.Value;
        _logger = logger;

        MaxQueueLength = _options.MaxQueueLength;
    }

    public int MaxQueueLength { get;  }

    // wait till the queue empties then tell all the workers to stop
    public async Task StopAsync()
    {
        _logger.LogCritical($"Queue: Waiting for queue to empty before stopping. Count: {_priorityQueue.Count}");
        while (_priorityQueue.Count > 0)
        {
            await Task.Delay(100);
        }

        // Shutdown
        _taskSignaler.CancelAllTasks();
    }

    public void StartSignaler(CancellationToken cancellationToken)
    {
        Task.Run(() => SignalWorker(cancellationToken), cancellationToken);
    }

    // Thread-safe Count property
    public int thrdSafeCount { get { return _priorityQueue.Count; } }

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