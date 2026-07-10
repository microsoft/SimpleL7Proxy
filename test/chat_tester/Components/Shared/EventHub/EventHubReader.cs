using System.Text;
using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace chat_tester.Components.Shared;

public sealed class EventHubReader : BackgroundService
{
    private const string ConnectionStringVariable = "EVENTHUB_CONNECTIONSTRING";
    private const string EventHubNameVariable = "EVENTHUB_NAME";
    private const string ConsumerGroupVariable = "EVENTHUB_CONSUMER_GROUP";
    private const string EventHubNamespaceVariable = "EVENTHUB_NAMESPACE";

    private readonly EventHubMonitorStore _store;
    private readonly EventHubMonitorOptions _options;
    private readonly ILogger<EventHubReader> _logger;
    private readonly Dictionary<string, List<string>> _requestLifecycle = new(StringComparer.OrdinalIgnoreCase);

    public EventHubReader(
        EventHubMonitorStore store,
        IOptions<EventHubMonitorOptions> options,
        ILogger<EventHubReader> logger)
    {
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = ResolveSettings();
        await ImportLocalFileAsync(settings.LocalFilePath, stoppingToken).ConfigureAwait(false);

        if (!settings.EventHubEnabled)
        {
            return;
        }

        if (!settings.IsConfigured)
        {
            _logger.LogInformation(
                "Event Hub reader not started. Set {ConnectionStringVariable} and {EventHubNameVariable}, or set {EventHubNamespaceVariable} and {EventHubNameVariable}.",
                ConnectionStringVariable,
                EventHubNameVariable,
                EventHubNamespaceVariable,
                EventHubNameVariable);
            return;
        }

        settings = EnsureNamespace(settings);

        var clientOptions = new EventHubConsumerClientOptions
        {
            ConnectionOptions = new EventHubConnectionOptions
            {
                TransportType = EventHubsTransportType.AmqpTcp,
            },
            RetryOptions = new EventHubsRetryOptions
            {
                MaximumRetries = 3,
                TryTimeout = TimeSpan.FromSeconds(60),
                Delay = TimeSpan.FromMilliseconds(800),
                MaximumDelay = TimeSpan.FromSeconds(10),
                Mode = EventHubsRetryMode.Exponential,
            },
        };

        try
        {
            await RunConsumerAsync(settings, clientOptions, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Event Hub reader stopped unexpectedly.");
        }
    }

    private async Task ImportLocalFileAsync(string? localFilePath, CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(localFilePath))
        {
            _store.DisableRequestAging = false;
            return;
        }

        if (!Path.IsPathRooted(localFilePath))
        {
            localFilePath = Path.GetFullPath(localFilePath, AppContext.BaseDirectory);
        }

        if (!File.Exists(localFilePath))
        {
            _store.DisableRequestAging = false;
            _logger.LogWarning("Configured Event Hub import file was not found: {LocalFilePath}", localFilePath);
            return;
        }

        _store.DisableRequestAging = true;
        _store.Clear();
        _requestLifecycle.Clear();

        var importedCount = 0;
        var skippedCount = 0;

        await using var stream = File.OpenRead(localFilePath);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var objectBuffer = new StringBuilder();
        var readState = new JsonObjectReadState();

        while (!stoppingToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(stoppingToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var jsonObjects = ExtractJsonObjects(line, objectBuffer, ref readState);
            foreach (var jsonObject in jsonObjects)
            {
                try
                {
                    var eventData = ParseEventData(jsonObject);
                    if (ProcessEventData(eventData))
                    {
                        importedCount++;
                    }
                    else
                    {
                        skippedCount++;
                    }
                }
                catch (JsonException ex)
                {
                    skippedCount++;
                    _logger.LogWarning(ex, "Ignoring invalid JSON record in Event Hub import file {LocalFilePath}.", localFilePath);
                }
            }
        }

        if (readState.IsCapturing && objectBuffer.Length > 0)
        {
            skippedCount++;
            _logger.LogWarning(
                "Ignoring incomplete JSON record at the end of Event Hub import file {LocalFilePath}.",
                localFilePath);
        }

        _logger.LogInformation(
            "Imported {ImportedCount} events from local Event Hub file {LocalFilePath}. Skipped {SkippedCount} unsupported or invalid records.",
            importedCount,
            localFilePath,
            skippedCount);
    }

    private static List<string> ExtractJsonObjects(
        string line,
        StringBuilder objectBuffer,
        ref JsonObjectReadState state)
    {
        var jsonObjects = new List<string>();

        if (state.IsCapturing && objectBuffer.Length > 0)
        {
            objectBuffer.Append('\n');
        }

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];

            if (!state.IsCapturing)
            {
                if (character != '{')
                {
                    continue;
                }

                state = state.StartObject();
                objectBuffer.Clear();
            }

            objectBuffer.Append(character);

            if (state.EscapeNext)
            {
                state = state with { EscapeNext = false };
                continue;
            }

            if (character == '\\' && state.InString)
            {
                state = state with { EscapeNext = true };
                continue;
            }

            if (character == '"')
            {
                state = state with { InString = !state.InString };
                continue;
            }

            if (state.InString)
            {
                continue;
            }

            if (character == '{')
            {
                state = state with { Depth = state.Depth + 1 };
                continue;
            }

            if (character != '}')
            {
                continue;
            }

            state = state with { Depth = state.Depth - 1 };
            if (state.Depth != 0)
            {
                continue;
            }

            jsonObjects.Add(objectBuffer.ToString());
            objectBuffer.Clear();
            state = default;
        }

        return jsonObjects;
    }

    private async Task RunConsumerAsync(
        ReaderSettings settings,
        EventHubConsumerClientOptions clientOptions,
        CancellationToken stoppingToken)
    {
        try
        {
            await using var consumerClient = CreateConsumerClient(settings, clientOptions);
            await ConsumeAsync(consumerClient, settings, stoppingToken).ConfigureAwait(false);
        }
        catch (EventHubsException ex) when (ShouldRetryWithManagedIdentity(ex, settings))
        {
            var fallbackSettings = settings with
            {
                ConnectionString = null,
            };

            _logger.LogWarning(
                ex,
                "Connection string authentication was rejected for Event Hub namespace {EventHubNamespace}. Retrying with DefaultAzureCredential.",
                fallbackSettings.EventHubNamespace);

            try
            {
                await using var consumerClient = CreateConsumerClient(fallbackSettings, clientOptions);
                await ConsumeAsync(consumerClient, fallbackSettings, stoppingToken).ConfigureAwait(false);
            }
            catch (CredentialUnavailableException credentialUnavailableException)
            {
                LogManagedIdentityUnavailable(credentialUnavailableException, fallbackSettings.EventHubNamespace);
            }
            catch (AuthenticationFailedException authenticationFailedException) when (IsCredentialUnavailable(authenticationFailedException))
            {
                LogManagedIdentityUnavailable(authenticationFailedException, fallbackSettings.EventHubNamespace);
            }
        }
    }

    private void LogManagedIdentityUnavailable(Exception exception, string? eventHubNamespace)
    {
        _logger.LogWarning(
            exception,
            "Event Hub reader disabled. Connection string auth was rejected for {EventHubNamespace}, and DefaultAzureCredential could not acquire a token. Configure a usable Azure credential or assign a managed identity with Event Hubs Data Receiver access.",
            eventHubNamespace ?? "the configured namespace");
    }

    private async Task ConsumeAsync(
        EventHubConsumerClient consumerClient,
        ReaderSettings settings,
        CancellationToken stoppingToken)
    {
        var startPosition = ResolveStartPosition(settings.StartPosition);
        var partitionIds = await consumerClient.GetPartitionIdsAsync(stoppingToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Event Hub reader started for {EventHubName} using consumer group {ConsumerGroup} across {PartitionCount} partitions.",
            settings.EventHubName,
            settings.ConsumerGroup,
            partitionIds.Length);

        var partitionReaders = partitionIds
            .Select(partitionId => ReadPartitionAsync(consumerClient, partitionId, startPosition, stoppingToken))
            .ToArray();

        await Task.WhenAll(partitionReaders).ConfigureAwait(false);
    }

    private async Task ReadPartitionAsync(
        EventHubConsumerClient consumerClient,
        string partitionId,
        EventPosition startPosition,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var partitionEvent in consumerClient.ReadEventsFromPartitionAsync(
                    partitionId,
                    startPosition,
                    cancellationToken: stoppingToken).ConfigureAwait(false))
                {
                    ProcessEvent(partitionEvent, partitionId);
                }

                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Event Hub read failed for partition {PartitionId}; retrying.", partitionId);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private void ProcessEvent(PartitionEvent partitionEvent, string partitionId)
    {
        var eventBody = Encoding.UTF8.GetString(partitionEvent.Data.Body.ToArray());
        Dictionary<string, string> eventData;

        try
        {
            eventData = ParseEventData(eventBody);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Ignoring non-JSON Event Hub message from partition {PartitionId}.", partitionId);
            return;
        }

        ProcessEventData(eventData);
    }

    private bool ProcessEventData(IReadOnlyDictionary<string, string> eventData)
    {
        if (!eventData.TryGetValue("Type", out var eventType) || string.IsNullOrWhiteSpace(eventType))
        {
            return false;
        }

        switch (eventType)
        {
            case "S7P-Backend":
                ApplyBackendEvent(eventData);
                return true;

            case "S7P-ProxyRequestEnqueued":
            case "S7P-BackendRequest":
            case "S7P-ServerError":
            case "S7P-CircuitBreakerError":
                TrackLifecycleEvent(eventData, eventType);
                return false;

            case "S7P-ProxyRequest":
            case "S7P-ProxyRequestExpired":
            case "S7P-ProxyRequestRequeued":
                ApplyRequestEvent(eventData, eventType);
                return true;

            default:
                return false;
        }
    }

    private void ApplyBackendEvent(IReadOnlyDictionary<string, string> eventData)
    {
        var backends = new List<BackendHealthSnapshot>();
        for (var index = 1; ; index++)
        {
            if (!eventData.TryGetValue($"{index}-Host", out var host) || string.IsNullOrWhiteSpace(host))
            {
                break;
            }

            var latencyMs = ParseDouble(eventData, $"{index}-Latency");
            var status = GetValue(eventData, $"{index}-Status");
            backends.Add(new BackendHealthSnapshot
            {
                Name = BuildBackendName(host),
                Url = host,
                Status = status,
                LatencyMs = latencyMs,
                SuccessRate = (int)Math.Round(ParseDouble(eventData, $"{index}-SuccessRate")),
                Calls = ParseInt(eventData, $"{index}-Calls"),
                Errors = ParseInt(eventData, $"{index}-Errors"),
                Css = ResolveBackendCss(status),
            });
        }

        if (backends.Count == 0)
        {
            return;
        }

        _store.UpdateBackends(backends);

        var primaryBackend = backends
            .FirstOrDefault(backend => backend.Css == "healthy")
            ?.Name ?? backends[0].Name;

        _store.UpdateFleet(new FleetInfoSnapshot
        {
            ActiveHosts = ParseInt(eventData, "ActiveHostsCount"),
            TotalHosts = backends.Count,
            ProbeLatencyMs = backends.Average(backend => backend.LatencyMs),
            LoadBalancingMode = GetValue(eventData, "LoadBalanceMode", "latency"),
            PrimaryBackend = primaryBackend,
            ProxyVersion = GetValue(eventData, "Ver"),
        });
    }

    private void ApplyRequestEvent(IReadOnlyDictionary<string, string> eventData, string eventType)
    {
        var correlationKey = GetCorrelationKey(eventData);
        if (!string.IsNullOrWhiteSpace(correlationKey))
        {
            TrackLifecycleEvent(eventData, eventType);
        }

        var statusCode = ParseNullableInt(eventData, "Status");
        var failed = eventType != "S7P-ProxyRequest" || (statusCode is >= 400);
        var statusLabel = failed ? "Failed" : "Completed";
        var statusMessage = BuildStatusMessage(eventData, eventType, statusCode, statusLabel);
        var requestHeaders = BuildRequestHeadersText(eventData);
        var responseHeaders = BuildResponseHeadersText(eventData, statusCode);
        var lifecycleText = BuildLifecycleText(correlationKey);
        if (!string.IsNullOrWhiteSpace(lifecycleText))
        {
            responseHeaders = string.IsNullOrWhiteSpace(responseHeaders)
                ? $"Lifecycle:{Environment.NewLine}{lifecycleText}"
                : $"{responseHeaders}{Environment.NewLine}Lifecycle:{Environment.NewLine}{lifecycleText}";
        }

        var summaryBody = BuildRequestSummaryBody(eventData);
        var responseBody = BuildResponseSummaryBody(eventData, statusMessage);

        _store.AddRequest(new MultiRequestStatusItem
        {
            Status = statusLabel,
            StatusMessage = statusMessage,
            StatusCode = statusCode,
            ContentType = GetValue(eventData, "Content-Type", "-"),
            TimeToFirstByte = ParseNullableMilliseconds(eventData, "Request-Queue-Duration"),
            Duration = ParseNullableMilliseconds(eventData, "Total-Latency")
                ?? ParseNullableMilliseconds(eventData, "Duration"),
            Chunks = 0,
            TotalBytes = ParseLong(eventData, "Content-Length"),
            RequestHeadersText = requestHeaders,
            ResponseHeadersText = responseHeaders,
            RequestBodyDisplay = summaryBody,
            ResponseBody = responseBody,
            IsComplete = true,
            IsFailed = failed,
        });

        if (!string.IsNullOrWhiteSpace(correlationKey))
        {
            _requestLifecycle.Remove(correlationKey);
        }
    }

    private void TrackLifecycleEvent(IReadOnlyDictionary<string, string> eventData, string eventType)
    {
        var correlationKey = GetCorrelationKey(eventData);
        if (string.IsNullOrWhiteSpace(correlationKey))
        {
            return;
        }

        if (!_requestLifecycle.TryGetValue(correlationKey, out var steps))
        {
            steps = new List<string>();
            _requestLifecycle[correlationKey] = steps;
        }

        var timestamp = FirstNonEmpty(
            GetValue(eventData, "Date"),
            GetValue(eventData, "Timestamp"),
            GetValue(eventData, "Request-Date"),
            GetValue(eventData, "EnqueueTime")) ?? "(time-unknown)";

        var status = GetValue(eventData, "Status");
        var backendHost = GetValue(eventData, "Backend-Host");
        var message = FirstNonEmpty(
            GetValue(eventData, "Message"),
            GetValue(eventData, "ErrorDetail"),
            GetValue(eventData, "Error"),
            GetValue(eventData, "ErrorMessage"));

        var detailParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(status))
        {
            detailParts.Add($"status={status}");
        }

        if (!string.IsNullOrWhiteSpace(backendHost))
        {
            detailParts.Add($"backend={backendHost}");
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            detailParts.Add($"message={message}");
        }

        var suffix = detailParts.Count > 0 ? $" | {string.Join(" | ", detailParts)}" : string.Empty;
        steps.Add($"{timestamp} | {eventType}{suffix}");
    }

    private string BuildLifecycleText(string? correlationKey)
    {
        if (string.IsNullOrWhiteSpace(correlationKey))
        {
            return string.Empty;
        }

        if (!_requestLifecycle.TryGetValue(correlationKey, out var steps) || steps.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, steps);
    }

    private static string BuildRequestSummaryBody(IReadOnlyDictionary<string, string> eventData)
    {
        var summary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MID"] = GetValue(eventData, "MID"),
            ["GUID"] = GetValue(eventData, "GUID"),
            ["UserID"] = GetValue(eventData, "UserID"),
            ["Path"] = GetValue(eventData, "Path"),
            ["RequestType"] = GetValue(eventData, "RequestType"),
            ["Priority"] = GetValue(eventData, "Priority"),
            ["Priority2"] = GetValue(eventData, "Priority2"),
            ["Request-Queue-Duration"] = GetValue(eventData, "Request-Queue-Duration"),
            ["Total-Latency"] = GetValue(eventData, "Total-Latency"),
        };

        return JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string BuildResponseSummaryBody(IReadOnlyDictionary<string, string> eventData, string statusMessage)
    {
        var summary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["StatusMessage"] = statusMessage,
            ["Message"] = GetValue(eventData, "Message"),
            ["ErrorDetail"] = GetValue(eventData, "ErrorDetail"),
            ["Backend-Host"] = GetValue(eventData, "Backend-Host"),
            ["backendLog"] = GetValue(eventData, "backendLog"),
        };

        return JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string? GetCorrelationKey(IReadOnlyDictionary<string, string> eventData)
    {
        return FirstNonEmpty(
            GetValue(eventData, "MID"),
            GetValue(eventData, "GUID"));
    }

    private ReaderSettings ResolveSettings()
    {
        var connectionString = FirstNonEmpty(
            Environment.GetEnvironmentVariable(ConnectionStringVariable),
            _options.ConnectionString);
        var eventHubName = FirstNonEmpty(
            Environment.GetEnvironmentVariable(EventHubNameVariable),
            _options.EventHubName);
        var consumerGroup = FirstNonEmpty(
            Environment.GetEnvironmentVariable(ConsumerGroupVariable),
            _options.ConsumerGroup,
            EventHubConsumerClient.DefaultConsumerGroupName) ?? EventHubConsumerClient.DefaultConsumerGroupName;
        var eventHubNamespace = FirstNonEmpty(
            Environment.GetEnvironmentVariable(EventHubNamespaceVariable),
            _options.EventHubNamespace);

        if (!string.IsNullOrWhiteSpace(eventHubNamespace) && !eventHubNamespace.Contains('.'))
        {
            eventHubNamespace = $"{eventHubNamespace}.servicebus.windows.net";
        }

        return new ReaderSettings(
            _options.EventHubEnabled,
            _options.LocalFilePath,
            connectionString,
            eventHubName,
            consumerGroup,
            eventHubNamespace,
            _options.StartPosition);
    }

    private static ReaderSettings EnsureNamespace(ReaderSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.EventHubNamespace))
        {
            return settings;
        }

        var namespaceFromConnectionString = TryExtractNamespace(settings.ConnectionString);
        return string.IsNullOrWhiteSpace(namespaceFromConnectionString)
            ? settings
            : settings with { EventHubNamespace = namespaceFromConnectionString };
    }

    private static EventHubConsumerClient CreateConsumerClient(
        ReaderSettings settings,
        EventHubConsumerClientOptions clientOptions)
    {
        if (!string.IsNullOrWhiteSpace(settings.ConnectionString))
        {
            return new EventHubConsumerClient(
                settings.ConsumerGroup,
                settings.ConnectionString,
                settings.EventHubName!,
                clientOptions);
        }

        return new EventHubConsumerClient(
            settings.ConsumerGroup,
            settings.EventHubNamespace!,
            settings.EventHubName!,
            new DefaultAzureCredential(),
            clientOptions);
    }

    private static bool ShouldRetryWithManagedIdentity(EventHubsException ex, ReaderSettings settings)
    {
        return !string.IsNullOrWhiteSpace(settings.ConnectionString)
            && !string.IsNullOrWhiteSpace(settings.EventHubNamespace)
            && ex.Message.Contains("LocalAuthDisabled", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCredentialUnavailable(AuthenticationFailedException exception)
    {
        return exception.InnerException is CredentialUnavailableException
            || exception.Message.Contains("DefaultAzureCredential failed to retrieve a token", StringComparison.OrdinalIgnoreCase);
    }

    private static EventPosition ResolveStartPosition(string? startPosition)
    {
        return string.Equals(startPosition, "earliest", StringComparison.OrdinalIgnoreCase)
            ? EventPosition.Earliest
            : EventPosition.Latest;
    }

    private static string? TryExtractNamespace(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        foreach (var segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!segment.StartsWith("Endpoint=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var endpointValue = segment["Endpoint=".Length..];
            if (Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpointUri))
            {
                return endpointUri.Host;
            }
        }

        return null;
    }

    private static Dictionary<string, string> ParseEventData(string eventBody)
    {
        using var document = JsonDocument.Parse(NormalizeJsonRecord(eventBody));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Event Hub payload was not a JSON object.");
        }

        var eventData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            eventData[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                JsonValueKind.Null => string.Empty,
                _ => property.Value.GetRawText(),
            };
        }

        return eventData;
    }

    private static string NormalizeJsonRecord(string eventBody)
    {
        if (string.IsNullOrEmpty(eventBody))
        {
            return eventBody;
        }

        var builder = new StringBuilder(eventBody.Length);
        var inString = false;
        var escapeNext = false;

        foreach (var character in eventBody)
        {
            if (escapeNext)
            {
                builder.Append(character);
                escapeNext = false;
                continue;
            }

            if (character == '\\')
            {
                builder.Append(character);
                if (inString)
                {
                    escapeNext = true;
                }

                continue;
            }

            if (character == '"')
            {
                builder.Append(character);
                inString = !inString;
                continue;
            }

            if (inString && character is '\r' or '\n')
            {
                builder.Append(character == '\r' ? "\\r" : "\\n");
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string BuildBackendName(string host)
    {
        return Uri.TryCreate(host, UriKind.Absolute, out var uri)
            ? uri.Host
            : host;
    }

    private static string ResolveBackendCss(string status)
    {
        if (status.Contains("active", StringComparison.OrdinalIgnoreCase))
        {
            return "healthy";
        }

        if (status.Contains("throttle", StringComparison.OrdinalIgnoreCase)
            || status.Contains("below", StringComparison.OrdinalIgnoreCase)
            || status.Contains("fail", StringComparison.OrdinalIgnoreCase))
        {
            return "degraded";
        }

        return "neutral";
    }

    private static string BuildStatusMessage(
        IReadOnlyDictionary<string, string> eventData,
        string eventType,
        int? statusCode,
        string statusLabel)
    {
        var message = GetValue(eventData, "Error")
            ?? GetValue(eventData, "ErrorMessage")
            ?? GetValue(eventData, "Message");

        if (!string.IsNullOrWhiteSpace(message))
        {
            return statusCode is int code
                ? $"{code} {message}"
                : message;
        }

        if (statusCode is int value)
        {
            return $"{value} {statusLabel}";
        }

        return eventType;
    }

    private static string BuildRequestHeadersText(IReadOnlyDictionary<string, string> eventData)
    {
        var lines = new List<string>();
        var method = GetValue(eventData, "Method", "GET");
        var path = GetPath(eventData);
        lines.Add($"{method} {path}");

        AddHeaderLine(lines, "RequestHost", GetValue(eventData, "RequestHost"));
        AddHeaderLine(lines, "UserID", GetValue(eventData, "UserID"));
        AddHeaderLine(lines, "Priority", GetValue(eventData, "Priority"));
        AddHeaderLine(lines, "Priority2", GetValue(eventData, "Priority2"));
        AddHeaderLine(lines, "MID", GetValue(eventData, "MID"));

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildResponseHeadersText(IReadOnlyDictionary<string, string> eventData, int? statusCode)
    {
        var lines = new List<string>();
        if (statusCode is int code)
        {
            lines.Add($"HTTP {code}");
        }

        AddHeaderLine(lines, "Content-Type", GetValue(eventData, "Content-Type"));
        AddHeaderLine(lines, "Backend-Host", GetValue(eventData, "Backend-Host"));
        AddHeaderLine(lines, "backendLog", GetValue(eventData, "backendLog"));
        AddHeaderLine(lines, "retry-after", GetValue(eventData, "retry-after"));

        return string.Join(Environment.NewLine, lines);
    }

    private static void AddHeaderLine(List<string> lines, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            lines.Add($"{name}: {value}");
        }
    }

    private static string GetPath(IReadOnlyDictionary<string, string> eventData)
    {
        var path = GetValue(eventData, "Path");
        if (!string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var uri = GetValue(eventData, "Uri");
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsedUri))
        {
            return parsedUri.PathAndQuery;
        }

        return uri ?? "/";
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string GetValue(
        IReadOnlyDictionary<string, string> eventData,
        string key,
        string fallback = "")
    {
        return eventData.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;
    }

    private static int ParseInt(IReadOnlyDictionary<string, string> eventData, string key)
    {
        return int.TryParse(GetValue(eventData, key), out var value) ? value : 0;
    }

    private static int? ParseNullableInt(IReadOnlyDictionary<string, string> eventData, string key)
    {
        return int.TryParse(GetValue(eventData, key), out var value) ? value : null;
    }

    private static long ParseLong(IReadOnlyDictionary<string, string> eventData, string key)
    {
        return long.TryParse(GetValue(eventData, key), out var value) ? value : 0;
    }

    private static double ParseDouble(IReadOnlyDictionary<string, string> eventData, string key)
    {
        return double.TryParse(GetValue(eventData, key), out var value) ? value : 0;
    }

    private static TimeSpan? ParseNullableMilliseconds(IReadOnlyDictionary<string, string> eventData, string key)
    {
        return double.TryParse(GetValue(eventData, key), out var value)
            ? TimeSpan.FromMilliseconds(value)
            : null;
    }

    private sealed record ReaderSettings(
        bool EventHubEnabled,
        string? LocalFilePath,
        string? ConnectionString,
        string? EventHubName,
        string ConsumerGroup,
        string? EventHubNamespace,
        string? StartPosition)
    {
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(EventHubName)
            && (!string.IsNullOrWhiteSpace(ConnectionString)
                || !string.IsNullOrWhiteSpace(EventHubNamespace));
    }

    private readonly record struct JsonObjectReadState(
        bool IsCapturing,
        bool InString,
        bool EscapeNext,
        int Depth)
    {
        public JsonObjectReadState StartObject()
        {
            return new JsonObjectReadState(
                IsCapturing: true,
                InString: false,
                EscapeNext: false,
                Depth: 0);
        }
    }
}