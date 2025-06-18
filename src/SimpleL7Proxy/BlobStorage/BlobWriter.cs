using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SimpleL7Proxy.Backend;
using System.Collections.Concurrent;

namespace SimpleL7Proxy.BlobStorage
{
    /// <summary>
    /// Provides methods for writing to Azure Blob Storage.
    /// </summary>
    public class BlobWriter
    {
        private readonly ConcurrentDictionary< string, BlobContainerClient> _containerClients = new();
        //private readonly BlobContainerClient _containerClient = null!;

        public  BlobServiceClient _blobServiceClient = null!;
        private readonly IOptionsMonitor<BackendOptions> _optionsMonitor;

        /// <summary>
        /// Initializes a new instance of the <see cref="BlobWriter"/> class.
        /// </summary>
        /// <param name="connectionString">The Azure Storage connection string.</param>
        /// <param name="containerName">The name of the blob container.</param>
        public BlobWriter(IOptionsMonitor<BackendOptions> optionsMonitor)
        {
            _optionsMonitor = optionsMonitor;
            
            if (optionsMonitor.CurrentValue.AsyncModeEnabled)
            {
                _blobServiceClient = new BlobServiceClient(optionsMonitor.CurrentValue.AsyncBlobStorageConnectionString);
                //_containerClient = blobServiceClient.GetBlobContainerClient(containerName);
            }
        }

        public bool initClient(string userId, string containerName)
        {

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
                throw new InvalidOperationException($"Failed to initialize BlobContainerClient for userId {userId}");
            }

            // Only create the container if it does not exist. This is thread-safe and efficient for concurrent calls.
            await _containerClient.CreateIfNotExistsAsync().ConfigureAwait(false);

            var blobClient = _containerClient.GetBlobClient(blobName);

            // OpenWriteAsync will create the blob if it does not exist and return a writable stream.
            return await blobClient.OpenWriteAsync(overwrite: true).ConfigureAwait(false);
        }

        /// <summary>
        /// Generates a SAS token for the specified blob.
        /// </summary>
        /// <param name="blobName">The name of the blob.</param>
        /// <param name="expiryTime">The expiry time for the SAS token.</param>
        /// <returns>The SAS token URL for the blob.</returns>
        public string GenerateSasToken(string userId, string blobName, TimeSpan expiryTime)
        {
            // Get the client for the userId
            if (!_containerClients.TryGetValue(userId, out var _containerClient))
            {
                throw new InvalidOperationException($"Failed to initialize BlobContainerClient for userId {userId}");
            }

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
    }
}