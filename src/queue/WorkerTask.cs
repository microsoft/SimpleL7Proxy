/// <summary>
/// Represents a worker task with a priority.
/// </summary>
/// <typeparam name="T">The type of the result produced by the task.</typeparam>
public class WorkerTask<T>
{
    /// <summary>
    /// Gets or sets the TaskCompletionSource associated with the task.
    /// </summary>
    public TaskCompletionSource<T> TaskCompletionSource { get; set; }
 
    /// <summary>
    /// Gets or sets the priority of the task.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkerTask{T}"/> class.
    /// </summary>
    /// <param name="tcs">The TaskCompletionSource associated with the task.</param>
    /// <param name="priority">The priority of the task.</param>
    public WorkerTask(TaskCompletionSource<T> tcs, int priority)
    {
        TaskCompletionSource = tcs;
        Priority = priority;
    }
}