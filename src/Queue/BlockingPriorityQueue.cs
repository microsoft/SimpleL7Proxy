using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SimpleL7Proxy.Queue;

public class BlockingPriorityQueue<T> : IBlockingPriorityQueue<T>
{
  private readonly PriorityQueue<T> _priorityQueue;
  private readonly object _lock = new object();
  private readonly ManualResetEventSlim _enqueueEvent = new ManualResetEventSlim(false);
  private TaskSignaler<T> _taskSignaler;

  public BlockingPriorityQueue(PriorityQueue<T> baseQueue, TaskSignaler<T> taskSignaler, BackendOptions backendOptions)
  {
    MaxQueueLength = backendOptions.MaxQueueLength;
    _priorityQueue = baseQueue;
    _taskSignaler = taskSignaler;
  }

  public int MaxQueueLength { get; private set; }

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
          Task.Delay(10).Wait(); // Wait for 10 ms for a Task Worker to be ready
        }
      }
    }
    Console.WriteLine("SignalWorker: Canceled");

    // Shutdown
    _taskSignaler.CancelAllTasks();
  }

  public async Task<T> Dequeue(CancellationToken cancellationToken, string id)
  {
    try
    {
      var parameter = await _taskSignaler.WaitForSignalAsync(id);
      return parameter;
    }
    catch (TaskCanceledException)
    {
      throw;
    }
  }

}
