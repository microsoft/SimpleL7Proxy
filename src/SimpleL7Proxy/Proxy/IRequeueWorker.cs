
namespace SimpleL7Proxy.Proxy;

public interface IRequeueWorker
{
    void DelayAsync(RequestData request, int delayMs);
    Task CancelAllCancelableTasks();
}