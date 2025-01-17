using Microsoft.Extensions.Logging;
using SimpleL7Proxy.Backend;

namespace SimpleL7Proxy.Queue;

public class BlockingPriorityQueue<T>(
  PriorityQueue<T> baseQueue,
  TaskSignaler<T> taskSignaler,
  BackendOptions backendOptions,
  ILogger<BlockingPriorityQueue<T>> logger)
  : IBlockingPriorityQueue<T>
{
  private readonly PriorityQueue<T> _priorityQueue = baseQueue;
  private readonly Lock _lock = new();
  private readonly ManualResetEventSlim _enqueueEvent = new(false);
  private readonly TaskSignaler<T> _taskSignaler = taskSignaler;
  private readonly ILogger<BlockingPriorityQueue<T>> _logger = logger;

  public int MaxQueueLength { get; private set; } = backendOptions.MaxQueueLength;

  public void StartSignaler(CancellationToken cancellationToken)
    => Task.Run(() => SignalWorker(cancellationToken), cancellationToken);
  public void Stop() => _taskSignaler.CancelAllTasks();

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
      if (_priorityQueue.Count >= MaxQueueLength)
      {
        return false;
      }
      PriorityQueueItem<T> queueItem = new(item, priority, timestamp);
      _priorityQueue.Enqueue(queueItem);
      _enqueueEvent.Set(); // Signal that an item has been added
    }

    return true;
  }

  public bool Requeue(T item, int priority, DateTime timestamp)
  {
    lock (_lock)
    {
      PriorityQueueItem<T> queueItem = new(item, priority, timestamp);
      _priorityQueue.Enqueue(queueItem);
      _enqueueEvent.Set(); // Signal that an item has been added
    }

    return true;
  }

  public Task SignalWorker(CancellationToken cancellationToken)
  {
    while (!cancellationToken.IsCancellationRequested)
    {
      _enqueueEvent.Wait(cancellationToken); // Wait for an item to be added
      lock (_lock)
      {
        if (_priorityQueue.Count > 0 && _taskSignaler.HasWaitingTasks())
        {
          var queueItem = _priorityQueue.Dequeue();
          if (_priorityQueue.Count == 0)
          {
            _enqueueEvent.Reset(); // Reset the event if the queue is empty
          }

          // Signal a random task or requeue the item if no tasks are waiting
          _taskSignaler.SignalRandomTask(queueItem);
        }
        else
        {
          Task.Delay(10, cancellationToken).Wait(cancellationToken); // Wait for 10 ms for a Task Worker to be ready
        }
      }
    }
    _logger.LogInformation("SignalWorker: Canceled");

    // Shutdown
    _taskSignaler.CancelAllTasks();
    return Task.CompletedTask; // TODO: refactor this to a proper task completion source implementation.
  }

  public async Task<T> Dequeue(string id, CancellationToken cancellationToken)
  {
    try
    {
      return await _taskSignaler.WaitForSignalAsync(id, cancellationToken);
    }
    catch (TaskCanceledException)
    {
      throw;
    }
  }
}
