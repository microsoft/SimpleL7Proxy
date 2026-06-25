using System.Text.Json;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Azure.Cosmos;

namespace chat_tester.Components.Shared;

public sealed class ChatConversationStore
{
    private const string DefaultDataDirectoryName = "data";
    private const string DefaultConversationDirectoryName = "conversations";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly IWebHostEnvironment _environment;
    private readonly ConversationSettings _conversationSettings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<ChatConversationMessage> _messages = new();
    private ChatConversationSummary[] _snapshot = [];

    private ConversationStorageSettings _settings = new();
    private CosmosClient? _cosmosClient;
    private string _cosmosAccount = string.Empty;

    public ChatConversationStore(IWebHostEnvironment environment, ConversationSettings conversationSettings)
    {
        _environment = environment;
        _conversationSettings = conversationSettings;
        _settings = NormalizeSettings(conversationSettings.Current);
    }

    public int Count => _snapshot.Length;

    public bool HasConversations => _snapshot.Length > 0;

    public string LastStorageError { get; private set; } = string.Empty;

    public IReadOnlyList<ChatConversationSummary> GetSnapshot() => _snapshot;

    public ConversationStorageSettings Settings => CloneSettings(_settings);

    public async Task ReloadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _settings = NormalizeSettings(_conversationSettings.Current);
            _conversationSettings.Apply(_settings);
            _messages.Clear();
            _messages.AddRange(await LoadCoreAsync(_settings));
            RefreshSnapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplySettingsAsync(ConversationStorageSettings settings)
    {
        await _gate.WaitAsync();
        try
        {
            _settings = NormalizeSettings(settings);
            _conversationSettings.Apply(_settings);
            LastStorageError = string.Empty;
            _messages.Clear();
            _messages.AddRange(await LoadCoreAsync(_settings));
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
            _messages.Clear();
            _messages.AddRange(await LoadCoreAsync(_settings));
            RefreshSnapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ChatConversation> GetConversationAsync(string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return ChatConversation.Empty;
        }

        await _gate.WaitAsync();
        try
        {
            var messages = _messages
                .Where(message => string.Equals(message.ConversationId, conversationId, StringComparison.Ordinal))
                .OrderBy(message => message.CreatedAt)
                .ToArray();

            return messages.Length == 0
                ? ChatConversation.Empty
                : new ChatConversation(conversationId, messages);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<ChatConversationMessage> AddMessageAsync(NewConversationMessage message) =>
        AddCoreAsync(ChatConversationMessage.FromNewMessage(message));

    private async Task<ChatConversationMessage> AddCoreAsync(ChatConversationMessage message)
    {
        await _gate.WaitAsync();
        try
        {
            PrepareMessage(message);
            _messages.Add(message);
            _messages.Sort((left, right) => left.CreatedAt.CompareTo(right.CreatedAt));
            RefreshSnapshot();
            try
            {
                await SaveMessageCoreAsync(_settings, message);
                LastStorageError = string.Empty;
            }
            catch (Exception ex)
            {
                LastStorageError = ex.Message;
            }

            return message;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<ChatConversationMessage>> LoadCoreAsync(ConversationStorageSettings settings)
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
            return Array.Empty<ChatConversationMessage>();
        }
    }

    private async Task<IReadOnlyList<ChatConversationMessage>> LoadFromDiskAsync(ConversationStorageSettings settings)
    {
        var messages = new List<ChatConversationMessage>();
        var directory = ResolveDiskDirectory(settings.DiskPath);
        Directory.CreateDirectory(directory);

        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
        {
            var message = JsonSerializer.Deserialize<ChatConversationMessage>(await File.ReadAllTextAsync(file), JsonOptions);
            if (message is not null)
            {
                messages.Add(message);
            }
        }

        return NormalizeMessages(messages);
    }

    private async Task<IReadOnlyList<ChatConversationMessage>> LoadFromBlobStorageAsync(ConversationStorageSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.StorageAccountName) || string.IsNullOrWhiteSpace(settings.BlobContainerName))
        {
            return Array.Empty<ChatConversationMessage>();
        }

        var messages = new List<ChatConversationMessage>();
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
            var message = response.Value.Content.ToObjectFromJson<ChatConversationMessage>(JsonOptions);
            if (message is not null)
            {
                messages.Add(message);
            }
        }

        return NormalizeMessages(messages);
    }

    private async Task<IReadOnlyList<ChatConversationMessage>> LoadFromCosmosAsync(ConversationStorageSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.CosmosAccount) || string.IsNullOrWhiteSpace(settings.CosmosDatabase) || string.IsNullOrWhiteSpace(settings.CosmosContainer))
        {
            return Array.Empty<ChatConversationMessage>();
        }

        var container = await GetCosmosContainerAsync(settings);
        var messages = new List<ChatConversationMessage>();
        using var iterator = container.GetItemQueryIterator<ChatConversationMessage>(
            new QueryDefinition("SELECT * FROM c WHERE c.documentType = @documentType ORDER BY c.createdAt")
                .WithParameter("@documentType", nameof(ChatConversationMessage)));

        while (iterator.HasMoreResults)
        {
            foreach (var message in await iterator.ReadNextAsync())
            {
                messages.Add(message);
            }
        }

        return NormalizeMessages(messages);
    }

    private async Task SaveMessageCoreAsync(ConversationStorageSettings settings, ChatConversationMessage message)
    {
        switch (settings.Mode)
        {
            case HistoryStorageMode.BlobStorage:
                await SaveToBlobStorageAsync(settings, message);
                break;
            case HistoryStorageMode.CosmosDb:
                await SaveToCosmosAsync(settings, message);
                break;
            default:
                await SaveToDiskAsync(settings, message);
                break;
        }
    }

    private async Task SaveToDiskAsync(ConversationStorageSettings settings, ChatConversationMessage message)
    {
        var path = Path.Combine(ResolveDiskDirectory(settings.DiskPath), BuildConversationPath(message));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, message, JsonOptions);
    }

    private async Task SaveToBlobStorageAsync(ConversationStorageSettings settings, ChatConversationMessage message)
    {
        if (string.IsNullOrWhiteSpace(settings.StorageAccountName) || string.IsNullOrWhiteSpace(settings.BlobContainerName))
        {
            return;
        }

        var container = CreateBlobContainerClient(settings);
        await container.CreateIfNotExistsAsync();
        var blob = container.GetBlobClient(BuildConversationPath(message).Replace(Path.DirectorySeparatorChar, '/'));
        await using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions));
        await blob.UploadAsync(stream, overwrite: true);
    }

    private async Task SaveToCosmosAsync(ConversationStorageSettings settings, ChatConversationMessage message)
    {
        if (string.IsNullOrWhiteSpace(settings.CosmosAccount) || string.IsNullOrWhiteSpace(settings.CosmosDatabase) || string.IsNullOrWhiteSpace(settings.CosmosContainer))
        {
            return;
        }

        var container = await GetCosmosContainerAsync(settings);
        await container.UpsertItemAsync(message, new PartitionKey(message.PartitionKey));
    }

    private async Task<Container> GetCosmosContainerAsync(ConversationStorageSettings settings)
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

    private static BlobContainerClient CreateBlobContainerClient(ConversationStorageSettings settings)
    {
        var accountName = settings.StorageAccountName.Trim();
        var containerName = settings.BlobContainerName.Trim();
        var containerUri = new Uri($"https://{accountName}.blob.core.windows.net/{containerName}");
        return new BlobContainerClient(containerUri, new DefaultAzureCredential());
    }

    private string ResolveDiskDirectory(string diskPath)
    {
        var configuredPath = string.IsNullOrWhiteSpace(diskPath)
            ? Path.Combine(DefaultDataDirectoryName, DefaultConversationDirectoryName)
            : diskPath;

        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(_environment.ContentRootPath, configuredPath);
    }

    private static string BuildConversationPath(ChatConversationMessage message) =>
        Path.Combine(message.ConversationId, $"{message.Id}.json");

    private static string BuildCosmosEndpoint(string account)
    {
        if (account.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || account.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return account;
        }

        return $"https://{account}.documents.azure.com:443/";
    }

    private static void PrepareMessage(ChatConversationMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Id))
        {
            message.Id = Guid.NewGuid().ToString("N");
        }

        if (string.IsNullOrWhiteSpace(message.ConversationId))
        {
            message.ConversationId = Guid.NewGuid().ToString("N");
        }

        if (message.CreatedAt == default)
        {
            message.CreatedAt = DateTimeOffset.Now;
        }

        message.DocumentType = nameof(ChatConversationMessage);
        message.PartitionKey = message.ConversationId;
        message.Method = string.IsNullOrWhiteSpace(message.Method) ? "POST" : message.Method;
        message.Metrics ??= new MultiStepChatMetrics();
    }

    private static IReadOnlyList<ChatConversationMessage> NormalizeMessages(IEnumerable<ChatConversationMessage> messages)
    {
        var deduped = new Dictionary<string, ChatConversationMessage>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            PrepareMessage(message);
            deduped[message.Id] = message;
        }

        return deduped.Values.OrderBy(message => message.CreatedAt).ToArray();
    }

    private void RefreshSnapshot()
    {
        _snapshot = _messages
            .GroupBy(message => message.ConversationId, StringComparer.Ordinal)
            .Select(group => ChatConversationSummary.FromMessages(group.Key, group))
            .OrderByDescending(summary => summary.UpdatedAt)
            .ToArray();
    }

    private static ConversationStorageSettings NormalizeSettings(ConversationStorageSettings? settings)
    {
        var source = settings ?? new ConversationStorageSettings();
        var mode = source.Mode switch
        {
            HistoryStorageMode.BlobStorage => HistoryStorageMode.BlobStorage,
            HistoryStorageMode.CosmosDb => HistoryStorageMode.CosmosDb,
            _ => HistoryStorageMode.Disk
        };

        return new ConversationStorageSettings
        {
            Mode = mode,
            DiskPath = source.DiskPath,
            StorageAccountName = source.StorageAccountName,
            BlobContainerName = string.IsNullOrWhiteSpace(source.BlobContainerName) ? "conversations" : source.BlobContainerName,
            CosmosAccount = source.CosmosAccount,
            CosmosDatabase = source.CosmosDatabase,
            CosmosContainer = source.CosmosContainer
        };
    }

    private static ConversationStorageSettings CloneSettings(ConversationStorageSettings settings) => new()
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

public sealed class ChatConversation
{
    public static ChatConversation Empty { get; } = new(string.Empty, Array.Empty<ChatConversationMessage>());

    public ChatConversation(string id, IReadOnlyList<ChatConversationMessage> messages)
    {
        Id = id;
        Messages = messages;
    }

    public string Id { get; }

    public IReadOnlyList<ChatConversationMessage> Messages { get; }
}

public sealed class ChatConversationSummary
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = "Untitled conversation";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int MessageCount { get; set; }

    public string EndpointPath { get; set; } = string.Empty;

    public static ChatConversationSummary FromMessages(string conversationId, IEnumerable<ChatConversationMessage> messages)
    {
        var orderedMessages = messages.OrderBy(message => message.CreatedAt).ToArray();
        var first = orderedMessages.FirstOrDefault();
        var last = orderedMessages.LastOrDefault();

        return new ChatConversationSummary
        {
            Id = conversationId,
            Title = BuildTitle(first?.UserMessage),
            CreatedAt = first?.CreatedAt ?? DateTimeOffset.Now,
            UpdatedAt = last?.CreatedAt ?? DateTimeOffset.Now,
            MessageCount = orderedMessages.Length,
            EndpointPath = last?.EndpointPath ?? string.Empty
        };
    }

    private static string BuildTitle(string? userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            return "Untitled conversation";
        }

        var normalized = string.Join(' ', userMessage.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 56 ? normalized : string.Concat(normalized.AsSpan(0, 53), "...");
    }
}

public sealed class ChatConversationMessage
{
    public string Id { get; set; } = string.Empty;

    public string DocumentType { get; set; } = nameof(ChatConversationMessage);

    public string PartitionKey { get; set; } = string.Empty;

    public string ConversationId { get; set; } = string.Empty;

    public string Method { get; set; } = "POST";

    public DateTimeOffset CreatedAt { get; set; }

    public string ServerBaseUrl { get; set; } = string.Empty;

    public string EndpointPath { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public string RequestHeadersText { get; set; } = string.Empty;

    public string ResponseHeadersText { get; set; } = string.Empty;

    public string RequestBody { get; set; } = string.Empty;

    public string RawResponseText { get; set; } = string.Empty;

    public string UserMessage { get; set; } = string.Empty;

    public string AssistantMessage { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public MultiStepChatMetrics Metrics { get; set; } = new();

    public static ChatConversationMessage FromNewMessage(NewConversationMessage message) => new()
    {
        ConversationId = message.ConversationId,
        Method = message.Method,
        CreatedAt = DateTimeOffset.Now,
        ServerBaseUrl = message.ServerBaseUrl,
        EndpointPath = message.EndpointPath,
        ContentType = message.ContentType,
        RequestHeadersText = message.RequestHeadersText,
        ResponseHeadersText = message.ResponseHeadersText,
        RequestBody = message.RequestBody,
        RawResponseText = message.RawResponseText,
        UserMessage = message.UserMessage,
        AssistantMessage = message.AssistantMessage,
        Status = message.Status,
        Metrics = message.Metrics
    };
}

public sealed class NewConversationMessage
{
    public string ConversationId { get; set; } = string.Empty;

    public string Method { get; set; } = "POST";

    public string ServerBaseUrl { get; set; } = string.Empty;

    public string EndpointPath { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public string RequestHeadersText { get; set; } = string.Empty;

    public string ResponseHeadersText { get; set; } = string.Empty;

    public string RequestBody { get; set; } = string.Empty;

    public string RawResponseText { get; set; } = string.Empty;

    public string UserMessage { get; set; } = string.Empty;

    public string AssistantMessage { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public MultiStepChatMetrics Metrics { get; set; } = new();
}

public sealed class ConversationStorageSettings
{
    public string Mode { get; set; } = HistoryStorageMode.Disk;

    public string DiskPath { get; set; } = string.Empty;

    public string StorageAccountName { get; set; } = string.Empty;

    public string BlobContainerName { get; set; } = "conversations";

    public string CosmosAccount { get; set; } = string.Empty;

    public string CosmosDatabase { get; set; } = string.Empty;

    public string CosmosContainer { get; set; } = string.Empty;
}