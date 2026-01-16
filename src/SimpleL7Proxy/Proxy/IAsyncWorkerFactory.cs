    namespace SimpleL7Proxy.Proxy;
    public interface IAsyncWorkerFactory
    {
        AsyncWorker CreateAsync(RequestData requestData, int AsyncTriggerTimeout);
    }