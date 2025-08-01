namespace SimpleL7Proxy.BlobStorage
{
    /// <summary>
    /// Interface for blob storage operations.
    /// </summary>
    public interface IBlobWriter
    {
        Task<Stream> CreateBlobAndGetOutputStreamAsync(string userId, string blobName);
        Task<bool> DeleteBlobAsync(string userId, string blobName);
        string GenerateSasToken(string userId, string blobName, TimeSpan expiryTime);
        Task<bool> InitClientAsync(string userId, string containerName);
        bool IsInitialized { get; }
    }
}