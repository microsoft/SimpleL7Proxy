using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.IO;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Concurrent;

using SimpleL7Proxy.Backend;

namespace SimpleL7Proxy.BlobStorage
{
    /// <summary>
    /// Provides methods for writing to Azure Blob Storage.
    /// </summary>
    public class BlobWriter : IBlobWriter
    {
        private static readonly ConcurrentDictionary<string, BlobContainerClient> _containerClients = new();
        //private readonly BlobContainerClient _containerClient = null!;

        private readonly BlobServiceClient _blobServiceClient;
        private readonly ILogger<BlobWriter> _logger;

        public bool IsInitialized => _blobServiceClient != null;

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
                // Log the exception or handle it as needed
                Console.WriteLine($"Error initializing BlobContainerClient for userId {userId}: {ex.Message}");

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
                throw new InvalidOperationException($"BlobContainerClient not initialized for userId: {userId}. Call InitializeClientAsync first.");
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
        /// <param name="blobName">The name of the blob.</param>
        /// <param name="expiryTime">The expiry time for the SAS token.</param>
        /// <returns>The SAS token URL for the blob.</returns>
        public string GenerateSasToken(string userId, string blobName, TimeSpan expiryTime)
        {
            if (string.IsNullOrEmpty(blobName))
            {
                throw new ArgumentException("BlobName cannot be null or empty", nameof(blobName));
            }

            // Get the client for the userId
            if (!_containerClients.TryGetValue(userId, out var _containerClient))
            {
                throw new InvalidOperationException($"BlobContainerClient not initialized for userId: {userId}. Call InitializeClientAsync first.");
            }

            try
            {
                var blobClient = _containerClient.GetBlobClient(blobName);

                var sasBuilder = new BlobSasBuilder
                {
                    BlobContainerName = _containerClient.Name,
                    BlobName = blobName,
                    Resource = "b",
                    ExpiresOn = DateTimeOffset.UtcNow.Add(expiryTime)
                };
                sasBuilder.SetPermissions(BlobSasPermissions.Read | BlobSasPermissions.Delete);

                var sasUri = blobClient.GenerateSasUri(sasBuilder);
                return sasUri.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate SAS token for blob {BlobName} in container {ContainerName}", blobName, _containerClient.Name);
                throw;
            }
        }
    }
}