using Microsoft.Extensions.Logging;

namespace SimpleL7Proxy.BlobStorage
{
    /// <summary>
    /// Null object pattern implementation for when blob storage is disabled.
    /// </summary>
    public class NullBlobWriter : IBlobWriter
    {
        private readonly ILogger<NullBlobWriter> _logger;

        public NullBlobWriter(ILogger<NullBlobWriter> logger)
        {
            _logger = logger;
        }

        public bool IsInitialized => false;

        public Task<Stream> CreateBlobAndGetOutputStreamAsync(string userId, string blobName)
        {
            // Return a no-op stream (Stream.Null) instead of throwing
            // This allows async processing to work even when blob storage is disabled
            return Task.FromResult<Stream>(Stream.Null);
        }

        public Task<bool> BlobExistsAsync(string userId, string blobName)
        {
            // Blob storage is disabled, so no blobs exist
            return Task.FromResult(false);
        }

        public Task<bool> DeleteBlobAsync(string userId, string blobName)
        {
            // Blob storage is disabled, deletion is a no-op (success)
            return Task.FromResult(true);
        }

        public async Task<string> GenerateSasTokenAsync(string userId, string blobName, TimeSpan expiryTime)
        {
            await Task.CompletedTask;
            // Return a placeholder SAS token instead of throwing
            // This allows async processing to complete even though blobs aren't stored
            return "null://blob-storage-disabled";
        }

        public string GetBlobUri(string userId, string blobName)
        {
            // Return a placeholder URI for disabled blob storage
            return "null://blob-storage-disabled";
        }

        public async Task<bool> InitClientAsync(string userId, string containerName)
        {
            _logger.LogWarning("[BlobWriter:Null] InitClientAsync called - UserId: {UserId}, Container: {ContainerName} (NULL implementation active, blob storage disabled)", 
                userId, containerName);
            // Blob storage is not enabled, but this is a valid no-op implementation.
            await Task.CompletedTask;
            return true; // Return true to indicate successful initialization (even though it's a no-op)
        }

        public Task<Stream> ReadBlobAsStreamAsync(string userId, string blobName)
        {
            // Return an empty stream instead of throwing
            return Task.FromResult<Stream>(Stream.Null);
        }

        public string GetConnectionInfo()
        {
            return "Disabled (NullBlobWriter)";
        }
    }
}
