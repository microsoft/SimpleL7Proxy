using System.Text.Json;

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
    private readonly AuthTokenSettings _authSettings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<ChatHistoryEntry> _entries = new();
    private ChatHistoryEntry[] _snapshot = [];

    private HistoryStorageSettings _settings = new();
    private readonly DocumentStorageRepository<ChatHistoryEntry> _repository;

    public ChatHistoryStore(IWebHostEnvironment environment, HistorySettings historySettings, AuthTokenSettings authSettings)
    {
        _environment = environment;
        _historySettings = historySettings;
        _authSettings = authSettings;
        _settings = NormalizeSettings(historySettings.Current);
        _repository = new DocumentStorageRepository<ChatHistoryEntry>(
            JsonOptions,
            environment.ContentRootPath,
            nameof(ChatHistoryEntry),
            Path.Combine(DefaultDataDirectoryName, DefaultHistoryDirectoryName),
            BuildHistoryPath,
            diskFileSkip: name => string.Equals(name, LegacyHistoryFileName, StringComparison.OrdinalIgnoreCase),
            diskLegacyLoader: LoadLegacyHistoryFromDiskAsync);
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
        AddCoreAsync(ChatHistoryEntry.FromRequest(RedactSensitiveHeaders(request)));

    private RequestHistoryEntry RedactSensitiveHeaders(RequestHistoryEntry request)
    {
        request.RequestHeadersText = ChatTesterHttp.RedactSensitiveHeaders(request.RequestHeadersText, _authSettings.HeaderName);
        request.ResponseHeadersText = ChatTesterHttp.RedactSensitiveHeaders(request.ResponseHeadersText, _authSettings.HeaderName);
        return request;
    }

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
                await _repository.SaveAsync(_settings, entry);
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

            await _repository.DeleteAsync(_settings, entry);
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
            return NormalizeEntries(await _repository.LoadAsync(settings));
        }
        catch (Exception ex)
        {
            LastStorageError = ex.Message;
            return Array.Empty<ChatHistoryEntry>();
        }
    }

    private async Task<IReadOnlyList<ChatHistoryEntry>> LoadLegacyHistoryFromDiskAsync(string directory)
    {
        var entries = new List<ChatHistoryEntry>();

        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
        {
            if (!string.Equals(Path.GetFileName(file), LegacyHistoryFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var legacyEntries = JsonSerializer.Deserialize<List<ChatHistoryEntry>>(await File.ReadAllTextAsync(file), JsonOptions);
            if (legacyEntries is not null)
            {
                entries.AddRange(legacyEntries);
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

        return entries;
    }

    private string ResolveDiskDirectory(string diskPath) => _repository.ResolveDiskDirectory(diskPath);

    private static string BuildHistoryPath(ChatHistoryEntry entry)
    {
        var local = entry.CreatedAt.LocalDateTime;
        return Path.Combine(local.ToString("yyyy-MM"), local.ToString("dd"), $"{entry.Id}.json");
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

public sealed class ChatHistoryEntry : IStorageDocument
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
