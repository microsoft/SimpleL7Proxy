using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using System.Text;

namespace EventHubToStorage;

/// <summary>
/// Handles writing data to Azure Blob Storage
/// </summary>
public class BlobWriter
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly ILogger? _logger;

    public bool UsesMI { get; set; }

    private BlobWriter(BlobServiceClient blobServiceClient, ILogger? logger = null)
    {
        _blobServiceClient = blobServiceClient;
        _logger = logger;
    }

    /// <summary>
    /// Creates a BlobWriter using managed identity authentication
    /// </summary>
    public static BlobWriter CreateWithManagedIdentity(string storageAccountUri, ILogger? logger = null)
    {
        try
        {
            var blobServiceUri = new Uri(storageAccountUri);
            var credential = new DefaultAzureCredential();
            var blobServiceClient = new BlobServiceClient(blobServiceUri, credential);

            var blobWriter = new BlobWriter(blobServiceClient, logger)
            {
                UsesMI = true
            };

            Console.WriteLine($"[INIT] ✓ BlobServiceClient created successfully with managed identity - URI: {storageAccountUri}");
            return blobWriter;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to create BlobServiceClient with managed identity: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Writes content to a blob in the specified container
    /// </summary>
    /// <param name="blobPath">Full path including container and blob name (e.g., "container/path/to/blob.json")</param>
    /// <param name="content">Content to write</param>
    public async Task WriteAsync(string blobPath, string content)
    {
        try
        {
            // Parse the blob path to extract container name and blob name
            // Format: "container/path/to/file.json"
            var parts = blobPath.Split('/', 2);
            if (parts.Length < 2)
            {
                throw new ArgumentException($"Invalid blob path format. Expected 'container/path', got: {blobPath}");
            }

            var containerName = parts[0];
            var blobName = parts[1];

            var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);

            // Ensure container exists
            await containerClient.CreateIfNotExistsAsync();

            var blobClient = containerClient.GetBlobClient(blobName);

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
            await blobClient.UploadAsync(stream, overwrite: true);

            Console.WriteLine($"[BlobWriter] ✓ Wrote {content.Length} bytes to {blobPath}");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, $"[BlobWriter] Failed to write to blob: {blobPath}");
            Console.WriteLine($"[BlobWriter] ✗ Error writing to {blobPath}: {ex.Message}");
            throw;
        }
    }
}
