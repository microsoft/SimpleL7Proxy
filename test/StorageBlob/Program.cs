using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System.Text;

// Configuration — set via environment variables or edit inline
var storageAccountName = Environment.GetEnvironmentVariable("STORAGE_ACCOUNT_NAME") ?? "mystorageaccount";
var containerName = Environment.GetEnvironmentVariable("STORAGE_CONTAINER_NAME") ?? "sample-container";
var connectionString = Environment.GetEnvironmentVariable("STORAGE_CONNECTION_STRING");

Console.WriteLine($"[CONFIG] Storage Account: {storageAccountName}");
Console.WriteLine($"[CONFIG] Container Name: {containerName}");
Console.WriteLine($"[CONFIG] Connection String: {(string.IsNullOrEmpty(connectionString) ? "NOT SET" : "SET")}");

BlobServiceClient blobServiceClient;

if (!string.IsNullOrEmpty(connectionString)) {
    // Use connection string (local dev / emulator)
    blobServiceClient = new BlobServiceClient(connectionString);
    Console.WriteLine("[INIT] Using connection string authentication");
} else {
    // Use DefaultAzureCredential (managed identity, az cli, etc.)
    var uri = new Uri($"https://{storageAccountName}.blob.core.windows.net");
    blobServiceClient = new BlobServiceClient(uri, new DefaultAzureCredential());
    Console.WriteLine($"[INIT] Using DefaultAzureCredential for {storageAccountName}");
}

// Create the container if it doesn't exist
var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
await containerClient.CreateIfNotExistsAsync(PublicAccessType.None);
Console.WriteLine($"[CONTAINER] Ensured container '{containerName}' exists");

// Upload a sample blob
var blobName = $"sample-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt";
var blobClient = containerClient.GetBlobClient(blobName);

var content = $"Hello from StorageBlob test at {DateTime.UtcNow:O}";
using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
await blobClient.UploadAsync(stream, overwrite: true);
Console.WriteLine($"[UPLOAD] Uploaded blob '{blobName}' ({content.Length} bytes)");

// Verify by reading it back
BlobDownloadInfo download = await blobClient.DownloadAsync();
using var reader = new StreamReader(download.Content);
var downloaded = await reader.ReadToEndAsync();
Console.WriteLine($"[DOWNLOAD] Read back: {downloaded}");

// List blobs in the container
Console.WriteLine($"[LIST] Blobs in '{containerName}':");
await foreach (BlobItem item in containerClient.GetBlobsAsync()) {
    Console.WriteLine($"  - {item.Name} ({item.Properties.ContentLength} bytes)");
}

Console.WriteLine("[DONE] Storage blob sample complete");
