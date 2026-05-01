namespace SimpleL7Proxy.Async.BlobStorage
{
    /// <summary>
    /// Interface for blob storage operations. The storage layer is user-agnostic — callers
    /// resolve user → container before calling. The container is the unit of isolation.
    /// </summary>
    public interface IBlobWriter : IDisposable
    {
        Task<Stream> CreateBlobAndGetOutputStreamAsync(string containerName, string blobName, CancellationToken cancellationToken = default);

        /// <summary>
        /// Uploads a fully-materialized payload to the specified blob in a single PUT request
        /// (1 round-trip). Prefer this over <see cref="CreateBlobAndGetOutputStreamAsync"/> when
        /// the entire payload is already in memory.
        /// </summary>
        Task UploadBlobAsync(string containerName, string blobName, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

        Task<bool> BlobExistsAsync(string containerName, string blobName);
        Task<Stream> ReadBlobAsStreamAsync(string containerName, string blobName);
        Task<bool> DeleteBlobAsync(string containerName, string blobName);
        // Task<string> GenerateSasTokenAsync(string containerName, string blobName, TimeSpan expiryTime);
        string GetBlobUri(string containerName, string blobName);

        /// <summary>
        /// Ensures the container exists. Idempotent and single-flight per container name.
        /// </summary>
        Task<bool> InitClientAsync(string containerName);

        bool IsInitialized { get; }
        string GetConnectionInfo();
    }
}