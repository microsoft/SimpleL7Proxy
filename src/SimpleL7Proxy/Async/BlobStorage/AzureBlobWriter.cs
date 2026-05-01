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
    public class BlobWriter : IBlobWriter, IDisposable
    {
        // Single-flight initialization: Lazy<Task<...>> ensures only one CreateIfNotExists call
        // per (userId) even under 1000 concurrent first-time inits.
        private static readonly ConcurrentDictionary<string, Lazy<Task<BlobContainerClient>>> _containerClients = new();

        private readonly BlobServiceClient _blobServiceClient;
        private readonly ILogger<BlobWriter> _logger;

        // Cache for the user delegation key used to sign SAS tokens when running under MI.
        // Refreshing on every SAS request would add a management-plane round-trip per call.
        private static readonly SemaphoreSlim _delegationKeyLock = new(1, 1);
        private static Azure.Storage.Blobs.Models.UserDelegationKey _cachedDelegationKey = default!;
        private static bool _hasCachedDelegationKey;
        private static DateTimeOffset _delegationKeyRefreshAfter = DateTimeOffset.MinValue;
        private static readonly TimeSpan DelegationKeyLifetime = TimeSpan.FromHours(1);
        // Refresh slightly before expiry to avoid a thundering herd at the boundary.
        private static readonly TimeSpan DelegationKeyRefreshSkew = TimeSpan.FromMinutes(10);

        public bool UsesMI { get; set; }

        public bool IsInitialized => _blobServiceClient != null;
        private bool _disposed = false;


        /// <summary>
        /// Initializes a new instance of the <see cref="BlobWriter"/> class.
        /// </summary>
        /// <param name="blobServiceClient">The blob service client.</param>
        /// <param name="logger">The logger instance.</param>
        public BlobWriter(BlobServiceClient blobServiceClient, ILogger<BlobWriter> logger)
        {
            _blobServiceClient = blobServiceClient ?? throw new ArgumentNullException(nameof(blobServiceClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _logger.LogDebug("Starting BlobWriter service");
        }


        public async Task<bool> InitClientAsync(string userId, string containerName)
        {
            if (string.IsNullOrEmpty(userId))
            {
                _logger.LogWarning("UserId cannot be null or empty");
                return false;
            }

            if (string.IsNullOrEmpty(containerName))
            {
                _logger.LogWarning("ContainerName cannot be null or empty for userId: {UserId}", userId);
                return false;
            }

            // Single-flight: GetOrAdd guarantees only one Lazy is stored per userId. Every concurrent
            // caller awaits the same Task, so CreateIfNotExistsAsync runs exactly once.
            // If the task faults, evict it so a later caller can retry.
            var lazy = _containerClients.GetOrAdd(userId, _ => new Lazy<Task<BlobContainerClient>>(
                () => CreateContainerClientAsync(userId, containerName),
                LazyThreadSafetyMode.ExecutionAndPublication));

            try
            {
                _ = await lazy.Value.ConfigureAwait(false);
                _logger.LogDebug("BlobWriter: Client ready for UserId: {UserId}, BlobContainerName: {BlobContainerName}", userId, containerName);
                return true;
            }
            catch (Exception ex)
            {
                // Evict the failed entry so the next caller can retry initialization.
                _containerClients.TryRemove(new KeyValuePair<string, Lazy<Task<BlobContainerClient>>>(userId, lazy));

                throw new BlobWriterException($"Failed to initialize BlobContainerClient for userId: {userId}, containerName: {containerName}", ex)
                {
                    Operation = "InitClientAsync: GetBlobContainerClient",
                    ContainerName = containerName,
                    UserId = userId
                };
            }
        }

        private async Task<BlobContainerClient> CreateContainerClientAsync(string userId, string containerName)
        {
            _logger.LogDebug("BlobWriter: Initializing for UserId: {UserId}, BlobContainerName: {BlobContainerName}", userId, containerName);
            var client = _blobServiceClient.GetBlobContainerClient(containerName);
            // Ensure the container exists once at init time, rather than on every write.
            await client.CreateIfNotExistsAsync().ConfigureAwait(false);
            return client;
        }

        // Synchronously resolves an already-initialized container client. Throws if init has not
        // completed successfully. Used by hot read/write paths to avoid awaiting on every call.
        private BlobContainerClient GetInitializedContainerClient(string userId, string operation, string? blobName = null)
        {
            if (_containerClients.TryGetValue(userId, out var lazy)
                && lazy.IsValueCreated
                && lazy.Value.IsCompletedSuccessfully)
            {
                return lazy.Value.Result;
            }

            throw new BlobWriterException($"BlobContainerClient not initialized for userId: {userId}. Call InitClientAsync first.")
            {
                Operation = operation,
                BlobName = blobName ?? "N/A",
                UserId = userId
            };
        }

        /// <summary>
        /// Creates the blob container if it does not exist and returns an output stream for the specified blob.
        /// </summary>
        /// <param name="blobName">The name of the blob.</param>
        /// <returns>A writable stream to the blob.</returns>
        public async Task<Stream> CreateBlobAndGetOutputStreamAsync(string userId, string blobName)
        {
            // Container existence is ensured once in InitClientAsync; do not re-check on every write.
            var containerClient = GetInitializedContainerClient(userId, "CreateBlobAndGetOutputStreamAsync", blobName);
            var blobClient = containerClient.GetBlobClient(blobName);

            _logger.LogDebug("BlobWriter: Creating blob {ContainerName}/{BlobName} for user {UserId}", containerClient.Name, blobName, userId);

            // The Azure SDK retries transient failures (408/429/5xx) automatically with exponential backoff.
            // 409 (Conflict) is NOT retried by the SDK, so we handle it here for concurrent-write scenarios.
            const int maxRetries = 3;
            const int baseDelayMs = 100;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    return await blobClient.OpenWriteAsync(overwrite: true).ConfigureAwait(false);
                }
                catch (RequestFailedException ex) when (ex.Status == 409 && attempt < maxRetries)
                {
                    var delay = baseDelayMs * (int)Math.Pow(2, attempt - 1);
                    _logger.LogWarning(
                        "BlobWriter: 409 conflict for {Blob}, attempt {Attempt}/{Max}, retrying in {Delay}ms",
                        blobName, attempt, maxRetries, delay);
                    await Task.Delay(delay).ConfigureAwait(false);
                }
            }

            // Unreachable in normal flow: a final 409 falls through the `when` filter and is rethrown above.
            throw new BlobWriterException($"Exhausted 409 retries for blob {blobName}")
            {
                Operation = "CreateBlobAndGetOutputStreamAsync",
                BlobName = blobName,
                UserId = userId
            };
        }

        public async Task<bool> BlobExistsAsync(string userId, string blobName)
        {
            var containerClient = GetInitializedContainerClient(userId, "BlobExistsAsync", blobName);
            var blobClient = containerClient.GetBlobClient(blobName);
            return await blobClient.ExistsAsync().ConfigureAwait(false);
        }


        public async Task<Stream> ReadBlobAsStreamAsync(string userId, string blobName)
        {
            var containerClient = GetInitializedContainerClient(userId, "ReadBlobAsStreamAsync", blobName);

            try
            {
                var blobClient = containerClient.GetBlobClient(blobName);
                return await blobClient.OpenReadAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new BlobWriterException($"Failed to read blob as stream for userId: {userId}, blobName: {blobName}", ex)
                {
                    Operation = "ReadBlobAsStreamAsync",
                    BlobName = blobName,
                    UserId = userId
                };
            }
        }


        public async Task<bool> DeleteBlobAsync(string userId, string blobName)
        {
            if (string.IsNullOrEmpty(userId))
            {
                _logger.LogWarning("UserId cannot be null or empty");
                return false;
            }

            if (string.IsNullOrEmpty(blobName))
            {
                _logger.LogWarning("BlobName cannot be null or empty for userId: {UserId}", userId);
                return false;
            }

            var containerClient = GetInitializedContainerClient(userId, "DeleteBlobAsync", blobName);
            var blobClient = containerClient.GetBlobClient(blobName);
            return await blobClient.DeleteIfExistsAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Generates a SAS token for the specified blob.
        /// </summary>
        /// <param name="userId">The user ID.</param>
        /// <param name="blobName">The name of the blob.</param>
        /// <param name="expiryTime">The expiry time for the SAS token.</param>
        /// <returns>The SAS token URL for the blob.</returns>
        public async Task<string> GenerateSasTokenAsync(string userId, string blobName, TimeSpan expiryTime)
        {
            if (string.IsNullOrEmpty(blobName))
            {
                throw new ArgumentException("BlobName cannot be null or empty", nameof(blobName));
            }

            var containerClient = GetInitializedContainerClient(userId, "GenerateSasTokenAsync", blobName);

            try
            {
                var blobClient = containerClient.GetBlobClient(blobName);
                var sasBuilder = new BlobSasBuilder
                {
                    BlobContainerName = containerClient.Name,
                    BlobName = blobName,
                    Resource = "b",
                    StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5), // Start 5 minutes ago to account for clock skew
                    ExpiresOn = DateTimeOffset.UtcNow.Add(expiryTime)
                };
                sasBuilder.SetPermissions(BlobSasPermissions.Read | BlobSasPermissions.Delete);

                if (UsesMI)
                {
                    // Reuse a cached user delegation key; refreshing per call would add a
                    // management-plane round-trip per SAS at high request rates.
                    var userDelegationKey = await GetOrRefreshUserDelegationKeyAsync().ConfigureAwait(false);

                    // Generate the SAS token using the user delegation key
                    var sasQueryParameters = sasBuilder.ToSasQueryParameters(userDelegationKey, _blobServiceClient.AccountName);

                    // Construct the full SAS URI
                    var blobUriBuilder = new BlobUriBuilder(blobClient.Uri)
                    {
                        Sas = sasQueryParameters
                    };

                    var sasUri = blobUriBuilder.ToUri();
                    _logger.LogDebug("Successfully generated user delegation SAS token for blob {BlobName}", blobName);
                    return sasUri.ToString();

                }
                else
                {
                    // Check if we can use account SAS (when using connection string)
                    if (blobClient.CanGenerateSasUri)
                    {
                        var sasUri = blobClient.GenerateSasUri(sasBuilder);
                        _logger.LogDebug("Successfully generated account SAS token for blob {BlobName}", blobName);
                        return sasUri.ToString();
                    }
                    else
                    {
                        throw new InvalidOperationException("Cannot generate SAS token. Either enable managed identity (UsesMI=true) or provide a connection string with account keys.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate SAS token for blob {BlobName} in container {ContainerName}", blobName, containerClient.Name);
                throw new BlobWriterException($"Failed to generate SAS token for blob {blobName} in container {containerClient.Name}", ex)
                {
                    Operation = "GenerateSasTokenAsync",
                    BlobName = blobName,
                    ContainerName = containerClient.Name,
                    UserId = userId
                };
            }
        }

        /// <summary>
        /// Gets the base URI for a blob without SAS token.
        /// </summary>
        /// <param name="userId">The user ID.</param>
        /// <param name="blobName">The name of the blob.</param>
        /// <returns>The base URI of the blob.</returns>
        public string GetBlobUri(string userId, string blobName)
        {
            if (string.IsNullOrEmpty(blobName))
            {
                throw new ArgumentException("BlobName cannot be null or empty", nameof(blobName));
            }

            var containerClient = GetInitializedContainerClient(userId, "GetBlobUri", blobName);
            var blobClient = containerClient.GetBlobClient(blobName);
            return blobClient.Uri.ToString();
        }

        // Returns a cached user delegation key, refreshing it shortly before expiry. Single-flight
        // protected via SemaphoreSlim so a refresh storm cannot fan out.
        private async Task<Azure.Storage.Blobs.Models.UserDelegationKey> GetOrRefreshUserDelegationKeyAsync()
        {
            var now = DateTimeOffset.UtcNow;
            if (_hasCachedDelegationKey && now < _delegationKeyRefreshAfter)
            {
                return _cachedDelegationKey;
            }

            await _delegationKeyLock.WaitAsync().ConfigureAwait(false);
            try
            {
                now = DateTimeOffset.UtcNow;
                if (_hasCachedDelegationKey && now < _delegationKeyRefreshAfter)
                {
                    return _cachedDelegationKey;
                }

                _logger.LogDebug("Requesting user delegation key for SAS token generation");
                var start = now.AddMinutes(-5); // tolerate clock skew
                var expiry = now.Add(DelegationKeyLifetime);
                var response = await _blobServiceClient
                    .GetUserDelegationKeyAsync(start, expiry)
                    .ConfigureAwait(false);

                _cachedDelegationKey = response.Value;
                _delegationKeyRefreshAfter = expiry - DelegationKeyRefreshSkew;
                _hasCachedDelegationKey = true;
                return _cachedDelegationKey;
            }
            finally
            {
                _delegationKeyLock.Release();
            }
        }

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
            if (!_disposed)
            {
                if (disposing)
                {
                }
                _disposed = true;
            }
        }
        

    }
}