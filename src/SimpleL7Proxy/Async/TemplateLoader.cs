using System.Collections.Frozen;
using System.Text.Json;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SimpleL7Proxy.Async.ServiceBus.SBQueue;
using SimpleL7Proxy.Async.ServiceBus.SBTopic;
using SimpleL7Proxy.Async.BlobStorage;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.Async.ServiceBus;
using SimpleL7Proxy.Proxy;
using SimpleL7Proxy.User;

namespace SimpleL7Proxy.Async;

/// <summary>
/// One-shot hosted service that wires up <see cref="RequestData"/> static references
/// for async-mode processing and loads canned message templates from blob storage.
/// Runs during the hosted-service startup phase so that all dependencies are ready
/// before Server and WorkerFactory begin accepting traffic.
/// </summary>
public sealed class TemplateLoader : IHostedService
{
    private const string TemplatesContainer = "templates";

    /// <summary>
    /// Mapping of message kind → blob name within the templates container.
    /// </summary>
    private static readonly FrozenDictionary<AsyncResponseTypeEnum, string> s_blobNames =
        new Dictionary<AsyncResponseTypeEnum, string>
        {
            [AsyncResponseTypeEnum.Welcome]       = "welcome.json",
            [AsyncResponseTypeEnum.NotReady]      = "notready.json",
            [AsyncResponseTypeEnum.NotAuthorized] = "notauthorized.json",
        }.ToFrozenDictionary();

    private readonly ISBTopicService _sbTopicService;
    private readonly ISBQueueService _sbQueueService;
    private readonly IUserPriorityService _userPriorityService;
    private readonly IBlobWriter _blobWriter;
    private readonly ProxyConfig _options;
    private readonly ILogger<TemplateLoader> _logger;

    private readonly Dictionary<AsyncResponseTypeEnum, AsyncMessage> _templates = new();

    public TemplateLoader(
        ISBTopicService sbTopicService,
        ISBQueueService sbQueueService,
        IUserPriorityService userPriorityService,
        IBlobWriter blobWriter,
        IOptions<ProxyConfig> options,
        ILogger<TemplateLoader> logger)
    {
        _sbTopicService = sbTopicService;
        _sbQueueService = sbQueueService;
        _userPriorityService = userPriorityService;
        _blobWriter = blobWriter;
        _options = options.Value;
        _logger = logger;
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Returns a rendered <see cref="AsyncMessage"/> for <paramref name="kind"/>.
    /// Placeholder substitution (<c>%GUID%</c>, <c>%MID%</c>, <c>%TIMESTAMP%</c>,
    /// <c>%USERID%</c>, <c>%DATA_BLOB_URI%</c>, <c>%HEADER_BLOB_URI%</c>) is applied
    /// only to the <see cref="AsyncMessage.Message"/> field. All other fields are
    /// taken from the caller-supplied values when provided, otherwise from the
    /// loaded template literal.
    /// </summary>
    public AsyncMessage GetMergedMessage(
        AsyncResponseTypeEnum kind,
        string guid,
        string mid,
        string? userId = null,
        string? dataBlobUri = null,
        string? headerBlobUri = null)
    {
        if (!_templates.TryGetValue(kind, out var template))
            throw new InvalidOperationException($"Template for {kind} was not loaded.");

        return new AsyncMessage
        {
            Message       = SubstituteMessage(template.Message, guid, mid, userId, dataBlobUri, headerBlobUri),
            UserId        = userId        ?? template.UserId        ?? string.Empty,
            MID           = mid           ?? template.MID           ?? string.Empty,
            Guid          = guid          ?? template.Guid          ?? string.Empty,
            Status        = template.Status,
            Timestamp     = DateTime.UtcNow,
            DataBlobUri   = dataBlobUri   ?? template.DataBlobUri   ?? string.Empty,
            HeaderBlobUri = headerBlobUri ?? template.HeaderBlobUri ?? string.Empty,
        };
    }

    private static string SubstituteMessage(
        string message,
        string? guid, string? mid, string? userId,
        string? dataBlobUri, string? headerBlobUri)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;
        if (message.IndexOf('%') < 0) return message;          // fast path

        var sb = new System.Text.StringBuilder(message.Length + 64);
        sb.Append(message);

        if (guid          is { Length: > 0 } && message.Contains("%GUID%"))            sb.Replace("%GUID%", guid);
        if (mid           is { Length: > 0 } && message.Contains("%MID%"))             sb.Replace("%MID%", mid);
        if (userId        is { Length: > 0 } && message.Contains("%USERID%"))          sb.Replace("%USERID%", userId);
        if (dataBlobUri   is { Length: > 0 } && message.Contains("%DATA_BLOB_URI%"))   sb.Replace("%DATA_BLOB_URI%", dataBlobUri);
        if (headerBlobUri is { Length: > 0 } && message.Contains("%HEADER_BLOB_URI%")) sb.Replace("%HEADER_BLOB_URI%", headerBlobUri);
        if (message.Contains("%TIMESTAMP%"))                                           sb.Replace("%TIMESTAMP%", DateTime.UtcNow.ToString("o"));

        return sb.ToString();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        RequestData.InitializeServiceBusRequestService(
            _sbTopicService,
            _sbQueueService,
            _userPriorityService,
            _options);

        _logger.LogInformation("[STARTUP] ✓ RequestData async statics initialized");

        await LoadAllTemplatesAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task LoadAllTemplatesAsync(CancellationToken cancellationToken)
    {
        if (!_blobWriter.IsInitialized)
        {
            _logger.LogWarning("[STARTUP] BlobWriter not initialized; skipping load of '{Container}' templates and disabling async mode",
                TemplatesContainer);
            _options.AsyncModeEnabled = false;
            return;
        }

        try
        {
            await _blobWriter.InitClientAsync(TemplatesContainer).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError("[STARTUP] Failed to initialize templates container '{Container}': {Message}. Disabling async mode.",
                TemplatesContainer, ex.Message);
            _options.AsyncModeEnabled = false;
            return;
        }

        var loadTasks = s_blobNames
            .Select(kvp => LoadTemplateAsync(kvp.Key, kvp.Value, cancellationToken))
            .ToList();
        var results = await Task.WhenAll(loadTasks).ConfigureAwait(false);
        var successfulTemplates = results.Where(r => r.success).Select(r => r.name).ToList();

        _logger.LogInformation("[STARTUP] ✓ Loaded {Count} templates from '{Container}': {Templates}",
            successfulTemplates.Count, TemplatesContainer, string.Join(", ", successfulTemplates));
    }

    private async Task<(string name, bool success)> LoadTemplateAsync(AsyncResponseTypeEnum kind, string blobName, CancellationToken cancellationToken)
    {
        try
        {
            string? body = null;

            if (!await _blobWriter.BlobExistsAsync(TemplatesContainer, blobName).ConfigureAwait(false))
            {
                _logger.LogWarning("[STARTUP] Template blob '{Container}/{Blob}' ({Kind}) not found, attempting to load from templates folder",
                    TemplatesContainer, blobName, kind);

                // Fallback to reading from templates folder
                var templatePath = Path.Combine("templates", blobName);
                if (File.Exists(templatePath))
                {
                    body = await File.ReadAllTextAsync(templatePath, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("[STARTUP] Loaded template {Kind} from file system: {Path}",
                        kind, templatePath);
                }
                else
                {
                    _logger.LogWarning("[STARTUP] Template file not found at '{Path}' ({Kind})",
                        templatePath, kind);
                    return (blobName, false);
                }
            }
            else
            {
                using var stream = await _blobWriter.ReadBlobAsStreamAsync(TemplatesContainer, blobName).ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<AsyncMessage>(body, s_jsonOptions);
                if (parsed == null)
                {
                    _logger.LogError("[STARTUP] Template {Kind} from '{Container}/{Blob}' deserialized to null",
                        kind, TemplatesContainer, blobName);
                    return (blobName, false);
                }

                _templates[kind] = parsed;
                return (blobName, true);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "[STARTUP] Failed to deserialize template {Kind} from '{Container}/{Blob}'",
                    kind, TemplatesContainer, blobName);
                _logger.LogInformation(ex.StackTrace);
                return (blobName, false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[STARTUP] Failed to load template {Kind} from '{Container}/{Blob}'",
                kind, TemplatesContainer, blobName);
            return (blobName, false);
        }
    }
}
