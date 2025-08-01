namespace SimpleL7Proxy.Storage;

public interface IRequestStorageService
{
    Task<bool> StoreRequestAsync(RequestData request);
    Task<bool> DeleteRequestAsync(RequestData request);
    Task StopAsync(CancellationToken cancellationToken);
}