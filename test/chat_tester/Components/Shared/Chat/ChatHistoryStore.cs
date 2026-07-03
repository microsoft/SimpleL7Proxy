using System.Text.Json;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Azure.Cosmos;

namespace chat_tester.Components.Shared;

public sealed class ChatHistoryStore
{
    private const string DefaultDataDirectoryName = "data";
    private const string DefaultHistoryDirectoryName = "history";
    private const string LegacyHistoryFileName = "chat-history.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly IWebHostEnvironment _environment;
    private readonly HistorySettings _historySettings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<ChatHistoryEntry> _entries = new();
    private ChatHistoryEntry[] _snapshot = [];

    private HistoryStorageSettings _settings = new();
    private CosmosClient? _cosmosClient;
    private string _cosmosAccount = string.Empty;

    public ChatHistoryStore(IWebHostEnvironment environment, HistorySettings historySettings)
    {
        _environment = environment;
        _historySettings = historySettings;
        _settings = NormalizeSettings(historySettings.Current);
    }

    public int Count => _snapshot.Length;

    public bool HasEntries => _snapshot.Length > 0;

    public IReadOnlyList<ChatHistoryEntry> GetSnapshot() => _snapshot;

    public HistoryStorageSettings Settings => CloneSettings(_settings);

    public string StorageDescription => _settings.Mode switch
    {
        HistoryStorageMode.BlobStorage => string.IsNullOrWhiteSpace(_settings.StorageAccountName) ? "Storage account name not configured" : $"{_settings.StorageAccountName} / {_settings.BlobContainerName}",
        HistoryStorageMode.CosmosDb => string.IsNullOrWhiteSpace(_settings.CosmosAccount) ? "Cosmos DB account not configured" : $"{_settings.CosmosAccount} / {_settings.CosmosDatabase} / {_settings.CosmosContainer}",
        _ => ResolveDiskDirectory(_settings.DiskPath)
    };

    public string LastStorageError { get; private set; } = string.Empty;

    public async Task ReloadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _settings = NormalizeSettings(_historySettings.Current);
            _historySettings.Apply(_settings);
            _entries.Clear();
            _entries.AddRange(await LoadCoreAsync(_settings));
            RefreshSnapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplySettingsAsync(HistoryStorageSettings settings)
    {
        await _gate.WaitAsync();
        try
        {
            _settings = NormalizeSettings(settings);
            _historySettings.Apply(_settings);
            LastStorageError = string.Empty;
            _entries.Clear();
            _entries.AddRange(await LoadCoreAsync(_settings));
            RefreshSnapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RefreshAsync()
    {
        await _gate.WaitAsync();
        try
        {
            LastStorageError = string.Empty;
            _entries.Clear();
            _entries.AddRange(await LoadCoreAsync(_settings));
            RefreshSnapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<ChatHistoryEntry> AddRequest(RequestHistoryEntry request) =>
        AddCoreAsync(ChatHistoryEntry.FromRequest(request));

    private async Task<ChatHistoryEntry> AddCoreAsync(ChatHistoryEntry entry)
    {
        await _gate.WaitAsync();
        try
        {
            PrepareEntry(entry);
            _entries.Add(entry);
            _entries.Sort((left, right) => left.CreatedAt.CompareTo(right.CreatedAt));
            RefreshSnapshot();
            try
            {
                await SaveEntryCoreAsync(_settings, entry);
                LastStorageError = string.Empty;
            }
            catch (Exception ex)
            {
                LastStorageError = ex.Message;
            }
            return entry;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        await _gate.WaitAsync();
        try
        {
            var entry = _entries.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            if (entry is null)
            {
                return false;
            }

            await DeleteEntryCoreAsync(_settings, entry);
            _entries.Remove(entry);
            RefreshSnapshot();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<ChatHistoryEntry>> LoadCoreAsync(HistoryStorageSettings settings)
    {
        try
        {
            return settings.Mode switch
            {
                HistoryStorageMode.BlobStorage => await LoadFromBlobStorageAsync(settings),
                HistoryStorageMode.CosmosDb => await LoadFromCosmosAsync(settings),
                _ => await LoadFromDiskAsync(settings)
            };
        }
        catch (Exception ex)
        {
            LastStorageError = ex.Message;
            return Array.Empty<ChatHistoryEntry>();
        }
    }

    private async Task<IReadOnlyList<ChatHistoryEntry>> LoadFromDiskAsync(HistoryStorageSettings settings)
    {
        var entries = new List<ChatHistoryEntry>();
        var directory = ResolveDiskDirectory(settings.DiskPath);
        Directory.CreateDirectory(directory);

        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(file), LegacyHistoryFileName, StringComparison.OrdinalIgnoreCase))
            {
                var legacyEntries = JsonSerializer.Deserialize<List<ChatHistoryEntry>>(await File.ReadAllTextAsync(file), JsonOptions);
                if (legacyEntries is not null)
                {
                    entries.AddRange(legacyEntries);
                }

                continue;
            }

            var entry = JsonSerializer.Deserialize<ChatHistoryEntry>(await File.ReadAllTextAsync(file), JsonOptions);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        var legacyFile = Path.Combine(_environment.ContentRootPath, DefaultDataDirectoryName, LegacyHistoryFileName);
        if (File.Exists(legacyFile) && !legacyFile.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
        {
            var legacyEntries = JsonSerializer.Deserialize<List<ChatHistoryEntry>>(await File.ReadAllTextAsync(legacyFile), JsonOptions);
            if (legacyEntries is not null)
            {
                entries.AddRange(legacyEntries);
            }
        }

        return NormalizeEntries(entries);
    }

    private async Task<IReadOnlyList<ChatHistoryEntry>> LoadFromBlobStorageAsync(HistoryStorageSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.StorageAccountName) || string.IsNullOrWhiteSpace(settings.BlobContainerName))
        {
            return Array.Empty<ChatHistoryEntry>();
        }

        var entries = new List<ChatHistoryEntry>();
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
            var entry = response.Value.Content.ToObjectFromJson<ChatHistoryEntry>(JsonOptions);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return NormalizeEntries(entries);
    }

    private async Task<IReadOnlyList<ChatHistoryEntry>> LoadFromCosmosAsync(HistoryStorageSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.CosmosAccount) || string.IsNullOrWhiteSpace(settings.CosmosDatabase) || string.IsNullOrWhiteSpace(settings.CosmosContainer))
        {
            return Array.Empty<ChatHistoryEntry>();
        }

        var container = await GetCosmosContainerAsync(settings);
        var entries = new List<ChatHistoryEntry>();
        using var iterator = container.GetItemQueryIterator<ChatHistoryEntry>(
            new QueryDefinition("SELECT * FROM c WHERE c.documentType = @documentType ORDER BY c.createdAt")
                .WithParameter("@documentType", nameof(ChatHistoryEntry)));

        while (iterator.HasMoreResults)
        {
            foreach (var entry in await iterator.ReadNextAsync())
            {
                entries.Add(entry);
            }
        }

        return NormalizeEntries(entries);
    }

    private async Task SaveEntryCoreAsync(HistoryStorageSettings settings, ChatHistoryEntry entry)
    {
        switch (settings.Mode)
        {
            case HistoryStorageMode.BlobStorage:
                await SaveToBlobStorageAsync(settings, entry);
                break;
            case HistoryStorageMode.CosmosDb:
                await SaveToCosmosAsync(settings, entry);
                break;
            default:
                await SaveToDiskAsync(settings, entry);
                break;
        }
    }

    private async Task SaveToDiskAsync(HistoryStorageSettings settings, ChatHistoryEntry entry)
    {
        var path = Path.Combine(ResolveDiskDirectory(settings.DiskPath), BuildHistoryPath(entry));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, entry, JsonOptions);
    }

    private async Task SaveToBlobStorageAsync(HistoryStorageSettings settings, ChatHistoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(settings.StorageAccountName) || string.IsNullOrWhiteSpace(settings.BlobContainerName))
        {
            return;
        }

        var container = CreateBlobContainerClient(settings);
        await container.CreateIfNotExistsAsync();
        var blob = container.GetBlobClient(BuildHistoryPath(entry).Replace(Path.DirectorySeparatorChar, '/'));
        await using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions));
        await blob.UploadAsync(stream, overwrite: true);
    }

    private async Task SaveToCosmosAsync(HistoryStorageSettings settings, ChatHistoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(settings.CosmosAccount) || string.IsNullOrWhiteSpace(settings.CosmosDatabase) || string.IsNullOrWhiteSpace(settings.CosmosContainer))
        {
            return;
        }

        var container = await GetCosmosContainerAsync(settings);
        await container.UpsertItemAsync(entry, new PartitionKey(entry.PartitionKey));
    }

    private async Task DeleteEntryCoreAsync(HistoryStorageSettings settings, ChatHistoryEntry entry)
    {
        switch (settings.Mode)
        {
            case HistoryStorageMode.BlobStorage:
                if (!string.IsNullOrWhiteSpace(settings.StorageAccountName) && !string.IsNullOrWhiteSpace(settings.BlobContainerName))
                {
                    var container = CreateBlobContainerClient(settings);
                    await container.GetBlobClient(BuildHistoryPath(entry).Replace(Path.DirectorySeparatorChar, '/')).DeleteIfExistsAsync();
                }
                break;
            case HistoryStorageMode.CosmosDb:
                if (!string.IsNullOrWhiteSpace(settings.CosmosAccount) && !string.IsNullOrWhiteSpace(settings.CosmosDatabase) && !string.IsNullOrWhiteSpace(settings.CosmosContainer))
                {
                    var container = await GetCosmosContainerAsync(settings);
                    await container.DeleteItemAsync<ChatHistoryEntry>(entry.Id, new PartitionKey(entry.PartitionKey));
                }
                break;
            default:
                var directory = ResolveDiskDirectory(settings.DiskPath);
                foreach (var file in Directory.EnumerateFiles(directory, $"{entry.Id}.json", SearchOption.AllDirectories))
                {
                    File.Delete(file);
                }
                break;
        }
    }

    private async Task<Container> GetCosmosContainerAsync(HistoryStorageSettings settings)
    {
        var endpoint = BuildCosmosEndpoint(settings.CosmosAccount);
        if (_cosmosClient is null || !string.Equals(_cosmosAccount, endpoint, StringComparison.OrdinalIgnoreCase))
        {
            _cosmosClient?.Dispose();
            _cosmosClient = new CosmosClient(endpoint, new DefaultAzureCredential(), new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Direct
            });
            _cosmosAccount = endpoint;
        }

        var database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(settings.CosmosDatabase);
        var container = await database.Database.CreateContainerIfNotExistsAsync(settings.CosmosContainer, "/partitionKey");
        return container.Container;
    }

    private static BlobContainerClient CreateBlobContainerClient(HistoryStorageSettings settings)
    {
        var accountName = settings.StorageAccountName.Trim();
        var containerName = settings.BlobContainerName.Trim();
        var containerUri = new Uri($"https://{accountName}.blob.core.windows.net/{containerName}");
        return new BlobContainerClient(containerUri, new DefaultAzureCredential());
    }

    private string ResolveDiskDirectory(string diskPath)
    {
        var configuredPath = string.IsNullOrWhiteSpace(diskPath)
            ? Path.Combine(DefaultDataDirectoryName, DefaultHistoryDirectoryName)
            : diskPath;

        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(_environment.ContentRootPath, configuredPath);
    }

    private static string BuildHistoryPath(ChatHistoryEntry entry)
    {
        var local = entry.CreatedAt.LocalDateTime;
        return Path.Combine(local.ToString("yyyy-MM"), local.ToString("dd"), $"{entry.Id}.json");
    }

    private static string BuildCosmosEndpoint(string account)
    {
        if (account.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || account.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return account;
        }

        return $"https://{account}.documents.azure.com:443/";
    }

    private static void PrepareEntry(ChatHistoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Id))
        {
            entry.Id = Guid.NewGuid().ToString("N");
        }

        if (entry.CreatedAt == default)
        {
            entry.CreatedAt = DateTimeOffset.Now;
        }

        entry.DocumentType = nameof(ChatHistoryEntry);
        entry.PartitionKey = entry.CreatedAt.ToString("yyyy-MM");
        entry.Source = NormalizeSource(entry);
        entry.Method = string.IsNullOrWhiteSpace(entry.Method) ? "POST" : entry.Method;
        entry.Metrics ??= new ChatHistoryMetrics();
    }

    private static string NormalizeSource(ChatHistoryEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Source) && !entry.Source.Equals("Request inspector", StringComparison.OrdinalIgnoreCase))
        {
            return entry.Source;
        }

        if (entry.StatusMessage.Contains("Canceled", StringComparison.OrdinalIgnoreCase))
        {
            return "Abort test";
        }

        if (entry.EndpointPath.Contains("url-test", StringComparison.OrdinalIgnoreCase))
        {
            return "URL tester";
        }

        return string.IsNullOrWhiteSpace(entry.Source) ? "Request inspector" : entry.Source;
    }

    private static IReadOnlyList<ChatHistoryEntry> NormalizeEntries(IEnumerable<ChatHistoryEntry> entries)
    {
        var deduped = new Dictionary<string, ChatHistoryEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            PrepareEntry(entry);
            deduped[entry.Id] = entry;
        }

        return deduped.Values.OrderBy(entry => entry.CreatedAt).ToArray();
    }

    private void RefreshSnapshot()
    {
        _snapshot = _entries.ToArray();
    }

    private static HistoryStorageSettings NormalizeSettings(HistoryStorageSettings? settings)
    {
        var source = settings ?? new HistoryStorageSettings();
        var mode = source.Mode switch
        {
            HistoryStorageMode.BlobStorage => HistoryStorageMode.BlobStorage,
            HistoryStorageMode.CosmosDb => HistoryStorageMode.CosmosDb,
            _ => HistoryStorageMode.Disk
        };

        return new HistoryStorageSettings
        {
            Mode = mode,
            DiskPath = source.DiskPath,
            StorageAccountName = source.StorageAccountName,
            BlobContainerName = string.IsNullOrWhiteSpace(source.BlobContainerName) ? "history" : source.BlobContainerName,
            CosmosAccount = source.CosmosAccount,
            CosmosDatabase = source.CosmosDatabase,
            CosmosContainer = source.CosmosContainer
        };
    }

    private static HistoryStorageSettings CloneSettings(HistoryStorageSettings settings) => new()
    {
        Mode = settings.Mode,
        DiskPath = settings.DiskPath,
        StorageAccountName = settings.StorageAccountName,
        BlobContainerName = settings.BlobContainerName,
        CosmosAccount = settings.CosmosAccount,
        CosmosDatabase = settings.CosmosDatabase,
        CosmosContainer = settings.CosmosContainer
    };
}

public sealed class ChatHistoryEntry
{
    public string Id { get; set; } = string.Empty;

    public string DocumentType { get; set; } = nameof(ChatHistoryEntry);

    public string PartitionKey { get; set; } = string.Empty;

    public string Source { get; set; } = "Request inspector";

    public string Method { get; set; } = "POST";

    public DateTimeOffset CreatedAt { get; set; }

    public string ServerBaseUrl { get; set; } = string.Empty;

    public string EndpointPath { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public string RequestHeadersText { get; set; } = string.Empty;

    public string ResponseHeadersText { get; set; } = string.Empty;

    public string RequestBody { get; set; } = string.Empty;

    public string ResponseBody { get; set; } = string.Empty;

    public string DisplayResponse { get; set; } = string.Empty;

    public string StatusMessage { get; set; } = string.Empty;

    public string ResponseFormat { get; set; } = string.Empty;

    public string ActiveResponseTab { get; set; } = string.Empty;

    public bool HasProxyError { get; set; }

    public ChatHistoryMetrics Metrics { get; set; } = new();

    public static ChatHistoryEntry FromRequest(RequestHistoryEntry request) => new()
    {
        Source = request.Source,
        Method = request.Method,
        CreatedAt = DateTimeOffset.Now,
        ServerBaseUrl = request.ServerBaseUrl,
        EndpointPath = request.EndpointPath,
        ContentType = request.ContentType,
        RequestHeadersText = request.RequestHeadersText,
        ResponseHeadersText = request.ResponseHeadersText,
        RequestBody = request.RequestBody,
        ResponseBody = request.ResponseBody,
        DisplayResponse = request.DisplayResponse,
        StatusMessage = request.StatusMessage,
        ResponseFormat = request.ResponseFormat,
        ActiveResponseTab = request.ActiveResponseTab,
        HasProxyError = request.HasProxyError,
        Metrics = request.Metrics
    };
}

public sealed class RequestHistoryEntry
{
    public string Source { get; set; } = string.Empty;

    public string Method { get; set; } = string.Empty;

    public string ServerBaseUrl { get; set; } = string.Empty;

    public string EndpointPath { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public string RequestHeadersText { get; set; } = string.Empty;

    public string ResponseHeadersText { get; set; } = string.Empty;

    public string RequestBody { get; set; } = string.Empty;

    public string ResponseBody { get; set; } = string.Empty;

    public string DisplayResponse { get; set; } = string.Empty;

    public string StatusMessage { get; set; } = string.Empty;

    public string ResponseFormat { get; set; } = string.Empty;

    public string ActiveResponseTab { get; set; } = string.Empty;

    public bool HasProxyError { get; set; }

    public ChatHistoryMetrics Metrics { get; set; } = new();
}

public sealed class ChatHistoryMetrics
{
    public string Status { get; set; } = "-";

    public string ContentType { get; set; } = "-";

    public TimeSpan? TimeToFirstByte { get; set; }

    public TimeSpan? Duration { get; set; }

    public int Chunks { get; set; }

    public long TotalBytes { get; set; }

    public int? InputTokens { get; set; }

    public int? OutputTokens { get; set; }

    public int? ReasoningTokens { get; set; }

    public int? CachedInputTokens { get; set; }

    public int? TotalTokens { get; set; }
}
