using System.IO;

namespace SimpleL7Proxy.Async;

/// <summary>
/// Store for SMALL one-shot blobs (response headers, status messages, server-side request
/// backups). Writes flow through the BlobWriteQueue and are uploaded with a single PUT
/// (BlockBlobClient.UploadAsync, 1 round-trip). Suitable only for payloads that fit
/// comfortably in memory.
///
/// The container is the unit of isolation. Callers resolve user/tenant → container name
/// before calling; this layer is user-agnostic. Use <see cref="Constants.Server"/> for
/// system/server-scoped blobs.
///
/// For potentially large streamed payloads (response bodies that may be GBs) use
/// <see cref="IAsyncStreamingStore"/> instead — it bypasses the queue and streams blocks
/// directly to storage so memory stays bounded.
/// </summary>
public interface IAsyncFileStore
{
    /// <summary>Ensures the container exists. Idempotent and single-flight per container name.</summary>
    Task<bool> InitializeClientAsync(string containerName);

    (string dataBlobUri, string headerBlobUri) GetBlobUriPair(
        string containerName, string dataBlobName, string headerBlobName);

    // Task<(string dataBlobUri, string headerBlobUri)> GenerateSasTokenPairAsync(
    //     string containerName, string dataBlobName, string headerBlobName, TimeSpan expiry);

    /// <summary>
    /// Writes a fully-materialized payload to the specified blob in a single PUT
    /// (BlockBlobClient.UploadAsync, 1 round-trip). Bypasses the BlobWriteQueue for
    /// minimum latency. Prefer this when the bytes are already in memory.
    /// </summary>
    Task WriteAsync(string containerName, string blobName, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    Task<bool>   BlobExistsAsync(string containerName, string blobName);
    Task<Stream> ReadBlobAsStreamAsync(string containerName, string blobName);
    Task<bool>   DeleteBlobAsync(string containerName, string blobName);
}

