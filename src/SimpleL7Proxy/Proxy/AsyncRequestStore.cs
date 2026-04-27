using SimpleL7Proxy.Async.BlobStorage;
using SimpleL7Proxy.DTO;

namespace SimpleL7Proxy.Proxy;

public class AsyncRequestStore : IAsyncRequestStore
{
    private readonly IBlobWriter _blobWriter;
    private readonly IRequestDataBackupService _requestBackupService;

    public AsyncRequestStore(IBlobWriter blobWriter, IRequestDataBackupService requestBackupService)
    {
        _blobWriter = blobWriter ?? throw new ArgumentNullException(nameof(blobWriter));
        _requestBackupService = requestBackupService ?? throw new ArgumentNullException(nameof(requestBackupService));
    }

    public Task<bool> InitializeClientAsync(string userId, string containerName)
        => _blobWriter.InitClientAsync(userId, containerName);

    public Task<Stream> OpenWriteStreamAsync(string userId, string blobName)
        => _blobWriter.CreateBlobAndGetOutputStreamAsync(userId, blobName);

    public string GetBlobUri(string userId, string blobName)
        => _blobWriter.GetBlobUri(userId, blobName);

    public Task<string> GenerateSasTokenAsync(string userId, string blobName, TimeSpan expiryTime)
        => _blobWriter.GenerateSasTokenAsync(userId, blobName, expiryTime);

    public Task BackupRequestAsync(RequestData requestData)
        => _requestBackupService.BackupAsync(requestData);
}