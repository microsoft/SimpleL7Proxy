using System.Collections.Concurrent;

public class ConcurrentSignal<T>
{
//    private readonly ConcurrentQueue<TaskCompletionSource<T>> _taskCompletionSources = new ConcurrentQueue<TaskCompletionSource<T>>();
    private readonly ConcurrentQueue<WorkerTask<T>> _taskCompletionSources = new ConcurrentQueue<WorkerTask<T>>();
    private WorkerTask<T> _probeWorkerTask;
    private bool _probeWorkerTaskSet;

    public Task<T> WaitForSignalAsync(int priority)
    {
        var tcs = new TaskCompletionSource<T>();
        var workerTask = new WorkerTask<T>(tcs, priority);
        _taskCompletionSources.Enqueue(workerTask);
        return tcs.Task;
    }

    public bool SignalNextTask(T parameter)
    {
        _taskCompletionSources.TryDequeue(out var workerTask);
        if (workerTask != null)
        {
            workerTask.TaskCompletionSource.SetResult(parameter);
            return true;
        }
        
        return false;
    }


    public WorkerTask<T>? GetNextTask(bool getProbeWorker = false)
    {
        if (getProbeWorker)
        {
            return _probeWorkerTask;
        }   

        _taskCompletionSources.TryDequeue(out var workerTask);
        if (workerTask != null)
        {
            return workerTask;
        }
        
        return null;
    }
    public void CancelAllTasks()
    {
        while (_taskCompletionSources.TryDequeue(out var wt))
        {
            wt.TaskCompletionSource.TrySetCanceled();
        }
    }

    public bool HasWaitingTasks()
    {
        //Console.WriteLine("HasWaitingTasks: " + !_taskCompletionSources.IsEmpty + " " + _taskCompletionSources.Count);  
        return !_taskCompletionSources.IsEmpty;
    }
}