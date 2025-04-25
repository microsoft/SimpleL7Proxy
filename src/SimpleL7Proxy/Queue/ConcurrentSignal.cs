using System.Collections.Concurrent;

namespace SimpleL7Proxy.Queue;

public class ConcurrentSignal<T>
{
//    private readonly ConcurrentQueue<TaskCompletionSource<T>> _taskCompletionSources = new ConcurrentQueue<TaskCompletionSource<T>>();
    private readonly ConcurrentQueue<WorkerTask<T>> _taskCompletionSources = new ConcurrentQueue<WorkerTask<T>>();
    private WorkerTask<T>? _probeWorkerTask;
    private bool _probeWorkerTaskSet;

    // This is the main method to signal a task.  It will return a TaskCompletionSource that can be awaited.
    public Task<T> WaitForSignalAsync(int priority)
    {
        var tcs = new TaskCompletionSource<T>();
        var workerTask = new WorkerTask<T>(tcs, priority);
        if (priority == 0)
        {
            _probeWorkerTask = workerTask;
            _probeWorkerTaskSet = true;
        } 
        else {
            _taskCompletionSources.Enqueue(workerTask);
        }

        return tcs.Task;
    }

    // This method is called by the PriQueue to requeue the task if it was next in line, but there were no requests available
    // This should never get called, but just if this is happening we would be loosing workers without doing this.
    public void ReQueueTask(WorkerTask<T> workerTask)
    {
        if (workerTask.Priority == 0)
        {
            _probeWorkerTask = workerTask;
            _probeWorkerTaskSet = true;
        } 
        else {
            _taskCompletionSources.Enqueue(workerTask);
        }
    }

    // This is looking for the designated probe worker task. 
 public WorkerTask<T>? GetNextProbeTask()
    {
        if (_probeWorkerTaskSet)
        {
            _probeWorkerTaskSet = false;
            return _probeWorkerTask;
        }
        return GetNextTask();
    }
   
    // This is looking for the next task in line.  The next available request will be processed by the worker.
public WorkerTask<T>? GetNextTask()
    {
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

        if (_probeWorkerTaskSet)
        {
            _probeWorkerTaskSet = false;
            _probeWorkerTask?.TaskCompletionSource.TrySetCanceled();
        }
    }

    public bool HasWaitingTasks()
    {
        //Console.WriteLine("HasWaitingTasks: " + !_taskCompletionSources.IsEmpty + " " + _taskCompletionSources.Count);  
        return !_taskCompletionSources.IsEmpty;
    }
}