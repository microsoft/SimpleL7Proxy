using System.Text.Json;
using System.Text.RegularExpressions;
using chat_tester.Components.Shared.EventHub;

namespace chat_tester.Components.Shared;

/// <summary>
/// Canonical metric groups and definitions for proxy observability.
/// </summary>
public sealed class ProxyMetricsCatalog
{
    private const string AllScopeId = "all";
    private const string UnknownValue = "-";
    private static readonly string[] ModelKeys =
    {
        "model",
        "Model",
        "ModelName",
        "ModelDisplayName",
        "ModelId",
        "ModelKey",
        "x-model",
        "x-model-name",
        "x-model-id",
        "Deployment",
        "DeploymentName",
        "ModelDeployment",
        "AOAIModel",
    };

    private readonly object _gate = new();
    private IReadOnlyDictionary<string, string> _activeValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ScopeState> _scopeStates = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastPublishedUtc;

    public MetricGroup Endpoints { get; } = new(
        "Endpoints",
        new[]
        {
            new MetricDefinition("Endpoint count", "Total unique endpoints observed from Event Hub records."),
            new MetricDefinition("Endpoint preview", "Representative endpoint paths observed in the current run."),
            new MetricDefinition("Load balancing mode", "Current endpoint routing strategy reported by the proxy."),
            new MetricDefinition("Queueing delay", "Observed queue wait before backend dispatch."),
            new MetricDefinition("Connection establishment time", "TLS handshake and TCP connect duration when available."),
        });

    public MetricGroup Models { get; } = new(
        "Models",
        new[]
        {
            new MetricDefinition("Model count", "Total unique LLM models observed from Event Hub records."),
            new MetricDefinition("Model distribution", "Representative set of model names/deployments used by requests."),
        });

    public MetricGroup Backends { get; } = new(
        "Backends",
        new[]
        {
            new MetricDefinition("Backend count", "Total backend hosts observed from Event Hub records."),
            new MetricDefinition("Upstream availability", "Whether backend endpoints are reachable."),
            new MetricDefinition("Upstream latency", "Average backend latency across hosts in the current run."),
            new MetricDefinition("Retry counts", "High retries indicate backend instability."),
            new MetricDefinition("Circuit breaker state", "Open and half-open states indicate degraded upstreams."),
        });

    public MetricGroup Server { get; } = new(
        "Server",
        new[]
        {
            new MetricDefinition("Proxy version", "Version string reported by the active proxy instance."),
            new MetricDefinition("CPU usage", "High CPU can indicate slow routing or dropped connections."),
            new MetricDefinition("Memory usage", "Helps detect leaks or oversized buffers."),
            new MetricDefinition("Open connections", "Helps identify connection storms or slow clients."),
            new MetricDefinition("Thread/worker pool saturation", "Critical for proxies such as Envoy, NGINX, and HAProxy."),
        });

    public MetricGroup Request { get; } = new(
        "Request",
        new[]
        {
            new MetricDefinition("Request rate", "Request volume from the current processing run."),
            new MetricDefinition("Success vs failure counts", "2xx/3xx/4xx/5xx distribution from request events."),
            new MetricDefinition("Request size", "Average incoming Event Hub payload size."),
            new MetricDefinition("Response size", "Response payload size when available from events."),
            new MetricDefinition("Total request latency", "End-to-end request latency from request event fields."),
            new MetricDefinition("Suspicious patterns", "Sudden spikes in 4xx/5xx, odd user agents, or repeated paths."),
        });

    public IReadOnlyList<MetricGroup> AllGroups =>
        new[]
        {
            Endpoints,
            Models,
            Backends,
            Server,
            Request,
        };

    public DateTimeOffset LastPublishedUtc
    {
        get
        {
            lock (_gate)
            {
                return _lastPublishedUtc;
            }
        }
    }

    public event Action? Changed;

    public IReadOnlyList<ScopeOption> GetScopeOptions()
    {
        lock (_gate)
        {
            var options = new List<ScopeOption>
            {
                new ScopeOption(AllScopeId, "All proxy instances", string.Empty, string.Empty, true, false),
            };

            var appOptions = _scopeStates.Values
                .Where(state => !string.IsNullOrWhiteSpace(state.ContainerApp) && string.IsNullOrWhiteSpace(state.Replica))
                .OrderBy(state => state.ContainerApp, StringComparer.OrdinalIgnoreCase)
                .Select(state => new ScopeOption(
                    state.ScopeId,
                    $"ContainerApp: {state.ContainerApp}",
                    state.ContainerApp,
                    string.Empty,
                    false,
                    false));

            var replicaOptions = _scopeStates.Values
                .Where(state => !string.IsNullOrWhiteSpace(state.ContainerApp) && !string.IsNullOrWhiteSpace(state.Replica))
                .OrderBy(state => state.ContainerApp, StringComparer.OrdinalIgnoreCase)
                .ThenBy(state => state.Replica, StringComparer.OrdinalIgnoreCase)
                .Select(state => new ScopeOption(
                    state.ScopeId,
                    $"Replica: {state.ContainerApp}/{state.Replica}",
                    state.ContainerApp,
                    state.Replica,
                    false,
                    true));

            options.AddRange(appOptions);
            options.AddRange(replicaOptions);
            return options;
        }
    }

    public bool IsKnownScope(string? scopeId)
    {
        if (string.IsNullOrWhiteSpace(scopeId) || string.Equals(scopeId, AllScopeId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        lock (_gate)
        {
            return _scopeStates.ContainsKey(scopeId);
        }
    }

    public void Publish(IReadOnlyList<ParsedEventRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var now = DateTimeOffset.UtcNow;
        var computed = BuildScopeValues(records);
        lock (_gate)
        {
            _activeValues = computed;
            _lastPublishedUtc = now;
            UpdateScopeStates(records, now);
        }

        Changed?.Invoke();
    }

    public IReadOnlyList<MetricGroupSnapshot> GetActiveGroups()
    {
        return GetActiveGroups(AllScopeId);
    }

    public IReadOnlyList<MetricGroupSnapshot> GetActiveGroups(string? scopeId)
    {
        lock (_gate)
        {
            var resolvedValues = ResolveScopeValues(scopeId);
            return AllGroups
                .Select(group => new MetricGroupSnapshot(
                    group.Name,
                    group.Metrics
                        .Select(metric => new ActiveMetricDefinition(
                            metric.Name,
                            metric.Description,
                            ResolveActiveValue(metric.Name, resolvedValues)))
                        .ToArray()))
                .ToArray();
        }
    }

    private IReadOnlyDictionary<string, string> ResolveScopeValues(string? scopeId)
    {
        if (string.IsNullOrWhiteSpace(scopeId) || string.Equals(scopeId, AllScopeId, StringComparison.OrdinalIgnoreCase))
        {
            return _activeValues;
        }

        return _scopeStates.TryGetValue(scopeId, out var state)
            ? state.Values
            : _activeValues;
    }

    private void UpdateScopeStates(IReadOnlyList<ParsedEventRecord> parsedRecords, DateTimeOffset now)
    {
        if (parsedRecords.Count == 0)
        {
            return;
        }

        var appGroups = parsedRecords
            .Where(record => !string.IsNullOrWhiteSpace(record.ContainerApp))
            .GroupBy(record => record.ContainerApp, StringComparer.OrdinalIgnoreCase);

        foreach (var appGroup in appGroups)
        {
            var appId = BuildContainerAppScopeId(appGroup.Key);
            _scopeStates[appId] = new ScopeState(
                appId,
                appGroup.Key,
                string.Empty,
                BuildScopeValues(appGroup.ToArray()),
                now);

            var replicaGroups = appGroup
                .Where(record => !string.IsNullOrWhiteSpace(record.Replica))
                .GroupBy(record => record.Replica, StringComparer.OrdinalIgnoreCase);

            foreach (var replicaGroup in replicaGroups)
            {
                var replicaId = BuildReplicaScopeId(appGroup.Key, replicaGroup.Key);
                _scopeStates[replicaId] = new ScopeState(
                    replicaId,
                    appGroup.Key,
                    replicaGroup.Key,
                    BuildScopeValues(replicaGroup.ToArray()),
                    now);
            }
        }
    }

    private static IReadOnlyDictionary<string, string> BuildScopeValues(IReadOnlyList<ParsedEventRecord> records)
    {
        var server = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var backend = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var endpoint = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var requests = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in records)
        {
            var data = record.Data;
            var eventType = EventFields.Get(data, "Type");

            foreach (var pair in data)
            {
                server[pair.Key] = pair.Value;
                endpoint[pair.Key] = pair.Value;
            }

            if (string.Equals(eventType, "S7P-Backend", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var pair in data)
                {
                    backend[pair.Key] = pair.Value;
                }
            }

            var correlationKey = EventFields.CorrelationKey(data);
            if (string.IsNullOrWhiteSpace(correlationKey))
            {
                continue;
            }

            var status = EventFields.Get(data, "Status");
            var path = EventFields.Get(data, "Path");
            requests[correlationKey] = string.Join(
                "|",
                new[] { eventType, status, path }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        return BuildActiveValues(server, backend, endpoint, requests, records);
    }

    private static string BuildContainerAppScopeId(string containerApp)
    {
        return $"app:{containerApp}";
    }

    private static string BuildReplicaScopeId(string containerApp, string replica)
    {
        return $"replica:{containerApp}:{replica}";
    }

    private static string ResolveActiveValue(string metricName, IReadOnlyDictionary<string, string> activeValues)
    {
        return activeValues.TryGetValue(metricName, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : UnknownValue;
    }

    private static IReadOnlyDictionary<string, string> BuildActiveValues(
        IReadOnlyDictionary<string, string> server,
        IReadOnlyDictionary<string, string> backend,
        IReadOnlyDictionary<string, string> endpoint,
        IReadOnlyDictionary<string, string> requests,
        IReadOnlyList<ParsedEventRecord> records)
    {
        var recordInsights = BuildRecordInsights(records, backend, endpoint);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Endpoint count"] = recordInsights.EndpointCount.ToString("N0"),
            ["Endpoint preview"] = recordInsights.EndpointPreview,
            ["Load balancing mode"] = ReadFirstNonEmpty(server, "LoadBalanceMode") ?? UnknownValue,
            ["Queueing delay"] = ReadFirstNonEmpty(endpoint, "Request-Queue-Duration") ?? UnknownValue,
            ["Connection establishment time"] = ReadFirstNonEmpty(endpoint, "Connection-Establishment-Time", "TLS-Handshake-Duration", "Tcp-Connect-Duration") ?? UnknownValue,
            ["Model count"] = recordInsights.ModelCount.ToString("N0"),
            ["Model distribution"] = recordInsights.ModelPreview,
            ["Backend count"] = recordInsights.BackendCount.ToString("N0"),
            ["Upstream availability"] = BuildUpstreamAvailabilityValue(backend, server),
            ["Upstream latency"] = BuildAverageBackendLatency(backend),
            ["Retry counts"] = BuildRetryValue(requests),
            ["Circuit breaker state"] = BuildCircuitBreakerValue(requests),
            ["Proxy version"] = ReadFirstNonEmpty(server, "Ver") ?? UnknownValue,
            ["CPU usage"] = ReadFirstNonEmpty(server, "CPU", "CPU-Usage") ?? UnknownValue,
            ["Memory usage"] = ReadFirstNonEmpty(server, "Memory", "Memory-Usage") ?? UnknownValue,
            ["Open connections"] = ReadFirstNonEmpty(server, "OpenConnections", "Open-Connections") ?? UnknownValue,
            ["Thread/worker pool saturation"] = ReadFirstNonEmpty(server, "WorkerPoolSaturation", "ThreadPoolSaturation") ?? UnknownValue,
            ["Request rate"] = BuildRequestRateValue(records, requests),
            ["Success vs failure counts"] = BuildSuccessFailureValue(recordInsights),
            ["Request size"] = BuildRequestSizeValue(records),
            ["Response size"] = ReadFirstNonEmpty(endpoint, "Content-Length", "Response-Content-Length")
                ?? ReadFirstNonEmpty(server, "Response-Content-Length", "Content-Length")
                ?? UnknownValue,
            ["Total request latency"] = ReadFirstNonEmpty(endpoint, "Total-Latency", "Duration") ?? UnknownValue,
            ["Suspicious patterns"] = BuildSuspiciousPatternsValue(requests),
        };

        return values;
    }

    private static string BuildRequestRateValue(IReadOnlyList<ParsedEventRecord> records, IReadOnlyDictionary<string, string> requests)
    {
        return requests.Count == 0
            ? UnknownValue
            : $"{requests.Count} req/run from {records.Count} event(s)";
    }

    private static string BuildSuccessFailureValue(RecordInsights recordInsights)
    {
        return $"2xx={recordInsights.Status2xx} 3xx={recordInsights.Status3xx} 4xx={recordInsights.Status4xx} 5xx={recordInsights.Status5xx}";
    }

    private static string BuildRequestSizeValue(IReadOnlyList<ParsedEventRecord> records)
    {
        if (records.Count == 0)
        {
            return UnknownValue;
        }

        var totalSize = records.Sum(record => record.Raw.Length);
        var averageSize = totalSize / (double)records.Count;
        return $"avg {averageSize:0} B";
    }

    private static string BuildAverageBackendLatency(IReadOnlyDictionary<string, string> backend)
    {
        var count = 0;
        var total = 0.0;

        foreach (var pair in backend)
        {
            if (!pair.Key.EndsWith("-Latency", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!double.TryParse(pair.Value, out var latency))
            {
                continue;
            }

            count++;
            total += latency;
        }

        return count == 0 ? UnknownValue : $"{total / count:0} ms";
    }

    private static string BuildUpstreamAvailabilityValue(
        IReadOnlyDictionary<string, string> backend,
        IReadOnlyDictionary<string, string> server)
    {
        var active = 0;
        var degraded = 0;
        var total = 0;

        foreach (var pair in backend)
        {
            if (!pair.Key.EndsWith("-Status", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            total++;
            if (pair.Value.Contains("active", StringComparison.OrdinalIgnoreCase))
            {
                active++;
            }
            else
            {
                degraded++;
            }
        }

        if (total == 0 && int.TryParse(ReadFirstNonEmpty(server, "ActiveHostsCount"), out var activeHostsFromServer))
        {
            return $"active={activeHostsFromServer}/{activeHostsFromServer} degraded=0";
        }

        return total == 0 ? UnknownValue : $"active={active}/{total} degraded={degraded}";
    }

    private static string BuildRetryValue(IReadOnlyDictionary<string, string> requests)
    {
        var retries = requests.Values.Count(value => value.Contains("ProxyRequestRequeued", StringComparison.OrdinalIgnoreCase));
        return retries.ToString("N0");
    }

    private static string BuildCircuitBreakerValue(IReadOnlyDictionary<string, string> requests)
    {
        var openSignals = requests.Values.Count(value => value.Contains("CircuitBreaker", StringComparison.OrdinalIgnoreCase));
        return openSignals == 0 ? "closed/unknown" : $"open-signals={openSignals}";
    }

    private static string BuildSuspiciousPatternsValue(IReadOnlyDictionary<string, string> requests)
    {
        var suspicious = requests.Values.Count(value => value.Contains("|5", StringComparison.OrdinalIgnoreCase));
        return suspicious == 0 ? "none detected" : $"{suspicious} possible pattern(s)";
    }

    private static RecordInsights BuildRecordInsights(
        IReadOnlyList<ParsedEventRecord> records,
        IReadOnlyDictionary<string, string> backend,
        IReadOnlyDictionary<string, string> endpoint)
    {
        var backendHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var endpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var status2xx = 0;
        var status3xx = 0;
        var status4xx = 0;
        var status5xx = 0;

        foreach (var pair in backend)
        {
            if (pair.Key.EndsWith("-Host", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pair.Value))
            {
                backendHosts.Add(pair.Value.Trim());
            }
        }

        var endpointPath = ReadFirstNonEmpty(endpoint, "Path");
        if (!string.IsNullOrWhiteSpace(endpointPath))
        {
            endpoints.Add(endpointPath);
        }

        foreach (var record in records)
        {
            var data = record.Data;

            var path = EventFields.Get(data, "Path");
            if (!string.IsNullOrWhiteSpace(path))
            {
                endpoints.Add(path.Trim());
            }

            var backendLog = EventFields.Get(data, "backendLog");
            if (!string.IsNullOrWhiteSpace(backendLog))
            {
                foreach (var endpointTarget in ExtractEndpointsFromBackendLog(backendLog))
                {
                    endpoints.Add(endpointTarget);
                }
            }

            var uri = EventFields.Get(data, "Uri");
            if (Uri.TryCreate(uri, UriKind.Absolute, out var endpointUri))
            {
                endpoints.Add(endpointUri.AbsolutePath);
            }

            var backendHost = EventFields.Get(data, "Backend-Host");
            if (!string.IsNullOrWhiteSpace(backendHost))
            {
                backendHosts.Add(backendHost.Trim());
            }

            foreach (var modelKey in ModelKeys)
            {
                var modelValue = EventFields.Get(data, modelKey);
                if (!string.IsNullOrWhiteSpace(modelValue))
                {
                    models.Add(modelValue.Trim());
                }
            }

            if (int.TryParse(EventFields.Get(data, "Status"), out var statusCode))
            {
                if (statusCode is >= 200 and < 300)
                {
                    status2xx++;
                }
                else if (statusCode is >= 300 and < 400)
                {
                    status3xx++;
                }
                else if (statusCode is >= 400 and < 500)
                {
                    status4xx++;
                }
                else if (statusCode is >= 500 and < 600)
                {
                    status5xx++;
                }
            }
        }

        return new RecordInsights(
            backendHosts.Count,
            endpoints.Count,
            BuildPreview(endpoints),
            models.Count,
            BuildPreview(models),
            status2xx,
            status3xx,
            status4xx,
            status5xx);
    }

    private static string BuildPreview(IEnumerable<string> values)
    {
        var top = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Take(3)
            .ToArray();

        return top.Length == 0 ? UnknownValue : string.Join(", ", top);
    }

    private static IReadOnlyList<string> ExtractEndpointsFromBackendLog(string backendLog)
    {
        var matches = Regex.Matches(
            backendLog,
            @"using\s+[^|]*?url:\s*(?<url>\S+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var endpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in matches)
        {
            var urlValue = match.Groups["url"].Value.Trim();
            if (string.IsNullOrWhiteSpace(urlValue))
            {
                continue;
            }

            if (Uri.TryCreate(urlValue, UriKind.Absolute, out var absoluteUri))
            {
                endpoints.Add($"{absoluteUri.Scheme}://{absoluteUri.Host}{absoluteUri.AbsolutePath}");
                continue;
            }

            endpoints.Add(urlValue);
        }

        return endpoints.Count == 0
            ? Array.Empty<string>()
            : endpoints.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? ReadFirstNonEmpty(IReadOnlyDictionary<string, string> source, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (source.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    public sealed record MetricGroup(string Name, IReadOnlyList<MetricDefinition> Metrics);

    public sealed record MetricDefinition(string Name, string Description);

    public sealed record ActiveMetricDefinition(string Name, string Description, string Value);

    public sealed record MetricGroupSnapshot(string Name, IReadOnlyList<ActiveMetricDefinition> Metrics);

    public sealed record ScopeOption(
        string Id,
        string Label,
        string ContainerApp,
        string Replica,
        bool IsAll,
        bool IsReplica);

    private readonly record struct RecordInsights(
        int BackendCount,
        int EndpointCount,
        string EndpointPreview,
        int ModelCount,
        string ModelPreview,
        int Status2xx,
        int Status3xx,
        int Status4xx,
        int Status5xx);

    private sealed record ScopeState(
        string ScopeId,
        string ContainerApp,
        string Replica,
        IReadOnlyDictionary<string, string> Values,
        DateTimeOffset LastUpdatedUtc);
}
