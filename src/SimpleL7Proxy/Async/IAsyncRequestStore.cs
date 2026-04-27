using System.IO;

namespace SimpleL7Proxy.Proxy;

public interface IAsyncRequestStore
{
    Task<bool> InitializeClientAsync(string userId, string containerName);
    Task<Stream> OpenWriteStreamAsync(string userId, string blobName);
    string GetBlobUri(string userId, string blobName);
    Task<string> GenerateSasTokenAsync(string userId, string blobName, TimeSpan expiryTime);
    Task BackupRequestAsync(RequestData requestData);
}