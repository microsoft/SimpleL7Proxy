    namespace SimpleL7Proxy.Proxy;
    public interface IAsyncWorkerFactory
    {
        Task<AsyncWorker> CreateAsync(RequestData requestData, int AsyncTriggerTimeout);
    }