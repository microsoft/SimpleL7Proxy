using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Concurrent;

using SimpleL7Proxy.Backend;

// Review DISPOSAL_ARCHITECTURE.MD in the root for details on disposal flow

namespace SimpleL7Proxy.BlobStorage
{
    /// <summary>
    /// Provides methods for writing to Azure Blob Storage.
    /// </summary>
    public class BlobWriter : IBlobWriter, IDisposable
    {
        private static readonly ConcurrentDictionary<string, BlobContainerClient> _containerClients = new();
        //private readonly BlobContainerClient _containerClient = null!;

        private readonly BlobServiceClient _blobServiceClient;
        private readonly ILogger<BlobWriter> _logger;

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
            // Check if the client for this userId already exists
            // Should we check if the writer is valid ? 
            if (_containerClients.ContainsKey(userId))
            {
                // Client already exists, no need to create a new one
                return true;
            }
            _logger.LogDebug("BlobWriter: Initializing for UserId: {UserId}, BlobContainerName: {BlobContainerName}", userId, containerName);

            try
            {
                var client = _blobServiceClient.GetBlobContainerClient(containerName);
                // Ensure container exists
                await client.CreateIfNotExistsAsync().ConfigureAwait(false);

                if (_containerClients.TryAdd(userId, client))
                {
                    // Successfully added the client to the dictionary
                    return true;
                }
            }
            catch (Exception ex)
            {

                throw new BlobWriterException($"Failed to initialize BlobContainerClient for userId: {userId}, containerName: {containerName}", ex)
                {
                    Operation = "InitClientAsync",
                    ContainerName = containerName,
                    UserId = userId
                };
                // Log the exception or handle it as needed
                //Console.WriteLine($"Error initializing BlobContainerClient for userId {userId}: {ex.Message}");

            }

            return false;
        }

        /// <summary>
        /// Creates the blob container if it does not exist and returns an output stream for the specified blob.
        /// </summary>
        /// <param name="blobName">The name of the blob.</param>
        /// <returns>A writable stream to the blob.</returns>
        public async Task<Stream> CreateBlobAndGetOutputStreamAsync(string userId, string blobName)
        {

            // Get the client for the userId
            if (!_containerClients.TryGetValue(userId, out var _containerClient))
            {
                throw new BlobWriterException($"BlobContainerClient not initialized for userId: {userId}. Call InitializeClientAsync first.")
                {
                    Operation = "CreateBlobAndGetOutputStreamAsync",
                    BlobName = blobName,
                    UserId = userId
                };
            }

            // Only create the container if it does not exist. This is thread-safe and efficient for concurrent calls.
            //await _containerClient.CreateIfNotExistsAsync().ConfigureAwait(false);

            var blobClient = _containerClient.GetBlobClient(blobName);

            // OpenWriteAsync will create the blob if it does not exist and return a writable stream.
            return await blobClient.OpenWriteAsync(overwrite: true).ConfigureAwait(false);
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

            // Get the client for the userId
            if (!_containerClients.TryGetValue(userId, out var _containerClient))
            {
                throw new InvalidOperationException($"BlobContainerClient not initialized for userId: {userId}. Call InitializeClientAsync first.");
            }

            var blobClient = _containerClient.GetBlobClient(blobName);
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

            // Get the client for the userId
            if (!_containerClients.TryGetValue(userId, out var _containerClient))
            {
                throw new BlobWriterException($"BlobContainerClient not initialized for userId: {userId}. Call InitializeClientAsync first.")
                {
                    Operation = "GenerateSasTokenAsync",
                    BlobName = blobName,
                    UserId = userId
                };
            }

            try
            {
                var blobClient = _containerClient.GetBlobClient(blobName);
                var sasBuilder = new BlobSasBuilder
                {
                    BlobContainerName = _containerClient.Name,
                    BlobName = blobName,
                    Resource = "b",
                    StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5), // Start 5 minutes ago to account for clock skew
                    ExpiresOn = DateTimeOffset.UtcNow.Add(expiryTime)
                };
                sasBuilder.SetPermissions(BlobSasPermissions.Read | BlobSasPermissions.Delete);

                if (UsesMI)
                {
                    // Get a user delegation key for the Blob service that's valid for 1 hour
                    var delegationKeyStartTime = DateTimeOffset.UtcNow;
                    var delegationKeyExpiryTime = delegationKeyStartTime.Add(TimeSpan.FromHours(1));

                    _logger.LogDebug("Requesting user delegation key for SAS token generation");
                    var userDelegationKey = await _blobServiceClient
                        .GetUserDelegationKeyAsync(delegationKeyStartTime, delegationKeyExpiryTime)
                        .ConfigureAwait(false);

                    // Generate the SAS token using the user delegation key
                    var sasQueryParameters = sasBuilder.ToSasQueryParameters(userDelegationKey.Value, _blobServiceClient.AccountName);

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
                _logger.LogError(ex, "Failed to generate SAS token for blob {BlobName} in container {ContainerName}", blobName, _containerClient.Name);
                throw new BlobWriterException($"Failed to generate SAS token for blob {blobName} in container {_containerClient.Name}", ex)
                {
                    Operation = "GenerateSasTokenAsync",
                    BlobName = blobName,
                    ContainerName = _containerClient.Name,
                    UserId = userId
                };
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