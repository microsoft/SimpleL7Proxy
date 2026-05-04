using System.IO;

namespace SimpleL7Proxy.Async;

/// <summary>
/// Store for LARGE/streamed blobs (response bodies that may run to gigabytes). Writes go
/// directly to <c>BlobClient.OpenWriteAsync</c>, bypassing the BlobWriteQueue entirely so
/// the SDK's transfer buffer (~4 MiB by default) is the only memory used regardless of
/// total payload size.
///
/// For small one-shot blobs (headers, status messages) use <see cref="IAsyncFileStore"/>
/// instead — it batches and dedups through the queue and uploads with a single PUT.
///
/// Container init and SAS/URI generation are owned by <see cref="IAsyncFileStore"/>;
/// the streaming store's only responsibility is producing a write stream.
/// </summary>
public interface IAsyncStreamingStore
{
    /// <summary>
    /// Opens a streaming write stream for a blob in the given container. The returned
    /// stream stages blocks to storage as data is written; the blob is committed when
    /// the stream is disposed.
    /// </summary>
    Task<Stream> OpenWriteStreamAsync(string containerName, string blobName, CancellationToken cancellationToken = default);
}
