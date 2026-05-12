using Microsoft.Extensions.Logging;

namespace SimpleL7Proxy.Async.BlobStorage
{
    /// <summary>
    /// Null object pattern implementation for when blob storage is disabled. Implements
    /// <see cref="IQueuedBlobWriter"/> for non-async mode operation.
    /// </summary>
    public class NullBlobWriter : IQueuedBlobWriter
    {
        private readonly ILogger<NullBlobWriter> _logger;

        public NullBlobWriter(ILogger<NullBlobWriter> logger)
        {
            _logger = logger;
        }

        public bool IsInitialized => false;

        public Task<Stream> CreateBlobAndGetOutputStreamAsync(string containerName, string blobName, CancellationToken cancellationToken = default)
        {
            // Return a no-op stream (Stream.Null) instead of throwing
            // This allows async processing to work even when blob storage is disabled
            return Task.FromResult<Stream>(Stream.Null);
        }

        public Task UploadBlobAsync(string containerName, string blobName, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            // Blob storage disabled: silently succeed
            return Task.CompletedTask;
        }

        public Task<bool> BlobExistsAsync(string containerName, string blobName)
        {
            // Blob storage is disabled, so no blobs exist
            return Task.FromResult(false);
        }

        public Task<bool> DeleteBlobAsync(string containerName, string blobName)
        {
            // Blob storage is disabled, deletion is a no-op (success)
            return Task.FromResult(true);
        }

        public async Task<string> GenerateSasTokenAsync(string containerName, string blobName, TimeSpan expiryTime)
        {
            await Task.CompletedTask;
            // Return a placeholder SAS token instead of throwing
            // This allows async processing to complete even though blobs aren't stored
            return "null://blob-storage-disabled";
        }

        public string GetBlobUri(string containerName, string blobName)
        {
            // Return a placeholder URI for disabled blob storage
            return "null://blob-storage-disabled";
        }

        public async Task<bool> InitClientAsync(string containerName)
        {
            _logger.LogWarning("[BlobWriter:Null] InitClientAsync called - Container: {ContainerName} (NULL implementation active, blob storage disabled)",
                containerName);
            // Blob storage is not enabled, but this is a valid no-op implementation.
            await Task.CompletedTask;
            return true; // Return true to indicate successful initialization (even though it's a no-op)
        }

        public Task<Stream> ReadBlobAsStreamAsync(string containerName, string blobName)
        {
            // Return an empty stream instead of throwing
            return Task.FromResult<Stream>(Stream.Null);
        }

        public string GetConnectionInfo()
        {
            return "Disabled (NullBlobWriter)";
        }

        public void Dispose()
        {
            // No-op: NullBlobWriter has no resources to release
        }
    }
}
