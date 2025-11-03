
namespace SimpleL7Proxy.BlobStorage
{
    /// <summary>
    /// Null object pattern implementation for when blob storage is disabled.
    /// </summary>
    public class NullBlobWriter : IBlobWriter
    {
        public bool IsInitialized => false;

        public Task<Stream> CreateBlobAndGetOutputStreamAsync(string userId, string blobName)
        {
            throw new NotSupportedException("Blob storage is not enabled");
        }

        public Task<bool> BlobExistsAsync(string userId, string blobName)
        {
            throw new NotSupportedException("Blob storage is not enabled");
        }

        public Task<bool> DeleteBlobAsync(string userId, string blobName)
        {
            throw new NotSupportedException("Blob storage is not enabled");
        }

        public async Task<string> GenerateSasTokenAsync(string userId, string blobName, TimeSpan expiryTime)
        {
            await Task.CompletedTask;
            throw new NotSupportedException("Blob storage is not enabled");
        }

        public async Task<bool> InitClientAsync(string userId, string containerName)
        {

            Console.Error.WriteLine($"NULL BlobWriterFactory: InitClientAsync called for userId: {userId}, containerName: {containerName}");
            // Blob storage is not enabled, so return false.
            await Task.CompletedTask;
            return false;
        }

        public Task<Stream> ReadBlobAsStreamAsync(string userId, string blobName)
        {
            throw new NotSupportedException("Blob storage is not enabled");
        }
    }
}
