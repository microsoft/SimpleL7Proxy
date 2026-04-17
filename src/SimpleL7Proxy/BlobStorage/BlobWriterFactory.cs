using System.Reflection.Metadata.Ecma335;
using Azure.Storage.Blobs;
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
        string InitStatus { get; }
    }

    public class BlobWriterFactory : IBlobWriterFactory
    {
        private readonly DefaultCredential _defaultCredential;
        private readonly IOptionsMonitor<ProxyConfig> _optionsMonitor;
        private readonly ILogger<BlobWriter> _logger;
        private readonly ILogger<NullBlobWriter> _nullBlobWriterLogger;

        public BlobWriterFactory(
            DefaultCredential defaultCredential,
            IOptionsMonitor<ProxyConfig> optionsMonitor,
            ILogger<BlobWriter> logger,
            ILogger<NullBlobWriter> nullBlobWriterLogger)
        {
            _defaultCredential = defaultCredential;
            _optionsMonitor = optionsMonitor;
            _logger = logger;
            _nullBlobWriterLogger = nullBlobWriterLogger;
        }

        public string InitStatus { get; private set; }

        public IBlobWriter CreateBlobWriter()
        {
            // Console.WriteLine($"BlobWriterFactory: Creating BlobWriter with  AsyncModeEnabled={_optionsMonitor.CurrentValue.AsyncModeEnabled}  AsyncBlobStorageUseMI={_optionsMonitor.CurrentValue.AsyncBlobStorageUseMI}  AsyncBlobStorageAccountUri={_optionsMonitor.CurrentValue.AsyncBlobStorageAccountUri}  AsyncBlobStorageConnectionString={(string.IsNullOrEmpty(_optionsMonitor.CurrentValue.AsyncBlobStorageConnectionString) ? "NOT SET" : "SET")}");
            if (!_optionsMonitor.CurrentValue.AsyncModeEnabled)
            {
                InitStatus = "Disabled";
            }
            else if (_optionsMonitor.CurrentValue.AsyncBlobStorageUseMI)
            {
                var uri = _optionsMonitor.CurrentValue.AsyncBlobStorageAccountUri;
                if (string.IsNullOrEmpty(uri) || !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    InitStatus = "MI, no URI";
                }
                else
                {
                    InitStatus = $"MI, {uri}";
                    return CreateBlobWriterWithManagedIdentity(uri);
                }
            }
            else
            {
                var connectionString = _optionsMonitor.CurrentValue.AsyncBlobStorageConnectionString;
                if (string.IsNullOrEmpty(connectionString))
                {
                    InitStatus = "CS, not set";
                }
                else
                {

                    try
                    {
                        InitStatus = "CS";
                        return CreateBlobWriterWithConnectionString(connectionString);
                    }
                    catch (Exception ex)
                    {
                        InitStatus = $"CS, error: {ex.Message}";
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
                var credential = _defaultCredential.Credential;
                var blobServiceClient = new BlobServiceClient(blobServiceUri, credential);
                var blobWriter = new BlobWriter(blobServiceClient, _logger);
                blobWriter.UsesMI = true; // Set on BlobWriter, not BlobServiceClient
                //_logger.LogInformation("[STARTUP] ✓ BlobServiceClient created successfully with managed identity - URI: {Uri}", storageAccountUri);

                return blobWriter;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create BlobServiceClient with managed identity: {ex.Message}");
                InitStatus = $"MI, error: {ex.Message}";
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
                InitStatus = $"CS, error: {ex.Message}";
                return new NullBlobWriter(_nullBlobWriterLogger);
            }
        }
    }

}