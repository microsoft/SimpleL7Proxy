using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SimpleL7Proxy.Queue;

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

  public bool SignalRandomTask(T parameter)
  {
    var taskIds = _taskCompletionSources.Keys.ToList();
    if (taskIds.Count > 0)
    {
      var randomTaskId = taskIds[_random.Next(taskIds.Count)];
      SignalTask(randomTaskId, parameter);

      return true;
    }

    return false;
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

  public bool HasWaitingTasks()
  {
    return !_taskCompletionSources.IsEmpty;
  }
}
