using System.Text.Json;

namespace chat_tester.Components.Shared;

public sealed class ChatHistoryStore
{
    private const string DataDirectoryName = "data";
    private const string HistoryFileName = "chat-history.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _historyFilePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<ChatHistoryEntry> _entries = new();

    public ChatHistoryStore(IWebHostEnvironment environment)
    {
        var dataDirectory = Path.Combine(environment.ContentRootPath, DataDirectoryName);
        Directory.CreateDirectory(dataDirectory);
        _historyFilePath = Path.Combine(dataDirectory, HistoryFileName);
        Load();
    }

    public IReadOnlyList<ChatHistoryEntry> Entries => _entries;

    public async Task<ChatHistoryEntry> AddAsync(ChatHistoryEntry entry)
    {
        await _gate.WaitAsync();
        try
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
            {
                entry.Id = Guid.NewGuid().ToString("N");
            }

            if (entry.CreatedAt == default)
            {
                entry.CreatedAt = DateTimeOffset.Now;
            }

            _entries.Add(entry);
            await SaveCoreAsync();
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
            var index = _entries.FindIndex(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
            if (index < 0)
            {
                return false;
            }

            _entries.RemoveAt(index);
            await SaveCoreAsync();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Load()
    {
        if (!File.Exists(_historyFilePath))
        {
            return;
        }

        try
        {
            var entries = JsonSerializer.Deserialize<List<ChatHistoryEntry>>(File.ReadAllText(_historyFilePath), JsonOptions);
            if (entries is not null)
            {
                _entries.AddRange(entries.OrderBy(entry => entry.CreatedAt));
            }
        }
        catch
        {
            _entries.Clear();
        }
    }

    private async Task SaveCoreAsync()
    {
        await using var stream = File.Create(_historyFilePath);
        await JsonSerializer.SerializeAsync(stream, _entries, JsonOptions);
    }
}

public sealed class ChatHistoryEntry
{
    public string Id { get; set; } = string.Empty;

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
}

public sealed class ChatHistoryMetrics
{
    public string Status { get; set; } = "-";

    public string ContentType { get; set; } = "-";

    public TimeSpan? TimeToFirstByte { get; set; }

    public TimeSpan? Duration { get; set; }

    public int Chunks { get; set; }

    public long TotalBytes { get; set; }
}