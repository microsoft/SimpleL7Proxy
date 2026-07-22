using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Identity;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using chat_tester.Components.Shared.EventHub;
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
    private readonly ProxyMetricsCatalog _proxyMetricsCatalog;
    private readonly EventHubMonitorOptions _options;
    private readonly ILogger<EventHubReader> _logger;
    private readonly DefaultAzureCredential? _credential;
    private readonly Dictionary<string, List<string>> _requestLifecycle = new(StringComparer.OrdinalIgnoreCase);
    // Per-request field capture keyed by S7P-ID: the enqueue, each backend attempt, and the final
    // proxy-request fields are retained so the request detail can show the full lifecycle.
    private readonly Dictionary<string, RequestPhaseRecord> _requestPhases = new(StringComparer.OrdinalIgnoreCase);
    private bool _logSkippedRecords;

    private static readonly PipelineStage[] OrderedStages =
    {
        PipelineStage.Statistics,
        PipelineStage.RequestStatus,
        PipelineStage.RuntimeStats,
        PipelineStage.Backends,
        PipelineStage.Endpoints,
    };

    private enum PipelineStage
    {
        Statistics,
        RequestStatus,
        RuntimeStats,
        Backends,
        Endpoints,
    }

    private enum RequestPhase
    {
        Enqueue,
        Attempt,
        Final
    }

    private sealed class RequestPhaseRecord
    {
        public IReadOnlyDictionary<string, string>? Enqueue { get; set; }
        public List<IReadOnlyDictionary<string, string>> Attempts { get; } = new();
        public IReadOnlyDictionary<string, string>? Final { get; set; }

        /// <summary>
        /// The still-running placeholder row added to the store when the enqueue event was seen.
        /// The final S7P-ProxyRequest/Expired/Requeued handler mutates this same instance in place
        /// (via <see cref="EventHubMonitorStore.MarkRequestFinalized"/>) instead of adding a new row,
        /// so the request keeps its position/RequestNumber in the Request Status list.
        /// </summary>
        public MultiRequestStatusItem? PendingItem { get; set; }
    }

    public EventHubReader(
        EventHubMonitorStore store,
        ProxyMetricsCatalog proxyMetricsCatalog,
        IOptions<EventHubMonitorOptions> options,
        ILogger<EventHubReader> logger)
    {
        _store = store;
        _proxyMetricsCatalog = proxyMetricsCatalog;
        _options = options.Value;
        _logger = logger;
        
        try
        {
            _credential = new DefaultAzureCredential();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize DefaultAzureCredential. Managed identity authentication will not be available.");
            _credential = null;
        }
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

        // Validate settings before attempting to create client
        if (string.IsNullOrWhiteSpace(settings.EventHubName))
        {
            _logger.LogError(
                "Event Hub reader cannot start. {EventHubNameVariable} is not configured.",
                EventHubNameVariable);
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.ConnectionString) && string.IsNullOrWhiteSpace(settings.EventHubNamespace))
        {
            _logger.LogError(
                "Event Hub reader cannot start. Neither {ConnectionStringVariable} nor {EventHubNamespaceVariable} is configured.",
                ConnectionStringVariable,
                EventHubNamespaceVariable);
            return;
        }

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
        _requestPhases.Clear();
        _logSkippedRecords = true;

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
            if (jsonObjects.Count == 0)
            {
                continue;
            }

            var runResult = RunPipeline(
                jsonObjects.ToArray(),
                $"Event Hub import file {localFilePath}");
            importedCount += runResult.Processed;
            skippedCount += runResult.Skipped;
        }

        if (readState.IsCapturing && objectBuffer.Length > 0)
        {
            skippedCount++;
            _logger.LogWarning(
                "Ignoring incomplete JSON record at the end of Event Hub import file {LocalFilePath}.",
                localFilePath);
        }

        _logSkippedRecords = false;

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

        var connString = string.IsNullOrEmpty(settings.ConnectionString) ? "Not Set" : "Set";
        _logger.LogInformation(
            "[EVENTHUB] ✓ Event Hub reader started: ConnectionString: {ConnString}, Name: {EventHubName}, Namespace: {EventHubNamespace}, ConsumerGroup: {ConsumerGroup}, PartitionCount: {PartitionCount}",
            connString,
            settings.EventHubName,
            settings.EventHubNamespace,
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
            catch (System.Net.Sockets.SocketException)
            {
                _logger.LogWarning(
                    "Event Hub reader is disconnected for partition {PartitionId}; retrying in 30 seconds.",
                    partitionId);
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
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
        RunPipeline(
            new[] { eventBody },
            $"Event Hub partition {partitionId}");
    }

    // Parses each incoming record exactly once, feeds it to the store via ProcessEventData, and
    // publishes the batch (already parsed) to the metrics catalog. No stage re-parses the JSON.
    private (int Processed, int Skipped) RunPipeline(string[] incomingRecords, string source)
    {
        if (incomingRecords.Length == 0)
        {
            return (0, 0);
        }

        var parsed = new List<ParsedEventRecord>(incomingRecords.Length);
        var processed = 0;
        var skipped = 0;

        foreach (var raw in incomingRecords)
        {
            Dictionary<string, string> eventData;
            try
            {
                eventData = ParseEventData(raw);
            }
            catch (JsonException ex)
            {
                skipped++;
                LogInvalidRecord(ex, source);
                continue;
            }

            parsed.Add(new ParsedEventRecord(raw, eventData));

            if (eventData.TryGetValue("Type", out var recordType)
                && IsIncompleteRecord(eventData, recordType))
            {
                AppendIncompleteRecord(raw);
            }

            if (ProcessEventData(eventData))
            {
                processed++;
            }
            else
            {
                skipped++;
                if (_logSkippedRecords)
                {
                    _logger.LogInformation("Skipped record: {Record}", raw);
                }
            }
        }

        RunStatisticsStage(parsed);

        return (processed, skipped);
    }

    private void RunStatisticsStage(IReadOnlyList<ParsedEventRecord> parsed)
    {
        _proxyMetricsCatalog.Publish(parsed);
    }

    private void LogInvalidRecord(Exception exception, string source)
    {
        _logger.LogWarning(exception, "Ignoring invalid Event Hub record from {Source}.", source);
    }

    // Matches the friendly backend selection logged by the APIM Priority-with-retry policy
    // (e.g. "Using PAYGO URL: https://..."). Records without this AND without an x-backend-label
    // are the "incomplete" / unlabeled attempts the Endpoints card falls back to a raw URL for.
    private static readonly Regex UsingBackendUrlRegex = new(
        @"Using\s+[A-Za-z0-9_-]+\s+URL:\s*https?://",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // incomplete.json lives next to the running binary; appended as JSON Lines (one record/line).
    private static readonly string IncompleteRecordsPath =
        Path.Combine(AppContext.BaseDirectory, "incomplete.json");

    private readonly object _incompleteFileGate = new();

    // A backend attempt (S7P-BackendRequest) or the final response (S7P-ProxyRequest) is
    // "incomplete" when it carries neither an x-backend-label header nor a "Using <NAME> URL:"
    // entry in any backendLog, so no friendly backend label can be resolved for it.
    private static bool IsIncompleteRecord(IReadOnlyDictionary<string, string> eventData, string eventType)
    {
        if (eventType is not ("S7P-BackendRequest" or "S7P-ProxyRequest"))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(GetValue(eventData, "x-backend-label")))
        {
            return false;
        }

        if (UsingBackendUrlRegex.IsMatch(GetValue(eventData, "backendLog")))
        {
            return false;
        }

        for (var attempt = 1; ; attempt++)
        {
            var log = GetValue(eventData, $"Attempt-{attempt}-backendLog");
            if (string.IsNullOrWhiteSpace(log))
            {
                break;
            }

            if (UsingBackendUrlRegex.IsMatch(log))
            {
                return false;
            }
        }

        return true;
    }

    private void AppendIncompleteRecord(string raw)
    {
        try
        {
            lock (_incompleteFileGate)
            {
                File.AppendAllText(IncompleteRecordsPath, raw.Trim() + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append incomplete record to {Path}.", IncompleteRecordsPath);
        }
    }

    private bool ProcessEventData(IReadOnlyDictionary<string, string> eventData)
    {
        if (!eventData.TryGetValue("Type", out var eventType) || string.IsNullOrWhiteSpace(eventType))
        {
            return false;
        }

        var handled = false;
        foreach (var stage in OrderedStages)
        {
            handled |= RunEventStage(stage, eventData, eventType);
        }

        return handled;
    }

    private bool RunEventStage(
        PipelineStage stage,
        IReadOnlyDictionary<string, string> eventData,
        string eventType)
    {
        return stage switch
        {
            PipelineStage.Statistics => IsSupportedEventType(eventType),
            PipelineStage.RequestStatus => RunRequestStatusStage(eventData, eventType),
            PipelineStage.RuntimeStats => RunRuntimeStatsStage(eventData, eventType),
            PipelineStage.Backends => RunBackendsStage(eventData, eventType),
            PipelineStage.Endpoints => RunEndpointsStage(eventData, eventType),
            _ => false,
        };
    }

    private static bool IsSupportedEventType(string eventType)
    {
        return eventType is
            "S7P-Backend"
            or "S7P-ProxyRequestEnqueued"
            or "S7P-BackendRequest"
            or "S7P-ServerError"
            or "S7P-CircuitBreakerError"
            or "S7P-ProxyRequest"
            or "S7P-ProxyRequestExpired"
            or "S7P-ProxyRequestRequeued";
    }

    private bool RunRequestStatusStage(IReadOnlyDictionary<string, string> eventData, string eventType)
    {
        switch (eventType)
        {
            case "S7P-ProxyRequestEnqueued":
                TrackLifecycleEvent(eventData, eventType);
                TrackRequestPhase(eventData, RequestPhase.Enqueue);
                TrackPendingRequest(eventData);
                return true;

            case "S7P-BackendRequest":
                ApplyBackendRequestEvent(eventData);
                return true;

            case "S7P-ServerError":
            case "S7P-CircuitBreakerError":
                TrackLifecycleEvent(eventData, eventType);
                return true;

            case "S7P-ProxyRequest":
            case "S7P-ProxyRequestExpired":
            case "S7P-ProxyRequestRequeued":
                ApplyRequestEvent(eventData, eventType);
                return true;

            default:
                return false;
        }
    }

    private bool RunRuntimeStatsStage(IReadOnlyDictionary<string, string> eventData, string eventType)
    {
        switch (eventType)
        {
            case "S7P-ProxyRequestEnqueued":
                RecordEnqueueHistory(eventData);
                return true;

            case "S7P-ServerError":
                RecordServerErrorHistory(eventData);
                return true;

            case "S7P-CircuitBreakerError":
                RecordCircuitBreakerHistory(eventData);
                return true;

            default:
                return false;
        }
    }

    private bool RunBackendsStage(IReadOnlyDictionary<string, string> eventData, string eventType)
    {
        if (!string.Equals(eventType, "S7P-Backend", StringComparison.Ordinal))
        {
            return false;
        }

        ApplyBackendEvent(eventData);
        return true;
    }

    private static bool RunEndpointsStage(IReadOnlyDictionary<string, string> eventData, string eventType)
    {
        // Endpoint metrics are derived from request items and backend logs in later stages.
        return eventType is "S7P-BackendRequest" or "S7P-ProxyRequest" or "S7P-ProxyRequestExpired";
    }

    private void ApplyBackendEvent(IReadOnlyDictionary<string, string> eventData)
    {
        var backends = new List<BackendHealthSnapshot>();
        var successThresholdPercent = NormalizeSuccessRateThreshold(ParseDouble(eventData, "SuccessRate"));
        for (var index = 1; ; index++)
        {
            if (!eventData.TryGetValue($"{index}-Host", out var host) || string.IsNullOrWhiteSpace(host))
            {
                break;
            }

            var latencyMs = ParseDouble(eventData, $"{index}-Latency");
            var status = GetValue(eventData, $"{index}-Status");
            var calls = ParseInt(eventData, $"{index}-Calls");
            var errors = ParseInt(eventData, $"{index}-Errors");
            var successRate = (int)Math.Round(ParseDouble(eventData, $"{index}-SuccessRate"));
            backends.Add(new BackendHealthSnapshot
            {
                HostKey = BuildBackendHostKey(host),
                Name = BuildBackendName(host),
                Url = host,
                Status = status,
                LatencyMs = latencyMs,
                SuccessRate = successRate,
                Calls = calls,
                Errors = errors,
                ProbeSuccesses = Math.Max(0, calls - errors),
                ProbeFailures = Math.Max(0, errors),
                Css = ResolveBackendCss(status, successRate, successThresholdPercent),
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
        var endpointKey = BuildEndpointKey(eventData);
        var endpointCircuitBreakerOpen = IsEndpointCircuitBreakerSignal(eventData, eventType, statusCode);
        var serverCircuitBreakerSignal = IsServerCircuitBreakerSignal(eventData, eventType, statusCode);

        if (serverCircuitBreakerSignal)
        {
            _store.MarkServerCircuitBreakerSignal();
        }

        var failed = eventType != "S7P-ProxyRequest" || (statusCode is >= 400);
        var statusLabel = failed ? "Failed" : "Completed";
        var statusMessage = BuildStatusMessage(eventData, eventType, statusCode, statusLabel);

        TrackRequestPhase(eventData, RequestPhase.Final);
        var phaseKey = GetPhaseKey(eventData);
        RequestPhaseRecord? phaseRecord = null;
        if (!string.IsNullOrWhiteSpace(phaseKey))
        {
            _requestPhases.TryGetValue(phaseKey, out phaseRecord);
        }

        var requestHeaders = BuildEnqueuePhaseText(phaseRecord, eventData);
        var responseHeaders = BuildAttemptAndFinalPhaseText(phaseRecord, eventData, statusCode);
        var lifecycleText = BuildLifecycleText(correlationKey);
        if (!string.IsNullOrWhiteSpace(lifecycleText))
        {
            responseHeaders = string.IsNullOrWhiteSpace(responseHeaders)
                ? $"Lifecycle:{Environment.NewLine}{lifecycleText}"
                : $"{responseHeaders}{Environment.NewLine}{Environment.NewLine}Lifecycle:{Environment.NewLine}{lifecycleText}";
        }

        var summaryBody = BuildRequestSummaryBody(eventData);
        var responseBody = BuildResponseSummaryBody(eventData, statusMessage);

        // If an enqueue event already added a still-running placeholder row for this request,
        // finalize that same instance in place so it keeps its position/RequestNumber in the
        // Request Status list. Otherwise (no enqueue was observed), add a new row as before.
        var finalizedItem = phaseRecord?.PendingItem ?? new MultiRequestStatusItem();
        var isNewItem = phaseRecord?.PendingItem is null;

        // Direct final events have no enqueue placeholder. Give them the final-event timestamp
        // (or ingestion time) so time-series consumers can place them in a trend bucket.
        finalizedItem.EnqueuedAtUtc ??= ParseNullableDateTimeOffset(eventData, "Date") ?? DateTimeOffset.UtcNow;
        finalizedItem.FinalizedAtUtc = DateTimeOffset.UtcNow;

        finalizedItem.ContainerApp = GetValue(eventData, "ContainerApp");
        finalizedItem.Replica = GetValue(eventData, "Replica");
        finalizedItem.UserId = GetValue(eventData, "UserID");
        finalizedItem.Status = statusLabel;
        finalizedItem.StatusMessage = statusMessage;
        finalizedItem.EventType = eventType;
        finalizedItem.BackendHost = BuildRequestBackendHost(eventData);
        finalizedItem.EndpointKey = endpointKey;
        finalizedItem.IsEndpointCircuitBreakerOpen = endpointCircuitBreakerOpen;
        finalizedItem.IsServerCircuitBreakerSignal = serverCircuitBreakerSignal;
        finalizedItem.StatusCode = statusCode;
        finalizedItem.ContentType = GetValue(eventData, "Content-Type", "-");
        finalizedItem.TimeToFirstByte = ParseNullableMilliseconds(eventData, "Request-Queue-Duration");
        finalizedItem.Duration = ParseNullableMilliseconds(eventData, "Total-Latency")
            ?? ParseNullableMilliseconds(eventData, "Duration");
        finalizedItem.Chunks = 0;
        finalizedItem.TotalBytes = ParseLong(eventData, "Content-Length");
        finalizedItem.RequestContentLength = ParseLong(eventData, "RequestContentLength");
        finalizedItem.RequestHeadersText = requestHeaders;
        finalizedItem.ResponseHeadersText = responseHeaders;
        finalizedItem.RequestBodyDisplay = summaryBody;
        finalizedItem.ResponseBody = responseBody;
        finalizedItem.Phases = BuildPhaseView(phaseRecord, eventData, phaseKey);
        finalizedItem.IsComplete = true;
        finalizedItem.IsFailed = failed;
        finalizedItem.IsRunning = false;

        if (isNewItem)
        {
            _store.AddRequest(finalizedItem);
        }
        else
        {
            _store.MarkRequestFinalized(finalizedItem);
        }

        if (!string.IsNullOrWhiteSpace(correlationKey))
        {
            _requestLifecycle.Remove(correlationKey);
        }

        if (!string.IsNullOrWhiteSpace(phaseKey))
        {
            _requestPhases.Remove(phaseKey!);
        }
    }

    // A backend request is a single backend attempt within a request's lifetime. It carries the
    // endpoint-level metrics (x-PolicyCycleCounter, Request-Process-Duration, backendLog PAYGO /
    // throttle details) consumed by the Endpoints card. It is stored as its own item so the card
    // can aggregate per attempt, but it is excluded from the request panel and runtime stats, which
    // are owned by the final S7P-ProxyRequest. The lifecycle step is added but NOT removed here; the
    // final S7P-ProxyRequest owns lifecycle removal.
    private void ApplyBackendRequestEvent(IReadOnlyDictionary<string, string> eventData)
    {
        const string eventType = "S7P-BackendRequest";

        var correlationKey = GetCorrelationKey(eventData);
        if (!string.IsNullOrWhiteSpace(correlationKey))
        {
            TrackLifecycleEvent(eventData, eventType);
        }

        TrackRequestPhase(eventData, RequestPhase.Attempt);

        var statusCode = ParseNullableInt(eventData, "Status");
        var failed = statusCode is >= 400;
        var statusLabel = failed ? "Failed" : "Completed";

        _store.AddRequest(new MultiRequestStatusItem
        {
            ContainerApp = GetValue(eventData, "ContainerApp"),
            Replica = GetValue(eventData, "Replica"),
            UserId = GetValue(eventData, "UserID"),
            Status = statusLabel,
            StatusMessage = BuildStatusMessage(eventData, eventType, statusCode, statusLabel),
            EventType = eventType,
            BackendHost = BuildRequestBackendHost(eventData),
            EndpointKey = string.Empty,
            IsEndpointCircuitBreakerOpen = false,
            IsServerCircuitBreakerSignal = false,
            StatusCode = statusCode,
            ContentType = GetValue(eventData, "Content-Type", "-"),
            TimeToFirstByte = ParseNullableMilliseconds(eventData, "Request-Queue-Duration"),
            Duration = null,
            Chunks = 0,
            TotalBytes = ParseLong(eventData, "Content-Length"),
            RequestHeadersText = BuildRequestHeadersText(eventData),
            ResponseHeadersText = BuildResponseHeadersText(eventData, statusCode),
            RequestBodyDisplay = BuildRequestSummaryBody(eventData),
            ResponseBody = BuildResponseSummaryBody(eventData, BuildStatusMessage(eventData, eventType, statusCode, statusLabel)),
            IsComplete = true,
            IsFailed = failed,
        });
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

    private void RecordCircuitBreakerHistory(IReadOnlyDictionary<string, string> eventData)
    {
        var code = GetValue(eventData, "Code");
        if (!int.TryParse(code, out var errorCode))
        {
            errorCode = 500; // Default to server error
        }

        var hasReportedCount = int.TryParse(GetValue(eventData, "Count"), out var reportedCount);

        var backendHost = FirstNonEmpty(
            GetValue(eventData, "Backend-Host"),
            GetValue(eventData, "Attempt-1-Backend-Host"),
            GetValue(eventData, "Host-URL"),
            GetValue(eventData, "Attempt-1-Host-URL"));

        if (string.IsNullOrWhiteSpace(backendHost))
        {
            // Hostless event is server-level history only.
            _store.RecordServerCircuitBreakerEvent(errorCode, hasReportedCount ? reportedCount : null);
            return;
        }

        _store.RecordCircuitBreakerIssue(backendHost, errorCode);
    }

    private void RecordServerErrorHistory(IReadOnlyDictionary<string, string> eventData)
    {
        var statusCode = ParseNullableInt(eventData, "Status");
        var message = GetValue(eventData, "Message");
        var queueLength = ParseNullableInt(eventData, "QueueLength");
        var path = GetValue(eventData, "Path");

        _store.RecordServerErrorEvent(statusCode, message, queueLength, path);
    }

    private void RecordEnqueueHistory(IReadOnlyDictionary<string, string> eventData)
    {
        var queueLength = ParseNullableInt(eventData, "QueueLength");
        var activeHosts = ParseNullableInt(eventData, "ActiveHosts");
        var path = GetValue(eventData, "Path");

        _store.RecordEnqueueSuccess(queueLength, activeHosts, path);
    }

    private static int NormalizeSuccessRateThreshold(double rawThreshold)
    {
        if (rawThreshold <= 0)
        {
            return 80;
        }

        // Backend event can emit either 0.8 or 80. Normalize to percent.
        return rawThreshold <= 1 ? (int)Math.Round(rawThreshold * 100) : (int)Math.Round(rawThreshold);
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

    // Captures the raw field set for a request phase (enqueue / backend attempt / final proxy
    // response) keyed by the request's S7P-ID so the detail view can show the full lifecycle.
    private void TrackRequestPhase(IReadOnlyDictionary<string, string> eventData, RequestPhase phase)
    {
        var key = GetPhaseKey(eventData);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (!_requestPhases.TryGetValue(key, out var record))
        {
            record = new RequestPhaseRecord();
            _requestPhases[key] = record;
        }

        var snapshot = new Dictionary<string, string>(eventData, StringComparer.OrdinalIgnoreCase);
        switch (phase)
        {
            case RequestPhase.Enqueue:
                record.Enqueue = snapshot;
                break;
            case RequestPhase.Attempt:
                record.Attempts.Add(snapshot);
                break;
            case RequestPhase.Final:
                record.Final = snapshot;
                break;
        }
    }

    // Adds a still-running placeholder row to the request panel as soon as a request is enqueued,
    // so it's visible before the final S7P-ProxyRequest arrives. The final handler
    // (ApplyRequestEvent) mutates this same instance in place via
    // EventHubMonitorStore.MarkRequestFinalized rather than adding a second row.
    private void TrackPendingRequest(IReadOnlyDictionary<string, string> eventData)
    {
        var key = GetPhaseKey(eventData);
        if (string.IsNullOrWhiteSpace(key) || !_requestPhases.TryGetValue(key, out var record))
        {
            return;
        }

        var enqueuedAt = ParseNullableDateTimeOffset(eventData, "Date") ?? DateTimeOffset.UtcNow;
        var pendingItem = new MultiRequestStatusItem
        {
            ContainerApp = GetValue(eventData, "ContainerApp"),
            Replica = GetValue(eventData, "Replica"),
            UserId = GetValue(eventData, "UserID"),
            Path = GetValue(eventData, "Path"),
            Status = "Running",
            StatusMessage = "Enqueued, awaiting completion...",
            EventType = "S7P-ProxyRequestEnqueued",
            EnqueuedAtUtc = enqueuedAt,
            IsRunning = true,
            IsComplete = false,
            IsFailed = false,
            Phases = BuildPendingPhaseView(record, key),
        };

        record.PendingItem = pendingItem;
        _store.AddRequest(pendingItem);
    }

    // A minimal phase view for a request that has only been enqueued so far: no Final tab is
    // included (the request hasn't completed yet), so the popup doesn't show stale/duplicate data.
    private static EventHub.RequestPhaseView BuildPendingPhaseView(RequestPhaseRecord record, string key)
    {
        return new EventHub.RequestPhaseView
        {
            SevenPId = key,
            Enqueue = record.Enqueue is { } enqueue ? ToOrderedFields(enqueue) : null,
        };
    }

    private static string? GetPhaseKey(IReadOnlyDictionary<string, string> eventData)
    {
        // S7P-ID is the stable request identity shared by the enqueue, every backend attempt (whose
        // MID carries an attempt suffix like "-234-1"), and the final proxy response. Fall back to
        // MID for events that omit it.
        return FirstNonEmpty(
            GetValue(eventData, "S7P-ID"),
            GetValue(eventData, "MID"));
    }

    private string BuildEnqueuePhaseText(RequestPhaseRecord? record, IReadOnlyDictionary<string, string> finalData)
    {
        var enqueue = record?.Enqueue;
        if (enqueue is null || enqueue.Count == 0)
        {
            return BuildRequestHeadersText(finalData);
        }

        return $"Enqueue:{Environment.NewLine}{FormatPhaseFields(enqueue)}";
    }

    private string BuildAttemptAndFinalPhaseText(
        RequestPhaseRecord? record,
        IReadOnlyDictionary<string, string> finalData,
        int? statusCode)
    {
        var sections = new List<string>();

        if (record is not null)
        {
            for (var index = 0; index < record.Attempts.Count; index++)
            {
                sections.Add($"Attempt {index + 1}:{Environment.NewLine}{FormatPhaseFields(record.Attempts[index])}");
            }
        }

        var final = record?.Final ?? finalData;
        var finalHeader = statusCode is int code ? $"Final (HTTP {code}):" : "Final:";
        sections.Add($"{finalHeader}{Environment.NewLine}{FormatPhaseFields(final)}");

        return string.Join($"{Environment.NewLine}{Environment.NewLine}", sections);
    }

    private static string FormatPhaseFields(IReadOnlyDictionary<string, string> fields)
    {
        return string.Join(
            Environment.NewLine,
            fields
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}: {pair.Value}"));
    }

    // Builds the structured per-phase view consumed by the EventHub request popup (enqueue tab,
    // one tab per backend attempt, and the final proxy-request tab).
    private EventHub.RequestPhaseView BuildPhaseView(
        RequestPhaseRecord? record,
        IReadOnlyDictionary<string, string> finalData,
        string? key)
    {
        var attempts = new List<IReadOnlyList<KeyValuePair<string, string>>>();
        string? backendLog = null;

        if (record is not null)
        {
            foreach (var attempt in record.Attempts)
            {
                attempts.Add(ToOrderedFields(attempt));
                var attemptLog = GetValue(attempt, "backendLog");
                if (!string.IsNullOrWhiteSpace(attemptLog))
                {
                    backendLog = attemptLog;
                }
            }
        }

        var final = record?.Final ?? finalData;
        var finalLog = FirstNonEmpty(
            GetValue(final, "backendLog"),
            GetValue(final, "Attempt-1-backendLog"));
        if (!string.IsNullOrWhiteSpace(finalLog))
        {
            backendLog = finalLog;
        }

        return new EventHub.RequestPhaseView
        {
            SevenPId = key ?? string.Empty,
            Enqueue = record?.Enqueue is { } enqueue ? ToOrderedFields(enqueue) : null,
            Attempts = attempts,
            Final = ToOrderedFields(final),
            BackendLog = backendLog,
        };
    }

    private static IReadOnlyList<KeyValuePair<string, string>> ToOrderedFields(IReadOnlyDictionary<string, string> fields)
    {
        return fields
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
        // GUID is stable across a request's lifetime (enqueue, backend attempts, final proxy
        // response) while MID differs per backend attempt; prefer GUID, fall back to MID.
        return EventFields.CorrelationKey(eventData);
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

    private EventHubConsumerClient CreateConsumerClient(
        ReaderSettings settings,
        EventHubConsumerClientOptions clientOptions)
    {
        if (!string.IsNullOrWhiteSpace(settings.ConnectionString))
        {
            _logger.LogInformation(
                "[EVENTHUB] Creating consumer client for {EventHubName} using connection string.",
                settings.EventHubName);
            
            return new EventHubConsumerClient(
                settings.ConsumerGroup,
                settings.ConnectionString,
                settings.EventHubName!,
                clientOptions);
        }

        if (_credential is null)
        {
            throw new InvalidOperationException(
                "Cannot create Event Hub consumer client: DefaultAzureCredential is not available and no connection string was provided.");
        }

        _logger.LogInformation(
            "[EVENTHUB] Creating consumer client for {EventHubName} in namespace {EventHubNamespace} using DefaultAzureCredential.",
            settings.EventHubName,
            settings.EventHubNamespace);

        return new EventHubConsumerClient(
            settings.ConsumerGroup,
            settings.EventHubNamespace!,
            settings.EventHubName!,
            _credential,
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
        if (Uri.TryCreate(host, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        var embeddedUri = ExtractFirstAbsoluteUri(host);
        if (embeddedUri is not null)
        {
            return embeddedUri.Host;
        }

        return host;
    }

    private static string BuildBackendHostKey(string host)
    {
        if (Uri.TryCreate(host, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        var embeddedUri = ExtractFirstAbsoluteUri(host);
        if (embeddedUri is not null)
        {
            return embeddedUri.Host;
        }

        return host.Trim();
    }

    private static string BuildRequestBackendHost(IReadOnlyDictionary<string, string> eventData)
    {
        var raw = FirstNonEmpty(
            GetValue(eventData, "Backend-Host"),
            GetValue(eventData, "Attempt-1-Backend-Host"),
            GetValue(eventData, "Host-URL"),
            GetValue(eventData, "Attempt-1-Host-URL"));

        if (string.IsNullOrWhiteSpace(raw)
            || raw.Contains("No Active Hosts Available", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        return raw.Trim();
    }

    private static Uri? ExtractFirstAbsoluteUri(string value)
    {
        foreach (var part in value.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (Uri.TryCreate(part, UriKind.Absolute, out var uri))
            {
                return uri;
            }
        }

        return null;
    }

    private static string ResolveBackendCss(string status, int successRate, int successThresholdPercent)
    {
        // Backend CB semantic: success-rate below threshold means tripped/degraded backend.
        if (successRate < successThresholdPercent)
        {
            return "down";
        }

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

    private static string BuildEndpointKey(IReadOnlyDictionary<string, string> eventData)
    {
        var path = NormalizeUrlPath(GetPath(eventData));
        var model = FirstNonEmpty(
            GetValue(eventData, "model"),
            GetValue(eventData, "Model"),
            GetValue(eventData, "ModelName"),
            GetValue(eventData, "ModelDisplayName"),
            GetValue(eventData, "ModelId"),
            GetValue(eventData, "ModelKey"),
            GetValue(eventData, "x-model"),
            GetValue(eventData, "x-model-name"),
            GetValue(eventData, "x-model-id"),
            GetValue(eventData, "Deployment"),
            GetValue(eventData, "DeploymentName"),
            GetValue(eventData, "ModelDeployment"),
            GetValue(eventData, "AOAIModel"));

        return string.IsNullOrWhiteSpace(model)
            ? path
            : $"{path} | model={model}";
    }

    private static string NormalizeUrlPath(string pathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl))
        {
            return "/";
        }

        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var absoluteUri))
        {
            return string.IsNullOrWhiteSpace(absoluteUri.AbsolutePath) ? "/" : absoluteUri.AbsolutePath;
        }

        var pathOnly = pathOrUrl;
        var queryIndex = pathOnly.IndexOf('?');
        if (queryIndex >= 0)
        {
            pathOnly = pathOnly[..queryIndex];
        }

        var fragmentIndex = pathOnly.IndexOf('#');
        if (fragmentIndex >= 0)
        {
            pathOnly = pathOnly[..fragmentIndex];
        }

        return string.IsNullOrWhiteSpace(pathOnly) ? "/" : pathOnly;
    }

    private static bool IsEndpointCircuitBreakerSignal(
        IReadOnlyDictionary<string, string> eventData,
        string eventType,
        int? statusCode)
    {
        var message = FirstNonEmpty(
            GetValue(eventData, "Message"),
            GetValue(eventData, "ErrorDetail"),
            GetValue(eventData, "Error"),
            GetValue(eventData, "ErrorMessage"),
            GetValue(eventData, "backendLog"),
            GetValue(eventData, "Attempt-1-backendLog"));

        if (!string.IsNullOrWhiteSpace(message)
            && (message.Contains("No active hosts", StringComparison.OrdinalIgnoreCase)
                || message.Contains("CALL INCOMPLETE", StringComparison.OrdinalIgnoreCase)
                || message.Contains("CircuitBreaker", StringComparison.OrdinalIgnoreCase)
                || message.Contains("THROTTLED", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return string.Equals(eventType, "S7P-ProxyRequest", StringComparison.OrdinalIgnoreCase)
            && statusCode is >= 500;
    }

    private static bool IsServerCircuitBreakerSignal(
        IReadOnlyDictionary<string, string> eventData,
        string eventType,
        int? statusCode)
    {
        if (string.Equals(eventType, "S7P-CircuitBreakerError", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsEndpointCircuitBreakerSignal(eventData, eventType, statusCode);
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
        AddHeaderLine(lines, "x-PolicyCycleCounter", GetValue(eventData, "x-PolicyCycleCounter"));
        AddHeaderLine(lines, "Request-Process-Duration", GetValue(eventData, "Request-Process-Duration"));
        AddHeaderLine(lines, "Request-Queue-Duration", GetValue(eventData, "Request-Queue-Duration"));
        AddHeaderLine(lines, "Model", FirstNonEmpty(
            GetValue(eventData, "model"),
            GetValue(eventData, "Model"),
            GetValue(eventData, "ModelName"),
            GetValue(eventData, "ModelDisplayName"),
            GetValue(eventData, "ModelId"),
            GetValue(eventData, "ModelKey"),
            GetValue(eventData, "x-model"),
            GetValue(eventData, "x-model-name"),
            GetValue(eventData, "x-model-id"),
            GetValue(eventData, "Deployment"),
            GetValue(eventData, "DeploymentName"),
            GetValue(eventData, "ModelDeployment"),
            GetValue(eventData, "AOAIModel")));
        AddHeaderLine(lines, "x-backend-label", GetValue(eventData, "x-backend-label"));
        AddHeaderLine(lines, "x-Backend-Attempts", GetValue(eventData, "x-Backend-Attempts"));

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
        AddHeaderLine(lines, "x-backend-label", GetValue(eventData, "x-backend-label"));
        AddHeaderLine(lines, "backendLog", GetValue(eventData, "backendLog"));
        AddHeaderLine(lines, "retry-after", GetValue(eventData, "retry-after"));

        // Keep all raw event fields available in hover details for discrepancy analysis.
        lines.Add("EventData:");
        foreach (var pair in eventData.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(pair.Value))
            {
                lines.Add($"{pair.Key}: {pair.Value}");
            }
        }

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
        return EventFields.FirstNonEmpty(values);
    }

    private static string GetValue(
        IReadOnlyDictionary<string, string> eventData,
        string key,
        string fallback = "")
    {
        return EventFields.Get(eventData, key, fallback);
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

    private static DateTimeOffset? ParseNullableDateTimeOffset(IReadOnlyDictionary<string, string> eventData, string key)
    {
        return DateTimeOffset.TryParse(
            GetValue(eventData, key),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out var value)
            ? value
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