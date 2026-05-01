using SimpleL7Proxy.Async.BlobStorage;

namespace SimpleL7Proxy.Async;

/// <summary>
/// Streaming store: large/streamed blobs (response bodies up to gigabytes) that bypass the
/// BlobWriteQueue entirely. Holds a dedicated <see cref="IBlobWriter"/> instance from the
/// factory whose <c>CreateBlobAndGetOutputStreamAsync</c> calls
/// <c>BlobClient.OpenWriteAsync</c> directly — the SDK's transfer buffer (~4 MiB by default)
/// is the only memory used regardless of total payload size.
/// </summary>
public sealed class AsyncStreamingStore : IAsyncStreamingStore, IDisposable
{
    private readonly IBlobWriter _writer;

    public AsyncStreamingStore(IBlobWriterFactory factory)
    {
        if (factory == null) throw new ArgumentNullException(nameof(factory));
        _writer = factory.CreateBlobWriter();
    }

    public Task<Stream> OpenWriteStreamAsync(string containerName, string blobName, CancellationToken cancellationToken = default)
        => _writer.CreateBlobAndGetOutputStreamAsync(containerName, blobName, cancellationToken);

    public void Dispose() => _writer?.Dispose();
}
