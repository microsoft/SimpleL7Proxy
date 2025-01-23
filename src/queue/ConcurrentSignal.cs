using System.Collections.Concurrent;

public class ConcurrentSignal<T>
{
    private readonly ConcurrentQueue<TaskCompletionSource<T>> _taskCompletionSources = new ConcurrentQueue<TaskCompletionSource<T>>();

    public Task<T> WaitForSignalAsync(string taskId)
    {
        var tcs = new TaskCompletionSource<T>();
        _taskCompletionSources.Enqueue(tcs);
        return tcs.Task;
    }

    // public void SignalTask(string taskId, T parameter)
    // {
    //     if (_taskCompletionSources.TryRemove(taskId, out var tcs))
    //     {
    //         tcs.SetResult(parameter);
    //     }
    // }

    public bool SignalNextTask(T parameter)
    {
        var taskIds = _taskCompletionSources.TryDequeue(out var tcs);
        if (tcs != null)
        {
            tcs.SetResult(parameter);
            return true;
        }
        
        return false;
    }

    public void CancelAllTasks()
    {
        while (_taskCompletionSources.TryDequeue(out var tcs))
        {
            tcs.TrySetCanceled();
        }
    }

    public bool HasWaitingTasks()
    {
        //Console.WriteLine("HasWaitingTasks: " + !_taskCompletionSources.IsEmpty + " " + _taskCompletionSources.Count);  
        return !_taskCompletionSources.IsEmpty;
    }
}