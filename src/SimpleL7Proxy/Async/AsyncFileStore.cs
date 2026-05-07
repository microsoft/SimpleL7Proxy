using SimpleL7Proxy.Async.BlobStorage;

namespace SimpleL7Proxy.Async;

/// <summary>
/// File-style store: small one-shot blobs that flow through the BlobWriteQueue.
/// In async mode <see cref="IBlobWriter"/> is registered as <c>QueuedBlobWriter</c>, so
/// stream-based calls here transparently go through the queue. <see cref="WriteAsync"/>
/// bypasses the queue for minimum latency on already-materialized payloads.
/// </summary>
public sealed class AsyncFileStore : IAsyncFileStore
{
    private readonly IBlobWriter _writer;

    public AsyncFileStore(IBlobWriter writer)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public Task<bool> InitializeClientAsync(string containerName)
        => _writer.InitClientAsync(containerName);

    public Task WriteAsync(string containerName, string blobName, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        => _writer.UploadBlobAsync(containerName, blobName, data, cancellationToken);

    public (string dataBlobUri, string headerBlobUri) GetBlobUriPair(
        string containerName, string dataBlobName, string headerBlobName)
        => (_writer.GetBlobUri(containerName, dataBlobName), _writer.GetBlobUri(containerName, headerBlobName));

    // public async Task<(string dataBlobUri, string headerBlobUri)> GenerateSasTokenPairAsync(
    //     string containerName, string dataBlobName, string headerBlobName, TimeSpan expiry)
    // {
    //     var dataTask   = _writer.GenerateSasTokenAsync(containerName, dataBlobName, expiry);
    //     var headerTask = _writer.GenerateSasTokenAsync(containerName, headerBlobName, expiry);
    //     await Task.WhenAll(dataTask, headerTask).ConfigureAwait(false);
    //     return (await dataTask, await headerTask);
    // }

    public Task<bool> BlobExistsAsync(string containerName, string blobName)
        => _writer.BlobExistsAsync(containerName, blobName);

    public Task<Stream> ReadBlobAsStreamAsync(string containerName, string blobName)
        => _writer.ReadBlobAsStreamAsync(containerName, blobName);

    public Task<bool> DeleteBlobAsync(string containerName, string blobName)
        => _writer.DeleteBlobAsync(containerName, blobName);
}

