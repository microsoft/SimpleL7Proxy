using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using Azure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Concurrent;

// Review DISPOSAL_ARCHITECTURE.MD in the root for details on disposal flow

namespace SimpleL7Proxy.Async.BlobStorage
{
    /// <summary>
    /// Provides methods for writing to Azure Blob Storage.
    /// </summary>
    public class AzureBlobWriter : IBlobWriter, IDisposable
    {
        // Single-flight initialization: Lazy<Task<...>> ensures only one CreateIfNotExists call
        // per (containerName) even under 1000 concurrent first-time inits.
        private static readonly ConcurrentDictionary<string, Lazy<Task<BlobContainerClient>>> _containerClients = new();

        private readonly BlobServiceClient _blobServiceClient;
        private readonly ILogger<AzureBlobWriter> _logger;
        private readonly IOptionsMonitor<Config.ProxyConfig>? _optionsMonitor;

        // Cache for the user delegation key used to sign SAS tokens when running under MI.
        // Refreshing on every SAS request would add a management-plane round-trip per call.
        private static readonly SemaphoreSlim _delegationKeyLock = new(1, 1);
        // private static Azure.Storage.Blobs.Models.UserDelegationKey _cachedDelegationKey = default!;
        // private static bool _hasCachedDelegationKey;
        private static DateTimeOffset _delegationKeyRefreshAfter = DateTimeOffset.MinValue;
        private static readonly TimeSpan DelegationKeyLifetime = TimeSpan.FromHours(1);
        // Refresh slightly before expiry to avoid a thundering herd at the boundary.
        private static readonly TimeSpan DelegationKeyRefreshSkew = TimeSpan.FromMinutes(10);

        public bool UsesMI { get; set; }

        public bool IsInitialized => _blobServiceClient != null;


        /// <summary>
        /// Initializes a new instance of the <see cref="AzureBlobWriter"/> class.
        /// </summary>
        /// <param name="blobServiceClient">The blob service client.</param>
        /// <param name="logger">The logger instance.</param>
        /// <param name="optionsMonitor">Optional config monitor used to read tuning settings such as the streaming buffer size.</param>
        public AzureBlobWriter(BlobServiceClient blobServiceClient, ILogger<AzureBlobWriter> logger, IOptionsMonitor<Config.ProxyConfig>? optionsMonitor = null)
        {
            _blobServiceClient = blobServiceClient ?? throw new ArgumentNullException(nameof(blobServiceClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _optionsMonitor = optionsMonitor;
            _logger.LogDebug("Starting BlobWriter service");
        }


        public async Task<bool> InitClientAsync(string containerName)
        {
            if (string.IsNullOrEmpty(containerName))
            {
                _logger.LogWarning("ContainerName cannot be null or empty");
                return false;
            }

            // Single-flight: GetOrAdd guarantees only one Lazy is stored per containerName. Every concurrent
            // caller awaits the same Task, so CreateIfNotExistsAsync runs exactly once.
            // If the task faults, evict it so a later caller can retry.
            var lazy = _containerClients.GetOrAdd(containerName, _ => new Lazy<Task<BlobContainerClient>>(
                () => CreateContainerClientAsync(containerName),
                LazyThreadSafetyMode.ExecutionAndPublication));

            try
            {
                _ = await lazy.Value.ConfigureAwait(false);
                _logger.LogDebug("BlobWriter: Client ready for BlobContainerName: {BlobContainerName}", containerName);
                return true;
            }
            catch (Exception ex)
            {
                // Evict the failed entry so the next caller can retry initialization.
                _containerClients.TryRemove(new KeyValuePair<string, Lazy<Task<BlobContainerClient>>>(containerName, lazy));

                throw new BlobWriterException($"Failed to initialize BlobContainerClient for containerName: {containerName}", ex)
                {
                    Operation = "InitClientAsync: GetBlobContainerClient",
                    ContainerName = containerName
                };
            }
        }

        private async Task<BlobContainerClient> CreateContainerClientAsync(string containerName)
        {
            _logger.LogDebug("BlobWriter: Initializing BlobContainerName: {BlobContainerName}", containerName);
            var client = _blobServiceClient.GetBlobContainerClient(containerName);
            // Ensure the container exists once at init time, rather than on every write.
            await client.CreateIfNotExistsAsync().ConfigureAwait(false);
            return client;
        }

        // Synchronously resolves an already-initialized container client. Throws if init has not
        // completed successfully. Used by hot read/write paths to avoid awaiting on every call.
        private BlobContainerClient GetInitializedContainerClient(string containerName, string operation, string? blobName = null)
        {
            if (_containerClients.TryGetValue(containerName, out var lazy)
                && lazy.IsValueCreated
                && lazy.Value.IsCompletedSuccessfully)
            {
                return lazy.Value.Result;
            }

            throw new BlobWriterException($"BlobContainerClient not initialized for containerName: {containerName}. Call InitClientAsync first.")
            {
                Operation = operation,
                BlobName = blobName ?? "N/A",
                ContainerName = containerName
            };
        }

        /// <summary>
        /// Creates the blob container if it does not exist and returns an output stream for the specified blob.
        /// </summary>
        /// <param name="containerName">The name of the blob container.</param>
        /// <param name="blobName">The name of the blob.</param>
        /// <param name="cancellationToken">Cancellation token plumbed through to the SDK handshake and StageBlock calls.</param>
        /// <returns>A writable stream to the blob.</returns>
        public async Task<Stream> CreateBlobAndGetOutputStreamAsync(string containerName, string blobName, CancellationToken cancellationToken = default)
        {
            // Container existence is ensured once in InitClientAsync; do not re-check on every write.
            var containerClient = GetInitializedContainerClient(containerName, "CreateBlobAndGetOutputStreamAsync", blobName);
            var blobClient = containerClient.GetBlobClient(blobName);

            _logger.LogDebug("BlobWriter: Creating blob {ContainerName}/{BlobName}", containerClient.Name, blobName);

            // The Azure SDK retries transient failures (408/429/5xx) automatically with exponential
            // backoff honoring Retry-After. Overwrite mode means no 409 conflicts are expected here,
            // so any failure that escapes the SDK's RetryPolicy is propagated to the caller (the
            // BlobWriteQueue worker) which records it as a failed operation.
            //
            // BufferSize tuning: SDK default is ~4 MiB per StageBlock. For multi-GB payloads,
            // raising BufferSize (e.g. 8/16 MiB) reduces round trips at the cost of memory per
            // concurrent worker. Read from config at call time so it can be tuned without restart.
            var bufferBytes = _optionsMonitor?.CurrentValue.AsyncStreamingBufferSizeBytes ?? 0L;
            var options = bufferBytes > 0
                ? new global::Azure.Storage.Blobs.Models.BlobOpenWriteOptions { BufferSize = bufferBytes }
                : null;
            return await blobClient.OpenWriteAsync(overwrite: true, options: options, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Uploads a fully-materialized payload to the specified blob in a single PUT request
        /// using <see cref="BlockBlobClient.UploadAsync(BinaryData, bool, CancellationToken)"/>.
        /// One network round-trip vs. two for the streaming OpenWriteAsync path.
        /// </summary>
        public async Task UploadBlobAsync(string containerName, string blobName, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            var containerClient = GetInitializedContainerClient(containerName, "UploadBlobAsync", blobName);
            var blockClient = containerClient.GetBlockBlobClient(blobName);

            _logger.LogDebug("BlobWriter: Uploading blob {ContainerName}/{BlobName} - Size: {Size}B (single-shot)",
                containerClient.Name, blobName, data.Length);

            // SDK retry policy handles transient failures. BlockBlobClient.UploadAsync
            // overwrites unconditionally — no 409 conflicts.
            // Avoid an unconditional ToArray() copy when the ReadOnlyMemory is array-backed
            // (the common case from BinaryData/ArrayPool buffers): wrap the existing segment.
            MemoryStream ms;
            if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(data, out var seg) && seg.Array != null)
            {
                ms = new MemoryStream(seg.Array, seg.Offset, seg.Count, writable: false);
            }
            else
            {
                ms = new MemoryStream(data.ToArray(), writable: false);
            }
            using (ms)
            {
                await blockClient.UploadAsync(ms, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task<bool> BlobExistsAsync(string containerName, string blobName)
        {
            var containerClient = GetInitializedContainerClient(containerName, "BlobExistsAsync", blobName);
            var blobClient = containerClient.GetBlobClient(blobName);
            return await blobClient.ExistsAsync().ConfigureAwait(false);
        }


        public async Task<Stream> ReadBlobAsStreamAsync(string containerName, string blobName)
        {
            var containerClient = GetInitializedContainerClient(containerName, "ReadBlobAsStreamAsync", blobName);

            try
            {
                var blobClient = containerClient.GetBlobClient(blobName);
                return await blobClient.OpenReadAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new BlobWriterException($"Failed to read blob as stream for containerName: {containerName}, blobName: {blobName}", ex)
                {
                    Operation = "ReadBlobAsStreamAsync",
                    BlobName = blobName,
                    ContainerName = containerName
                };
            }
        }


        public async Task<bool> DeleteBlobAsync(string containerName, string blobName)
        {
            if (string.IsNullOrEmpty(containerName))
            {
                _logger.LogWarning("ContainerName cannot be null or empty");
                return false;
            }

            if (string.IsNullOrEmpty(blobName))
            {
                _logger.LogWarning("BlobName cannot be null or empty for containerName: {ContainerName}", containerName);
                return false;
            }

            var containerClient = GetInitializedContainerClient(containerName, "DeleteBlobAsync", blobName);
            var blobClient = containerClient.GetBlobClient(blobName);
            return await blobClient.DeleteIfExistsAsync().ConfigureAwait(false);
        }

        // /// <summary>
        // /// Generates a SAS token for the specified blob.
        // /// </summary>
        // /// <param name="containerName">The blob container name.</param>
        // /// <param name="blobName">The name of the blob.</param>
        // /// <param name="expiryTime">The expiry time for the SAS token.</param>
        // /// <returns>The SAS token URL for the blob.</returns>
        // public async Task<string> GenerateSasTokenAsync(string containerName, string blobName, TimeSpan expiryTime)
        // {
        //     if (string.IsNullOrEmpty(blobName))
        //     {
        //         throw new ArgumentException("BlobName cannot be null or empty", nameof(blobName));
        //     }

        //     var containerClient = GetInitializedContainerClient(containerName, "GenerateSasTokenAsync", blobName);

        //     try
        //     {
        //         var blobClient = containerClient.GetBlobClient(blobName);
        //         var sasBuilder = new BlobSasBuilder
        //         {
        //             BlobContainerName = containerClient.Name,
        //             BlobName = blobName,
        //             Resource = "b",
        //             StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5), // Start 5 minutes ago to account for clock skew
        //             ExpiresOn = DateTimeOffset.UtcNow.Add(expiryTime)
        //         };
        //         sasBuilder.SetPermissions(BlobSasPermissions.Read | BlobSasPermissions.Delete);

        //         if (UsesMI)
        //         {
        //             // Reuse a cached user delegation key; refreshing per call would add a
        //             // management-plane round-trip per SAS at high request rates.
        //             var userDelegationKey = await GetOrRefreshUserDelegationKeyAsync().ConfigureAwait(false);

        //             // Generate the SAS token using the user delegation key
        //             var sasQueryParameters = sasBuilder.ToSasQueryParameters(userDelegationKey, _blobServiceClient.AccountName);

        //             // Construct the full SAS URI
        //             var blobUriBuilder = new BlobUriBuilder(blobClient.Uri)
        //             {
        //                 Sas = sasQueryParameters
        //             };

        //             var sasUri = blobUriBuilder.ToUri();
        //             _logger.LogDebug("Successfully generated user delegation SAS token for blob {BlobName}", blobName);
        //             return sasUri.ToString();

        //         }
        //         else
        //         {
        //             // Check if we can use account SAS (when using connection string)
        //             if (blobClient.CanGenerateSasUri)
        //             {
        //                 var sasUri = blobClient.GenerateSasUri(sasBuilder);
        //                 _logger.LogDebug("Successfully generated account SAS token for blob {BlobName}", blobName);
        //                 return sasUri.ToString();
        //             }
        //             else
        //             {
        //                 throw new InvalidOperationException("Cannot generate SAS token. Either enable managed identity (UsesMI=true) or provide a connection string with account keys.");
        //             }
        //         }
        //     }
        //     catch (Exception ex)
        //     {
        //         _logger.LogError(ex, "Failed to generate SAS token for blob {BlobName} in container {ContainerName}", blobName, containerClient.Name);
        //         throw new BlobWriterException($"Failed to generate SAS token for blob {blobName} in container {containerClient.Name}", ex)
        //         {
        //             Operation = "GenerateSasTokenAsync",
        //             BlobName = blobName,
        //             ContainerName = containerClient.Name
        //         };
        //     }
        // }

        /// <summary>
        /// Gets the base URI for a blob without SAS token.
        /// </summary>
        /// <param name="containerName">The blob container name.</param>
        /// <param name="blobName">The name of the blob.</param>
        /// <returns>The base URI of the blob.</returns>
        public string GetBlobUri(string containerName, string blobName)
        {
            if (string.IsNullOrEmpty(blobName))
            {
                throw new ArgumentException("BlobName cannot be null or empty", nameof(blobName));
            }

            var containerClient = GetInitializedContainerClient(containerName, "GetBlobUri", blobName);
            var blobClient = containerClient.GetBlobClient(blobName);
            return blobClient.Uri.ToString();
        }

        // // Returns a cached user delegation key, refreshing it shortly before expiry. Single-flight
        // // protected via SemaphoreSlim so a refresh storm cannot fan out.
        // private async Task<Azure.Storage.Blobs.Models.UserDelegationKey> GetOrRefreshUserDelegationKeyAsync()
        // {
        //     var now = DateTimeOffset.UtcNow;
        //     if (_hasCachedDelegationKey && now < _delegationKeyRefreshAfter)
        //     {
        //         return _cachedDelegationKey;
        //     }

        //     await _delegationKeyLock.WaitAsync().ConfigureAwait(false);
        //     try
        //     {
        //         now = DateTimeOffset.UtcNow;
        //         if (_hasCachedDelegationKey && now < _delegationKeyRefreshAfter)
        //         {
        //             return _cachedDelegationKey;
        //         }

        //         _logger.LogDebug("Requesting user delegation key for SAS token generation");
        //         var start = now.AddMinutes(-5); // tolerate clock skew
        //         var expiry = now.Add(DelegationKeyLifetime);
        //         var response = await _blobServiceClient
        //             .GetUserDelegationKeyAsync(new global::Azure.Storage.Blobs.Models.BlobGetUserDelegationKeyOptions(expiry) { StartsOn = start })
        //             .ConfigureAwait(false);

        //         _cachedDelegationKey = response.Value;
        //         _delegationKeyRefreshAfter = expiry - DelegationKeyRefreshSkew;
        //         _hasCachedDelegationKey = true;
        //         return _cachedDelegationKey;
        //     }
        //     finally
        //     {
        //         _delegationKeyLock.Release();
        //     }
        // }

        /// <summary>
        /// Gets connection information for health check and diagnostics.
        /// </summary>
        /// <returns>A string describing the blob storage connection configuration.</returns>
        public string GetConnectionInfo()
        {
            if (_blobServiceClient == null)
            {
                return "Not Initialized";
            }

            if (UsesMI)
            {
                return $"MI: {_blobServiceClient.Uri.Host}";
            }
            else
            {
                return $"ConnectionString: {_blobServiceClient.Uri.Host}";
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            // The BlobServiceClient is owned by BlobWriterFactory (singleton, shared
            // across all BlobWriter instances), so we deliberately do not dispose it here.
        }
        

    }
}