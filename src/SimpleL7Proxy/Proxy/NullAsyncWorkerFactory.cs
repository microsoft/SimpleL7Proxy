namespace SimpleL7Proxy.Proxy;

public class NullAsyncWorkerFactory: IAsyncWorkerFactory
{
    public Task<AsyncWorker> CreateAsync(RequestData requestData, int AsyncTriggerTimeout)
    {
        //NOP
        return Task.FromResult<AsyncWorker>(null!);
    }
}