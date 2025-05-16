using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using System.IO;
using System.Threading.Tasks;

namespace SimpleL7Proxy.BlobStorage
{
    /// <summary>
    /// Provides methods for writing to Azure Blob Storage.
    /// </summary>
    public class BlobWriter
    {
        private readonly BlobContainerClient _containerClient;

        /// <summary>
        /// Initializes a new instance of the <see cref="BlobWriter"/> class.
        /// </summary>
        /// <param name="connectionString">The Azure Storage connection string.</param>
        /// <param name="containerName">The name of the blob container.</param>
        public BlobWriter(string connectionString, string containerName)
        {
            var blobServiceClient = new BlobServiceClient(connectionString);
            _containerClient = blobServiceClient.GetBlobContainerClient(containerName);
        }

        /// <summary>
        /// Creates the blob container if it does not exist and returns an output stream for the specified blob.
        /// </summary>
        /// <param name="blobName">The name of the blob.</param>
        /// <returns>A writable stream to the blob.</returns>
        public async Task<Stream> CreateBlobAndGetOutputStreamAsync(string blobName)
        {
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
        public string GenerateSasToken(string blobName, TimeSpan expiryTime)
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
    }
}