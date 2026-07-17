using System.Text.Json;

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
    private readonly DocumentStorageRepository<ChatConversationMessage> _repository;

    public ChatConversationStore(IWebHostEnvironment environment, ConversationSettings conversationSettings)
    {
        _environment = environment;
        _conversationSettings = conversationSettings;
        _settings = NormalizeSettings(conversationSettings.Current);
        _repository = new DocumentStorageRepository<ChatConversationMessage>(
            JsonOptions,
            environment.ContentRootPath,
            nameof(ChatConversationMessage),
            Path.Combine(DefaultDataDirectoryName, DefaultConversationDirectoryName),
            BuildConversationPath);
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
                .ThenBy(GetMessageSortOrder)
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

    public async Task<ChatConversationTurn> AddTurnAsync(NewConversationTurn turn)
    {
        var conversationTurn = ChatConversationTurn.FromNewTurn(turn);
        await AddCoreAsync(ChatConversationMessage.FromTurn(conversationTurn));
        return conversationTurn;
    }

    public async Task<ChatConversationMessage> AddMessageAsync(NewConversationMessage message)
    {
        var conversationMessage = ChatConversationMessage.FromNewMessage(message);
        await AddCoreAsync(new[] { conversationMessage });
        return conversationMessage;
    }

    private async Task AddCoreAsync(IReadOnlyList<ChatConversationMessage> messages)
    {
        if (messages.Count == 0)
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            foreach (var message in messages)
            {
                PrepareMessage(message);
            }

            try
            {
                foreach (var message in messages)
                {
                    await _repository.SaveAsync(_settings, message);
                }

                LastStorageError = string.Empty;
            }
            catch (Exception ex)
            {
                LastStorageError = ex.Message;
                throw;
            }

            _messages.AddRange(messages);
            _messages.Sort((left, right) =>
            {
                var createdAtComparison = left.CreatedAt.CompareTo(right.CreatedAt);
                return createdAtComparison != 0
                    ? createdAtComparison
                    : GetMessageSortOrder(left).CompareTo(GetMessageSortOrder(right));
            });
            RefreshSnapshot();
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
            return NormalizeMessages(await _repository.LoadAsync(settings));
        }
        catch (Exception ex)
        {
            LastStorageError = ex.Message;
            return Array.Empty<ChatConversationMessage>();
        }
    }

    private static string BuildConversationPath(ChatConversationMessage message) =>
        Path.Combine(message.ConversationId, $"{message.Id}.json");

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

        message.Role = ChatConversationRole.Normalize(message.Role);
        if (string.IsNullOrWhiteSpace(message.Content))
        {
            message.Content = message.Role switch
            {
                ChatConversationRole.User => message.UserMessage,
                ChatConversationRole.Assistant => message.AssistantMessage,
                _ => message.Content
            };
        }

        if (string.IsNullOrWhiteSpace(message.TurnId) && !string.IsNullOrWhiteSpace(message.Role))
        {
            message.TurnId = Guid.NewGuid().ToString("N");
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

        return deduped.Values
            .OrderBy(message => message.CreatedAt)
            .ThenBy(GetMessageSortOrder)
            .ToArray();
    }

    private void RefreshSnapshot()
    {
        _snapshot = _messages
            .GroupBy(message => message.ConversationId, StringComparer.Ordinal)
            .Select(group => ChatConversationSummary.FromMessages(group.Key, group))
            .OrderByDescending(summary => summary.UpdatedAt)
            .ToArray();
    }

    private static int GetMessageSortOrder(ChatConversationMessage message) => message.Role switch
    {
        ChatConversationRole.User => 0,
        ChatConversationRole.Assistant => 1,
        _ => 2
    };

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

    public IReadOnlyList<ChatConversationTurn> Turns => ChatConversationTurn.FromMessages(Messages);
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
        var orderedMessages = messages
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.Role == ChatConversationRole.Assistant ? 1 : 0)
            .ToArray();
        var first = orderedMessages.FirstOrDefault();
        var last = orderedMessages.LastOrDefault();

        return new ChatConversationSummary
        {
            Id = conversationId,
            Title = BuildTitle(orderedMessages.FirstOrDefault(message => message.Role == ChatConversationRole.User)?.Content ?? first?.UserMessage),
            CreatedAt = first?.CreatedAt ?? DateTimeOffset.Now,
            UpdatedAt = last?.CreatedAt ?? DateTimeOffset.Now,
            MessageCount = ChatConversationTurn.FromMessages(orderedMessages).Count,
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

public sealed class ChatConversationMessage : IStorageDocument
{
    public string Id { get; set; } = string.Empty;

    public string DocumentType { get; set; } = nameof(ChatConversationMessage);

    public string PartitionKey { get; set; } = string.Empty;

    public string ConversationId { get; set; } = string.Empty;

    public string TurnId { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

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
        TurnId = message.TurnId,
        Role = message.Role,
        Content = message.Content,
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

    public static IReadOnlyList<ChatConversationMessage> FromTurn(ChatConversationTurn turn) =>
    [
        FromTurnMessage(turn, ChatConversationRole.User, turn.UserMessage, turn.CreatedAt),
        FromTurnMessage(turn, ChatConversationRole.Assistant, turn.AssistantMessage, turn.CreatedAt.AddTicks(1))
    ];

    private static ChatConversationMessage FromTurnMessage(ChatConversationTurn turn, string role, string content, DateTimeOffset createdAt) => new()
    {
        ConversationId = turn.ConversationId,
        TurnId = turn.Id,
        Role = role,
        Content = content,
        Method = turn.Method,
        CreatedAt = createdAt,
        ServerBaseUrl = turn.ServerBaseUrl,
        EndpointPath = turn.EndpointPath,
        ContentType = turn.ContentType,
        RequestHeadersText = role == ChatConversationRole.User ? turn.RequestHeadersText : string.Empty,
        ResponseHeadersText = role == ChatConversationRole.Assistant ? turn.ResponseHeadersText : string.Empty,
        RequestBody = role == ChatConversationRole.User ? turn.RequestBody : string.Empty,
        RawResponseText = role == ChatConversationRole.Assistant ? turn.RawResponseText : string.Empty,
        UserMessage = role == ChatConversationRole.User ? turn.UserMessage : string.Empty,
        AssistantMessage = role == ChatConversationRole.Assistant ? turn.AssistantMessage : string.Empty,
        Status = role == ChatConversationRole.Assistant ? turn.Status : string.Empty,
        Metrics = role == ChatConversationRole.Assistant ? turn.Metrics : new MultiStepChatMetrics()
    };
}

public sealed class NewConversationMessage
{
    public string ConversationId { get; set; } = string.Empty;

    public string TurnId { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

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

public sealed class NewConversationTurn
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

public sealed class ChatConversationTurn
{
    public string Id { get; set; } = string.Empty;

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

    public static ChatConversationTurn FromNewTurn(NewConversationTurn turn)
    {
        var createdAt = DateTimeOffset.Now;
        return new ChatConversationTurn
        {
            Id = Guid.NewGuid().ToString("N"),
            ConversationId = turn.ConversationId,
            Method = turn.Method,
            CreatedAt = createdAt,
            ServerBaseUrl = turn.ServerBaseUrl,
            EndpointPath = turn.EndpointPath,
            ContentType = turn.ContentType,
            RequestHeadersText = turn.RequestHeadersText,
            ResponseHeadersText = turn.ResponseHeadersText,
            RequestBody = turn.RequestBody,
            RawResponseText = turn.RawResponseText,
            UserMessage = turn.UserMessage,
            AssistantMessage = turn.AssistantMessage,
            Status = turn.Status,
            Metrics = turn.Metrics
        };
    }

    public static IReadOnlyList<ChatConversationTurn> FromMessages(IEnumerable<ChatConversationMessage> messages)
    {
        var turns = new List<ChatConversationTurn>();
        var pendingUserMessages = new Queue<ChatConversationMessage>();
        foreach (var message in messages.OrderBy(message => message.CreatedAt))
        {
            if (string.IsNullOrWhiteSpace(message.Role))
            {
                turns.Add(FromLegacyMessage(message));
                continue;
            }

            if (message.Role == ChatConversationRole.User)
            {
                pendingUserMessages.Enqueue(message);
                continue;
            }

            if (message.Role == ChatConversationRole.Assistant)
            {
                var userMessage = pendingUserMessages.Count > 0 ? pendingUserMessages.Dequeue() : null;
                turns.Add(FromMessagePair(userMessage, message));
            }
        }

        foreach (var userMessage in pendingUserMessages)
        {
            turns.Add(FromMessagePair(userMessage, null));
        }

        return turns.OrderBy(turn => turn.CreatedAt).ToArray();
    }

    private static ChatConversationTurn FromMessagePair(ChatConversationMessage? userMessage, ChatConversationMessage? assistantMessage)
    {
        var sourceMessage = assistantMessage ?? userMessage ?? new ChatConversationMessage();
        return new ChatConversationTurn
        {
            Id = sourceMessage.TurnId,
            ConversationId = sourceMessage.ConversationId,
            Method = string.IsNullOrWhiteSpace(sourceMessage.Method) ? "POST" : sourceMessage.Method,
            CreatedAt = userMessage?.CreatedAt ?? assistantMessage?.CreatedAt ?? DateTimeOffset.Now,
            ServerBaseUrl = FirstNonEmpty(userMessage?.ServerBaseUrl, assistantMessage?.ServerBaseUrl),
            EndpointPath = FirstNonEmpty(userMessage?.EndpointPath, assistantMessage?.EndpointPath),
            ContentType = FirstNonEmpty(userMessage?.ContentType, assistantMessage?.ContentType),
            RequestHeadersText = userMessage?.RequestHeadersText ?? string.Empty,
            ResponseHeadersText = assistantMessage?.ResponseHeadersText ?? string.Empty,
            RequestBody = userMessage?.RequestBody ?? string.Empty,
            RawResponseText = assistantMessage?.RawResponseText ?? string.Empty,
            UserMessage = userMessage?.Content ?? string.Empty,
            AssistantMessage = assistantMessage?.Content ?? string.Empty,
            Status = assistantMessage?.Status ?? string.Empty,
            Metrics = assistantMessage?.Metrics ?? new MultiStepChatMetrics()
        };
    }

    private static ChatConversationTurn FromLegacyMessage(ChatConversationMessage message) => new()
    {
        Id = string.IsNullOrWhiteSpace(message.TurnId) ? message.Id : message.TurnId,
        ConversationId = message.ConversationId,
        Method = message.Method,
        CreatedAt = message.CreatedAt,
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

    private static string FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first! : second ?? string.Empty;
}

public static class ChatConversationRole
{
    public const string User = "user";
    public const string Assistant = "assistant";

    public static string Normalize(string role) => role switch
    {
        User => User,
        Assistant => Assistant,
        _ => string.Empty
    };
}

public sealed class ConversationStorageSettings : IStorageSettings
{
    public string Mode { get; set; } = HistoryStorageMode.Disk;

    public string DiskPath { get; set; } = string.Empty;

    public string StorageAccountName { get; set; } = string.Empty;

    public string BlobContainerName { get; set; } = "conversations";

    public string CosmosAccount { get; set; } = string.Empty;

    public string CosmosDatabase { get; set; } = string.Empty;

    public string CosmosContainer { get; set; } = string.Empty;
}