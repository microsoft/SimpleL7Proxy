namespace SimpleL7Proxy.Proxy;

public class NullAsyncWorkerFactory: IAsyncWorkerFactory
{
    public AsyncWorker CreateAsync(RequestData requestData, int AsyncTriggerTimeout)
    {
        //NOP
        return null!;
    }
}