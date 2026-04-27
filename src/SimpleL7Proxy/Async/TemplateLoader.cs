using System.Collections.Frozen;
using System.Text.Json;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SimpleL7Proxy.Async.BackupAPI;
using SimpleL7Proxy.Async.BlobStorage;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.Async.ServiceBus;
using SimpleL7Proxy.Proxy;
using SimpleL7Proxy.User;

namespace SimpleL7Proxy.Async;

/// <summary>
/// Identifies a canned message template loaded from the "templates" blob container.
/// </summary>
public enum AsyncMessageKind
{
    Welcome,
    NotReady,
    NotAuthorized,
}

/// <summary>
/// One-shot hosted service that wires up <see cref="RequestData"/> static references
/// for async-mode processing and loads canned message templates from blob storage.
/// Runs during the hosted-service startup phase so that all dependencies are ready
/// before Server and WorkerFactory begin accepting traffic.
/// </summary>
public sealed class TemplateLoader : IHostedService
{
    private const string TemplatesContainer = "templates";
    private const string TemplatesUserId = "templates";

    /// <summary>
    /// Mapping of message kind → blob name within the templates container.
    /// </summary>
    private static readonly FrozenDictionary<AsyncMessageKind, string> s_blobNames =
        new Dictionary<AsyncMessageKind, string>
        {
            [AsyncMessageKind.Welcome]       = "welcome.json",
            [AsyncMessageKind.NotReady]      = "notready.json",
            [AsyncMessageKind.NotAuthorized] = "notauthorized.json",
        }.ToFrozenDictionary();

    private readonly IServiceBusRequestService _serviceBusRequestService;
    private readonly IBackupAPIService _backupAPIService;
    private readonly IUserPriorityService _userPriorityService;
    private readonly IBlobWriter _blobWriter;
    private readonly ProxyConfig _options;
    private readonly ILogger<TemplateLoader> _logger;

    private readonly Dictionary<AsyncMessageKind, string> _templates = new();

    public TemplateLoader(
        IServiceBusRequestService serviceBusRequestService,
        IBackupAPIService backupAPIService,
        IUserPriorityService userPriorityService,
        IBlobWriter blobWriter,
        IOptions<ProxyConfig> options,
        ILogger<TemplateLoader> logger)
    {
        _serviceBusRequestService = serviceBusRequestService;
        _backupAPIService = backupAPIService;
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
    /// Returns the loaded template body for <paramref name="kind"/>, or an empty string
    /// if the blob was missing or failed to load.
    /// </summary>
    private string GetTemplate(AsyncMessageKind kind)
        => _templates.TryGetValue(kind, out var body) ? body : string.Empty;

    /// <summary>
    /// Renders the template for <paramref name="kind"/>, substituting <c>%GUID%</c>,
    /// <c>%MID%</c>, and <c>%TIMESTAMP%</c> placeholders, and deserializes it as an
    /// <see cref="AsyncMessage"/>. Other placeholders (e.g. <c>%DELAY_S%</c>) are left
    /// untouched for the caller to fill before this is called.
    /// </summary>
    /// <param name="kind">Which template to render.</param>
    /// <param name="guid">Request GUID; replaces every <c>%GUID%</c>.</param>
    /// <param name="mid">Message id; replaces every <c>%MID%</c>.</param>
    /// <returns>The merged <see cref="AsyncMessage"/>, or <c>null</c> if the template was
    /// not loaded or could not be parsed.</returns>
    public AsyncMessage? GetMergedMessage(AsyncMessageKind kind, string guid, string mid)
    {
        var template = GetTemplate(kind);
        if (string.IsNullOrEmpty(template))
            return null;

        var merged = template
            .Replace("%GUID%", guid ?? string.Empty, StringComparison.Ordinal)
            .Replace("%MID%", mid ?? string.Empty, StringComparison.Ordinal)
            .Replace("%TIMESTAMP%", DateTime.UtcNow.ToString("o"), StringComparison.Ordinal);

        try
        {
            return JsonSerializer.Deserialize<AsyncMessage>(merged, s_jsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "[TEMPLATE] Failed to deserialize merged template {Kind} for GUID {Guid}",
                kind, guid);
            return null;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        RequestData.InitializeServiceBusRequestService(
            _serviceBusRequestService,
            _backupAPIService,
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
            _logger.LogWarning("[STARTUP] BlobWriter not initialized; skipping load of '{Container}' templates",
                TemplatesContainer);
            return;
        }

        try
        {
            await _blobWriter.InitClientAsync(TemplatesUserId, TemplatesContainer).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[STARTUP] Failed to initialize templates container '{Container}'",
                TemplatesContainer);
            return;
        }

        foreach (var (kind, blobName) in s_blobNames)
        {
            await LoadTemplateAsync(kind, blobName, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task LoadTemplateAsync(AsyncMessageKind kind, string blobName, CancellationToken cancellationToken)
    {
        try
        {
            if (!await _blobWriter.BlobExistsAsync(TemplatesUserId, blobName).ConfigureAwait(false))
            {
                _logger.LogWarning("[STARTUP] Template blob '{Container}/{Blob}' ({Kind}) not found",
                    TemplatesContainer, blobName, kind);
                return;
            }

            using var stream = await _blobWriter.ReadBlobAsStreamAsync(TemplatesUserId, blobName).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            _templates[kind] = body;

            _logger.LogInformation("[STARTUP] ✓ Loaded template {Kind} from '{Container}/{Blob}' ({Length} bytes)",
                kind, TemplatesContainer, blobName, body.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[STARTUP] Failed to load template {Kind} from '{Container}/{Blob}'",
                kind, TemplatesContainer, blobName);
        }
    }
}
