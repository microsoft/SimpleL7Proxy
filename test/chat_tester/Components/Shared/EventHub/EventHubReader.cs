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

        if (!eventData.TryGetValue("Type", out var eventType) || string.IsNullOrWhiteSpace(eventType))
        {
            return;
        }

        switch (eventType)
        {
            case "S7P-Backend":
                ApplyBackendEvent(eventData);
                break;

            case "S7P-ProxyRequest":
            case "S7P-ProxyRequestExpired":
            case "S7P-ProxyRequestRequeued":
                ApplyRequestEvent(eventData, eventType);
                break;
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
        var statusCode = ParseNullableInt(eventData, "Status");
        var failed = eventType != "S7P-ProxyRequest" || (statusCode is >= 400);
        var statusLabel = failed ? "Failed" : "Completed";
        var statusMessage = BuildStatusMessage(eventData, eventType, statusCode, statusLabel);

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
            RequestHeadersText = BuildRequestHeadersText(eventData),
            ResponseHeadersText = BuildResponseHeadersText(eventData, statusCode),
            RequestBodyDisplay = string.Empty,
            ResponseBody = string.Empty,
            IsComplete = true,
            IsFailed = failed,
        });
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
        using var document = JsonDocument.Parse(eventBody);
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
}