using System.Reflection.Metadata.Ecma335;
using Azure.Storage.Blobs;
using Azure.Identity;
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
            _logger.LogDebug("Starting BlobWriter factory");
        }

        public IBlobWriter CreateBlobWriter()
        {
            if (!_optionsMonitor.CurrentValue.AsyncModeEnabled)
            {
                _logger.LogError("Async mode is disabled, returning NullBlobWriter.");
            }
            else if (_optionsMonitor.CurrentValue.AsyncBlobStorageUseMI)
            {
                var uri = _optionsMonitor.CurrentValue.AsyncBlobStorageAccountUri;
                if (string.IsNullOrEmpty(uri) || !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError("AsyncBlobStorageAccountUri is not set. Cannot create BlobWriter.");
                }
                else
                {
                    _logger.LogInformation("Creating BlobWriter with managed identity for URI: {Uri}", uri);
                    return CreateBlobWriterWithManagedIdentity(uri);
                }
            }
            else
            {
                var connectionString = _optionsMonitor.CurrentValue.AsyncBlobStorageConnectionString;
                if (string.IsNullOrEmpty(connectionString))
                {
                    _logger.LogError("Invalid blob storage connection string provided");
                }
                else
                {

                    try
                    {
                        _logger.LogInformation("Creating BlobServiceClient with provided connection string.");
                        return CreateBlobWriterWithConnectionString(connectionString);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to create BlobServiceClient: {ex.Message}");
                    }
                }
            }

            return new NullBlobWriter();
        }

        private IBlobWriter CreateBlobWriterWithManagedIdentity(string storageAccountUri)
        {
            try
            {
                Uri blobServiceUri;

                blobServiceUri = new Uri(storageAccountUri);

                // Use DefaultAzureCredential for managed identity
                var credential = new DefaultAzureCredential();
                var blobServiceClient = new BlobServiceClient(blobServiceUri, credential);
                var blobWriter = new BlobWriter(blobServiceClient, _logger);
                blobWriter.UsesMI = true; // Set on BlobWriter, not BlobServiceClient
                _logger.LogInformation("BlobServiceClient created successfully with managed identity for URI: {Uri}", storageAccountUri);

                return blobWriter;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create BlobServiceClient with managed identity: {ex.Message}");
                return new NullBlobWriter();
            }
        }

        private IBlobWriter CreateBlobWriterWithConnectionString(string connectionString)
        {
            try
            {
                var blobServiceClient = new BlobServiceClient(connectionString);
                var blobWriter = new BlobWriter(blobServiceClient, _logger);
                blobWriter.UsesMI = false; // Set to false for connection string authentication
                return blobWriter;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create BlobServiceClient with connection string: {ex.Message}");
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