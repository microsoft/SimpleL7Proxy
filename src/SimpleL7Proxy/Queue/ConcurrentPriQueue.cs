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
        _logger.LogInformation($"[SHUTDOWN] ⏳ Queue draining - {_priorityQueue.Count} items remaining");
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
        // Lock-free fast path for priority 0 (probe requests)
        if (priority == 0) {
            // Try dedicated probe worker first
            var t = _taskSignaler.GetNextProbeTask();
            if (t != null)
            {
                t.TaskCompletionSource.SetResult(item);
                return true;
            }
            
            // Try any available worker next (still lock-free)
            var anyWorker = _taskSignaler.GetNextTask();
            if (anyWorker != null)
            {
                anyWorker.TaskCompletionSource.SetResult(item);
                return true;
            }
            
            // Only queue if NO workers available at all
            // This should be rare for probe requests
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
                if (nextWorker == null) continue;
                
                lock (_lock)
                {
                    // Check inside lock to handle race
                    if (_priorityQueue.Count == 0)
                    {
                        _taskSignaler.ReQueueTask(nextWorker);
                        break; // No more work
                    }

                    // Dequeue and deliver in one atomic operation
                    var item = _priorityQueue.Dequeue(nextWorker.Priority);
                    nextWorker.TaskCompletionSource.SetResult(item);
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
            // Register this worker's wait and nudge the signaler in case items already exist
            var waitTask = _taskSignaler.WaitForSignalAsync(preferredPriority);
            _enqueueEvent.Release(); // wake SignalWorker for potential item->worker pairing
            var parameter = await waitTask.ConfigureAwait(false);
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