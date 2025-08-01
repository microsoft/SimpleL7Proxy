using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimpleL7Proxy.Backend;

namespace SimpleL7Proxy.BlobStorage
{
    /// <summary>
    /// Factory for creating BlobWriter instances.
    /// </summary>
    public interface IBlobWriterFactory
    {
        IBlobWriter CreateBlobWriter();
    }

    public class BlobWriterFactory : IBlobWriterFactory
    {
        private readonly IOptionsMonitor<BackendOptions> _optionsMonitor;
        private readonly ILogger<BlobWriter> _logger;

        public BlobWriterFactory(
            IOptionsMonitor<BackendOptions> optionsMonitor,
            ILogger<BlobWriter> logger)
        {
            _optionsMonitor = optionsMonitor;
            _logger = logger;
        }

        public IBlobWriter CreateBlobWriter()
        {
            if (!_optionsMonitor.CurrentValue.AsyncModeEnabled)
            {
                return new NullBlobWriter();
            }

            var connectionString = _optionsMonitor.CurrentValue.AsyncBlobStorageConnectionString;
            if (string.IsNullOrEmpty(connectionString) )
            {
                _logger.LogError("Invalid blob storage connection string provided");
                return new NullBlobWriter();
            }

            try
            {
                var blobServiceClient = new BlobServiceClient(connectionString);
                return new BlobWriter(blobServiceClient, _logger);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create BlobServiceClient");
                return new NullBlobWriter();
            }
        }
    }

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

        public Task<bool> DeleteBlobAsync(string userId, string blobName)
        {
            throw new NotSupportedException("Blob storage is not enabled");
        }

        public string GenerateSasToken(string userId, string blobName, TimeSpan expiryTime)
        {
            throw new NotSupportedException("Blob storage is not enabled");
        }

        public Task<bool> InitClientAsync(string userId, string containerName)
        {
            return Task.FromResult(false);
        }
    }
}