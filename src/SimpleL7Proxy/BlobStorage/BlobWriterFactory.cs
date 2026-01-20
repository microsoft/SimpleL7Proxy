using System.Reflection.Metadata.Ecma335;
using Azure.Storage.Blobs;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SimpleL7Proxy.Config;

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
        private readonly ILogger<NullBlobWriter> _nullBlobWriterLogger;

        public BlobWriterFactory(
            IOptionsMonitor<BackendOptions> optionsMonitor,
            ILogger<BlobWriter> logger,
            ILogger<NullBlobWriter> nullBlobWriterLogger)
        {
            _optionsMonitor = optionsMonitor;
            _logger = logger;
            _nullBlobWriterLogger = nullBlobWriterLogger;
            _logger.LogDebug("Starting BlobWriter factory");
        }

        public IBlobWriter CreateBlobWriter()
        {
            if (!_optionsMonitor.CurrentValue.AsyncModeEnabled)
            {
                _logger.LogInformation("[INIT] ✓ BlobWriter: Async mode disabled - using NullBlobWriter");
            }
            else if (_optionsMonitor.CurrentValue.AsyncBlobStorageUseMI)
            {
                var uri = _optionsMonitor.CurrentValue.AsyncBlobStorageAccountUri;
                if (string.IsNullOrEmpty(uri) || !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("[INIT] ⚠ BlobWriter: AsyncBlobStorageAccountUri not set - using NullBlobWriter");
                }
                else
                {
                    _logger.LogInformation("[INIT] ✓ BlobWriter created with managed identity - URI: {Uri}", uri);
                    return CreateBlobWriterWithManagedIdentity(uri);
                }
            }
            else
            {
                var connectionString = _optionsMonitor.CurrentValue.AsyncBlobStorageConnectionString;
                if (string.IsNullOrEmpty(connectionString))
                {
                    _logger.LogWarning("[INIT] ⚠ BlobWriter: Connection string not provided - using NullBlobWriter");
                }
                else
                {

                    try
                    {
                        _logger.LogInformation("[INIT] ✓ BlobWriter: Creating with connection string");
                        return CreateBlobWriterWithConnectionString(connectionString);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[INIT] ✗ BlobWriter: Failed to create BlobServiceClient - using NullBlobWriter");
                    }
                }
            }

            return new NullBlobWriter(_nullBlobWriterLogger);
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
                _logger.LogInformation("[INIT] ✓ BlobServiceClient created successfully with managed identity - URI: {Uri}", storageAccountUri);

                return blobWriter;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create BlobServiceClient with managed identity: {ex.Message}");
                return new NullBlobWriter(_nullBlobWriterLogger);
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
                return new NullBlobWriter(_nullBlobWriterLogger);
            }
        }
    }

}