
namespace SimpleL7Proxy.Queue;
public class WorkerTask<T>
{
    public TaskCompletionSource<T> TaskCompletionSource { get; set; }
    public int Priority { get; set; }

    public WorkerTask(TaskCompletionSource<T> tcs, int priority)
    {
        TaskCompletionSource = tcs;
        Priority = priority;
    }
}