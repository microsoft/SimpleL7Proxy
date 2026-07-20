using System.Text.Json;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Azure.Cosmos;

namespace chat_tester.Components.Shared;

/// <summary>
/// Shared storage-backend helpers used by the chat history and conversation stores to
/// build blob clients, resolve on-disk directories, and normalize Cosmos DB endpoints.
/// </summary>
internal static class StorageBackend
{
    /// <summary>Creates a blob container client for the given account/container using managed identity.</summary>
    public static BlobContainerClient CreateBlobContainerClient(string storageAccountName, string blobContainerName)
    {
        var accountName = storageAccountName.Trim();
        var containerName = blobContainerName.Trim();
        var containerUri = new Uri($"https://{accountName}.blob.core.windows.net/{containerName}");
        return new BlobContainerClient(containerUri, new DefaultAzureCredential());
    }

    /// <summary>
    /// Resolves the on-disk directory for a store, falling back to <paramref name="defaultRelativePath"/>
    /// when no disk path is configured and rooting relative paths under the content root.
    /// </summary>
    public static string ResolveDiskDirectory(string contentRootPath, string diskPath, string defaultRelativePath)
    {
        var configuredPath = string.IsNullOrWhiteSpace(diskPath) ? defaultRelativePath : diskPath;

        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(contentRootPath, configuredPath);
    }

    /// <summary>Normalizes a Cosmos DB account name or URI into a fully-qualified endpoint.</summary>
    public static string BuildCosmosEndpoint(string account)
    {
        if (account.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || account.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return account;
        }

        return $"https://{account}.documents.azure.com:443/";
    }
}

/// <summary>
/// Caches a <see cref="CosmosClient"/> keyed by resolved endpoint and provisions the requested
/// database/container on demand. Mirrors the previous per-store caching behavior.
/// </summary>
internal sealed class CosmosContainerProvider
{
    private const string PartitionKeyPath = "/partitionKey";

    private CosmosClient? _cosmosClient;
    private string _cosmosAccount = string.Empty;

    /// <summary>Returns the requested container, creating the client, database, and container if needed.</summary>
    public async Task<Container> GetContainerAsync(string account, string database, string container)
    {
        var endpoint = StorageBackend.BuildCosmosEndpoint(account);
        if (_cosmosClient is null || !string.Equals(_cosmosAccount, endpoint, StringComparison.OrdinalIgnoreCase))
        {
            _cosmosClient?.Dispose();
            _cosmosClient = new CosmosClient(endpoint, new DefaultAzureCredential(), new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Direct
            });
            _cosmosAccount = endpoint;
        }

        var databaseResponse = await _cosmosClient.CreateDatabaseIfNotExistsAsync(database);
        var containerResponse = await databaseResponse.Database.CreateContainerIfNotExistsAsync(container, PartitionKeyPath);
        return containerResponse.Container;
    }
}

/// <summary>
/// A persisted document identified by an id and a Cosmos/blob partition key. Implemented by the
/// entity types stored through <see cref="DocumentStorageRepository{T}"/>.
/// </summary>
internal interface IStorageDocument
{
    string Id { get; }

    string PartitionKey { get; }
}

/// <summary>
/// The storage location configuration shared by the chat history and conversation stores.
/// Selects the backend (disk, blob, or Cosmos DB) and carries the per-backend connection details.
/// </summary>
internal interface IStorageSettings
{
    string Mode { get; }

    string DiskPath { get; }

    string StorageAccountName { get; }

    string BlobContainerName { get; }

    string CosmosAccount { get; }

    string CosmosDatabase { get; }

    string CosmosContainer { get; }
}

/// <summary>
/// Generic disk/blob/Cosmos persistence for a document type. Encapsulates the backend selection,
/// client creation, and empty-configuration guards that were previously duplicated across the
/// chat history and conversation stores. Normalization and in-memory shaping remain the caller's
/// responsibility so entity-specific rules stay in their owning store.
/// </summary>
internal sealed class DocumentStorageRepository<T> where T : class, IStorageDocument
{
    private const string DocumentQuery = "SELECT * FROM c WHERE c.documentType = @documentType ORDER BY c.createdAt";

    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string _contentRootPath;
    private readonly string _documentType;
    private readonly string _defaultDiskRelativePath;
    private readonly Func<T, string> _buildRelativePath;
    private readonly Predicate<string>? _diskFileSkip;
    private readonly Func<string, Task<IReadOnlyList<T>>>? _diskLegacyLoader;
    private readonly CosmosContainerProvider _cosmosContainers = new();

    /// <summary>Creates a repository for the given document type.</summary>
    /// <param name="jsonOptions">Serializer options used for all backends.</param>
    /// <param name="contentRootPath">Application content root used to resolve relative disk paths.</param>
    /// <param name="documentType">Cosmos <c>documentType</c> discriminator queried on load.</param>
    /// <param name="defaultDiskRelativePath">Disk directory used when no disk path is configured.</param>
    /// <param name="buildRelativePath">Builds the per-document storage path (blob name / disk file).</param>
    /// <param name="diskFileSkip">Optional filter for file names to exclude from the standard disk scan.</param>
    /// <param name="diskLegacyLoader">Optional loader for legacy on-disk formats, merged after the standard scan.</param>
    public DocumentStorageRepository(
        JsonSerializerOptions jsonOptions,
        string contentRootPath,
        string documentType,
        string defaultDiskRelativePath,
        Func<T, string> buildRelativePath,
        Predicate<string>? diskFileSkip = null,
        Func<string, Task<IReadOnlyList<T>>>? diskLegacyLoader = null)
    {
        _jsonOptions = jsonOptions;
        _contentRootPath = contentRootPath;
        _documentType = documentType;
        _defaultDiskRelativePath = defaultDiskRelativePath;
        _buildRelativePath = buildRelativePath;
        _diskFileSkip = diskFileSkip;
        _diskLegacyLoader = diskLegacyLoader;
    }

    /// <summary>Resolves the on-disk directory for the configured disk path.</summary>
    public string ResolveDiskDirectory(string diskPath) =>
        StorageBackend.ResolveDiskDirectory(_contentRootPath, diskPath, _defaultDiskRelativePath);

    /// <summary>Loads all documents from the backend selected by <paramref name="settings"/>.</summary>
    public Task<IReadOnlyList<T>> LoadAsync(IStorageSettings settings) => settings.Mode switch
    {
        HistoryStorageMode.BlobStorage => LoadFromBlobStorageAsync(settings),
        HistoryStorageMode.CosmosDb => LoadFromCosmosAsync(settings),
        _ => LoadFromDiskAsync(settings)
    };

    /// <summary>Persists a single document to the backend selected by <paramref name="settings"/>.</summary>
    public Task SaveAsync(IStorageSettings settings, T document) => settings.Mode switch
    {
        HistoryStorageMode.BlobStorage => SaveToBlobStorageAsync(settings, document),
        HistoryStorageMode.CosmosDb => SaveToCosmosAsync(settings, document),
        _ => SaveToDiskAsync(settings, document)
    };

    /// <summary>Removes a single document from the backend selected by <paramref name="settings"/>.</summary>
    public Task DeleteAsync(IStorageSettings settings, T document) => settings.Mode switch
    {
        HistoryStorageMode.BlobStorage => DeleteFromBlobStorageAsync(settings, document),
        HistoryStorageMode.CosmosDb => DeleteFromCosmosAsync(settings, document),
        _ => DeleteFromDiskAsync(settings, document)
    };

    private async Task<IReadOnlyList<T>> LoadFromDiskAsync(IStorageSettings settings)
    {
        var documents = new List<T>();
        var directory = ResolveDiskDirectory(settings.DiskPath);
        Directory.CreateDirectory(directory);

        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
        {
            if (_diskFileSkip is not null && _diskFileSkip(Path.GetFileName(file)))
            {
                continue;
            }

            var document = JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(file), _jsonOptions);
            if (document is not null)
            {
                documents.Add(document);
            }
        }

        if (_diskLegacyLoader is not null)
        {
            documents.AddRange(await _diskLegacyLoader(directory));
        }

        return documents;
    }

    private async Task<IReadOnlyList<T>> LoadFromBlobStorageAsync(IStorageSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.StorageAccountName) || string.IsNullOrWhiteSpace(settings.BlobContainerName))
        {
            return Array.Empty<T>();
        }

        var documents = new List<T>();
        var container = CreateBlobContainerClient(settings);
        await container.CreateIfNotExistsAsync();
        await foreach (var blobItem in container.GetBlobsAsync())
        {
            if (!blobItem.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var blob = container.GetBlobClient(blobItem.Name);
            var response = await blob.DownloadContentAsync();
            var document = response.Value.Content.ToObjectFromJson<T>(_jsonOptions);
            if (document is not null)
            {
                documents.Add(document);
            }
        }

        return documents;
    }

    private async Task<IReadOnlyList<T>> LoadFromCosmosAsync(IStorageSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.CosmosAccount) || string.IsNullOrWhiteSpace(settings.CosmosDatabase) || string.IsNullOrWhiteSpace(settings.CosmosContainer))
        {
            return Array.Empty<T>();
        }

        var container = await GetCosmosContainerAsync(settings);
        var documents = new List<T>();
        using var iterator = container.GetItemQueryIterator<T>(
            new QueryDefinition(DocumentQuery).WithParameter("@documentType", _documentType));

        while (iterator.HasMoreResults)
        {
            foreach (var document in await iterator.ReadNextAsync())
            {
                documents.Add(document);
            }
        }

        return documents;
    }

    private async Task SaveToDiskAsync(IStorageSettings settings, T document)
    {
        var path = Path.Combine(ResolveDiskDirectory(settings.DiskPath), _buildRelativePath(document));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, document, _jsonOptions);
    }

    private async Task SaveToBlobStorageAsync(IStorageSettings settings, T document)
    {
        if (string.IsNullOrWhiteSpace(settings.StorageAccountName) || string.IsNullOrWhiteSpace(settings.BlobContainerName))
        {
            return;
        }

        var container = CreateBlobContainerClient(settings);
        await container.CreateIfNotExistsAsync();
        var blob = container.GetBlobClient(_buildRelativePath(document).Replace(Path.DirectorySeparatorChar, '/'));
        await using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(document, _jsonOptions));
        await blob.UploadAsync(stream, overwrite: true);
    }

    private async Task SaveToCosmosAsync(IStorageSettings settings, T document)
    {
        if (string.IsNullOrWhiteSpace(settings.CosmosAccount) || string.IsNullOrWhiteSpace(settings.CosmosDatabase) || string.IsNullOrWhiteSpace(settings.CosmosContainer))
        {
            return;
        }

        var container = await GetCosmosContainerAsync(settings);
        await container.UpsertItemAsync(document, new PartitionKey(document.PartitionKey));
    }

    private Task DeleteFromDiskAsync(IStorageSettings settings, T document)
    {
        var directory = ResolveDiskDirectory(settings.DiskPath);
        foreach (var file in Directory.EnumerateFiles(directory, $"{document.Id}.json", SearchOption.AllDirectories))
        {
            File.Delete(file);
        }

        return Task.CompletedTask;
    }

    private async Task DeleteFromBlobStorageAsync(IStorageSettings settings, T document)
    {
        if (string.IsNullOrWhiteSpace(settings.StorageAccountName) || string.IsNullOrWhiteSpace(settings.BlobContainerName))
        {
            return;
        }

        var container = CreateBlobContainerClient(settings);
        await container.GetBlobClient(_buildRelativePath(document).Replace(Path.DirectorySeparatorChar, '/')).DeleteIfExistsAsync();
    }

    private async Task DeleteFromCosmosAsync(IStorageSettings settings, T document)
    {
        if (string.IsNullOrWhiteSpace(settings.CosmosAccount) || string.IsNullOrWhiteSpace(settings.CosmosDatabase) || string.IsNullOrWhiteSpace(settings.CosmosContainer))
        {
            return;
        }

        var container = await GetCosmosContainerAsync(settings);
        await container.DeleteItemAsync<T>(document.Id, new PartitionKey(document.PartitionKey));
    }

    private Task<Container> GetCosmosContainerAsync(IStorageSettings settings) =>
        _cosmosContainers.GetContainerAsync(settings.CosmosAccount, settings.CosmosDatabase, settings.CosmosContainer);

    private static BlobContainerClient CreateBlobContainerClient(IStorageSettings settings) =>
        StorageBackend.CreateBlobContainerClient(settings.StorageAccountName, settings.BlobContainerName);
}
