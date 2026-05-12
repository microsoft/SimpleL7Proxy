using System.Reflection.Metadata.Ecma335;
using Azure.Core;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SimpleL7Proxy.Config;

namespace SimpleL7Proxy.Async.BlobStorage
{
    /// <summary>
    /// Azure Blob Storage implementation of <see cref="IBlobWriterFactory"/>.
    /// Selected by the <c>IBlobWriterFactory:BlobWriterFactory</c> entry in
    /// <c>AsyncClassNames</c> (the default). Swap to <c>S3BlobWriterFactory</c>
    /// to target AWS S3 instead.
    /// </summary>
    public class BlobWriterFactory : GenericBlobFactory
    {
        private readonly DefaultCredential _defaultCredential;
        private readonly ILogger<AzureBlobWriter> _logger;

        // Shared BlobServiceClient — owns the HTTP connection pool. Created once on first call
        // so that all IBlobWriter instances (QueuedBlobWriter, BlobWriteQueue, etc.) share the same pool.
        private BlobServiceClient? _sharedBlobServiceClient;
        private bool _usesMI;

        public BlobWriterFactory(
            DefaultCredential defaultCredential,
            IOptionsMonitor<ProxyConfig> optionsMonitor,
            ILogger<AzureBlobWriter> logger,
            ILogger<NullBlobWriter> nullBlobWriterLogger,
            Lazy<BlobWorkerPump> workerPump,
            ILogger<QueuedBlobWriter> queuedLogger)
            : base(optionsMonitor, nullBlobWriterLogger, workerPump, queuedLogger)
        {
            _defaultCredential = defaultCredential;
            _logger = logger;
        }

        public override IBlobWriter CreateBlobWriter()
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
                    return CreateBlobWriterFromSharedClient(() => CreateBlobServiceClientWithManagedIdentity(uri), useMI: true);
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
                        return CreateBlobWriterFromSharedClient(() => CreateBlobServiceClientWithConnectionString(connectionString), useMI: false);
                    }
                    catch (Exception ex)
                    {
                        InitStatus = $"CS, error: {ex.Message}";
                    }
                }
            }

            return CreateNullWriter();
        }

        // Returns a BlobWriter backed by the shared BlobServiceClient, creating it on first call.
        private IBlobWriter CreateBlobWriterFromSharedClient(Func<BlobServiceClient> clientFactory, bool useMI)
        {
            if (_sharedBlobServiceClient == null)
            {
                _sharedBlobServiceClient = clientFactory();
                _usesMI = useMI;
            }
            var writer = new AzureBlobWriter(_sharedBlobServiceClient, _logger, _optionsMonitor);
            writer.UsesMI = _usesMI;
            return writer;
        }

        private BlobServiceClient CreateBlobServiceClientWithManagedIdentity(string storageAccountUri)
        {
            try
            {
                var blobServiceUri = new Uri(storageAccountUri);
                var credential = _defaultCredential.Credential;
                return new BlobServiceClient(blobServiceUri, credential, BuildClientOptions());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create BlobServiceClient with managed identity: {ex.Message}");
                InitStatus = $"MI, error: {ex.Message}";
                throw;
            }
        }

        private BlobServiceClient CreateBlobServiceClientWithConnectionString(string connectionString)
        {
            try
            {
                return new BlobServiceClient(connectionString, BuildClientOptions());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create BlobServiceClient with connection string: {ex.Message}");
                InitStatus = $"CS, error: {ex.Message}";
                throw;
            }
        }

        // Tightened retry / timeout policy for high-throughput small-blob writes.
        // SDK defaults (3 retries, 800ms initial backoff, 60s max, 100s network timeout) are tuned
        // for large-blob workloads and add seconds of latency on the first transient error. We
        // shorten them so a stuck call fails fast and the BlobWriteQueue worker can move on.
        private static BlobClientOptions BuildClientOptions()
        {
            var options = new BlobClientOptions();
            options.Retry.MaxRetries = 3;
            options.Retry.Mode = RetryMode.Exponential;
            options.Retry.Delay = TimeSpan.FromMilliseconds(200);
            options.Retry.MaxDelay = TimeSpan.FromSeconds(5);
            options.Retry.NetworkTimeout = TimeSpan.FromSeconds(15);
            return options;
        }
    }

}