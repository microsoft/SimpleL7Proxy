using System.Globalization;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using CompanionApp.Components.Shared;
using CompanionApp.Components.Shared.EventHub;

namespace CompanionApp.Observability;

/// <summary>Request analytics shared by Insights and Event Hub for one publication and filter query.</summary>
public sealed class ObservabilityRequestProjection
{
    private readonly MonitorSnapshot _snapshot;
    private readonly IReadOnlyDictionary<MultiRequestStatusItem, ObservedRequestSnapshot> _sourceRequests;
    private readonly IReadOnlyList<ProxyMetricsCatalog.ScopeOption> _scopeOptions;
    private readonly string EffectiveScopeId;
    private readonly string SelectedEnvironmentFilter;
    private readonly string SelectedUserFilter;
    private readonly string SelectedModelFilter;
    private const bool IsConnected = true;

    internal ObservabilityRequestProjection(MonitorSnapshot snapshot, ObservabilityMonitorSnapshot frozenSnapshot, IReadOnlyDictionary<MultiRequestStatusItem, ObservedRequestSnapshot> sourceRequests, IReadOnlyList<ProxyMetricsCatalog.ScopeOption> scopes, string scopeId, string environment, string user, string model)
    {
        _snapshot = snapshot;
        Snapshot = frozenSnapshot;
        _sourceRequests = sourceRequests;
        _scopeOptions = scopes;
        EffectiveScopeId = scopeId;
        SelectedEnvironmentFilter = environment;
        SelectedUserFilter = user;
        SelectedModelFilter = model;
        AnalyticsRequests = FilterHistoryItemsByScope().Where(MatchesSelectedFilters).ToImmutableArray();
        FilteredRequests = AnalyticsRequests.Select(item => _sourceRequests[item]).ToImmutableArray();
        HistoryItems = FilteredRequests.Where(item => !string.Equals(item.EventType, "S7P-BackendRequest", StringComparison.OrdinalIgnoreCase)).ToImmutableArray();
        EnvironmentFilterOptions = BuildEnvironmentFilterOptions();
        UserFilterOptions = BuildUserFilterOptions();
        ModelFilterOptions = BuildModelFilterOptions();
        EndpointTiles = BuildEndpointTiles();
        SuccessfulPaths = BuildPathCounts(true);
        FailedPaths = BuildPathCounts(false);
        SuccessfulUsers = BuildUserCounts(true);
        FailedUsers = BuildUserCounts(false);
    }

    public ObservabilityMonitorSnapshot Snapshot { get; }
    public IReadOnlyList<ObservedRequestSnapshot> FilteredRequests { get; }
    public IReadOnlyList<ObservedRequestSnapshot> HistoryItems { get; }
    internal MonitorSnapshot AnalyticsSnapshot => _snapshot;
    internal IReadOnlyList<MultiRequestStatusItem> AnalyticsRequests { get; }
    internal ObservedRequestSnapshot GetSourceRequest(MultiRequestStatusItem item) => _sourceRequests[item];
    public IReadOnlyList<string> EnvironmentFilterOptions { get; }
    public IReadOnlyList<string> UserFilterOptions { get; }
    public IReadOnlyList<string> ModelFilterOptions { get; }
    public IReadOnlyList<EndpointTile> EndpointTiles { get; }
    public IReadOnlyList<PathCount> SuccessfulPaths { get; }
    public IReadOnlyList<PathCount> FailedPaths { get; }
    public IReadOnlyList<UserCount> SuccessfulUsers { get; }
    public IReadOnlyList<UserCount> FailedUsers { get; }

    private IReadOnlyList<EndpointTile> BuildEndpointTiles()
    {
        var filteredRequests = GetFilteredRequestItems();
        if (!IsConnected || !_snapshot.HasData || filteredRequests.Count == 0)
        {
            return Array.Empty<EndpointTile>();
        }

        var byModelAndTarget = new Dictionary<string, Dictionary<string, EndpointAggregate>>(StringComparer.OrdinalIgnoreCase);
        var backendFallbackRequestIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requestModelById = BuildRequestModelMap(filteredRequests);
        var requestLabelById = BuildRequestLabelMap(filteredRequests);

        // Endpoints are derived solely from the backend target inside backendLog
        // (e.g. "Using PAYGO URL: https://..."). Request paths are NOT endpoints.
        // Both S7P-BackendRequest (single attempt) and the final S7P-ProxyRequest
        // (which carries every Attempt-N-backendLog) contribute. The same attempt's
        // log is emitted identically by both, so dedupe on the exact log string to
        // avoid double counting while still picking up retry attempts that only
        // appear inside the ProxyRequest summary.
        var seenLogs = new HashSet<string>(StringComparer.Ordinal);

        static string NormalizeModel(string? model)
        {
            return string.IsNullOrWhiteSpace(model) ? "(unknown model)" : model.Trim();
        }

        static string NormalizePriority(string? priority)
        {
            return string.IsNullOrWhiteSpace(priority) ? "(none)" : priority.Trim();
        }

        static string BuildPrioritySummary(IDictionary<string, int> counts)
        {
            if (counts.Count == 0)
            {
                return "(none)";
            }

            return string.Join(", ", counts
                .OrderBy(pair => int.TryParse(pair.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? 0 : 1)
                .ThenBy(pair =>
                {
                    return int.TryParse(pair.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric)
                        ? numeric
                        : int.MaxValue;
                })
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"P{pair.Key}:{pair.Value}"));
        }

        static int GetPriorityCount(IDictionary<string, int> counts, string targetPriority)
        {
            if (counts.Count == 0)
            {
                return 0;
            }

            var normalizedTarget = targetPriority.Trim();
            var total = 0;
            foreach (var pair in counts)
            {
                var key = pair.Key.Trim();
                if (key.StartsWith("P", StringComparison.OrdinalIgnoreCase))
                {
                    key = key[1..];
                }

                if (string.Equals(key, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                {
                    total += pair.Value;
                }
            }

            return total;
        }

        EndpointAggregate GetOrCreateAggregate(string modelName, string targetName)
        {
            if (!byModelAndTarget.TryGetValue(modelName, out var targets))
            {
                targets = new Dictionary<string, EndpointAggregate>(StringComparer.OrdinalIgnoreCase);
                byModelAndTarget[modelName] = targets;
            }

            if (!targets.TryGetValue(targetName, out var aggregate))
            {
                aggregate = new EndpointAggregate();
                targets[targetName] = aggregate;
            }

            return aggregate;
        }

        string ResolveModel(MultiRequestStatusItem item)
        {
            var directModel = ExtractHeaderValue(item.RequestHeadersText, "Model")
                ?? ExtractHeaderValue(item.ResponseHeadersText, "Model");
            if (!string.IsNullOrWhiteSpace(directModel))
            {
                return NormalizeModel(directModel);
            }

            var requestId = ExtractHeaderValue(item.RequestHeadersText, "S7P-ID")
                ?? ExtractHeaderValue(item.ResponseHeadersText, "S7P-ID");

            if (!string.IsNullOrWhiteSpace(requestId)
                && requestModelById.TryGetValue(requestId.Trim(), out var modelFromRequestId))
            {
                return NormalizeModel(modelFromRequestId);
            }

            // EndpointKey is produced by the EventHub reader and often carries "| model=<name>".
            if (!string.IsNullOrWhiteSpace(item.EndpointKey))
            {
                var marker = "model=";
                var index = item.EndpointKey.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    var modelFromEndpointKey = item.EndpointKey[(index + marker.Length)..].Trim();
                    if (!string.IsNullOrWhiteSpace(modelFromEndpointKey))
                    {
                        return NormalizeModel(modelFromEndpointKey);
                    }
                }
            }

            return NormalizeModel(null);
        }

        string ResolveBackendLabel(MultiRequestStatusItem item, string? fallbackLabel)
        {
            var directLabel = ExtractHeaderValue(item.ResponseHeadersText, "x-backend-label")
                ?? ExtractHeaderValue(item.RequestHeadersText, "x-backend-label");
            if (!string.IsNullOrWhiteSpace(directLabel))
            {
                return directLabel.Trim();
            }

            var requestId = ExtractHeaderValue(item.RequestHeadersText, "S7P-ID")
                ?? ExtractHeaderValue(item.ResponseHeadersText, "S7P-ID");
            if (!string.IsNullOrWhiteSpace(requestId)
                && requestLabelById.TryGetValue(requestId.Trim(), out var mappedLabel)
                && !string.IsNullOrWhiteSpace(mappedLabel))
            {
                return mappedLabel.Trim();
            }

            if (!string.IsNullOrWhiteSpace(fallbackLabel))
            {
                return fallbackLabel.Trim();
            }

            return "(unknown label)";
        }

        void AggregateBackendLog(MultiRequestStatusItem item, string backendLog)
        {
            var targets = ExtractAllBackendTargets(backendLog);
            if (targets.Count == 0)
            {
                return;
            }

            var model = ResolveModel(item);

            var priorityFromLog = Regex.Match(
                backendLog,
                @"(?:^|\|)\s*Priority\s*:\s*([^|]+)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var priority = NormalizePriority(
                ExtractHeaderValue(item.RequestHeadersText, "Priority")
                ?? ExtractHeaderValue(item.ResponseHeadersText, "Priority")
                ?? (priorityFromLog.Success ? priorityFromLog.Groups[1].Value : null));

            // For metrics shared across all targets in this attempt (timings, overall call count)
            var policyCycleCounter = ParseDoubleHeaderValue(item.RequestHeadersText, "x-PolicyCycleCounter");
            var requestProcessDuration = ParseDoubleHeaderValue(item.RequestHeadersText, "Request-Process-Duration");
            var queueDuration = TryReadBackendLogDuration(backendLog, "Proxy-Queue-Duration");
            var processDuration = TryReadBackendLogDuration(backendLog, "Proxy-Process-Duration");
            var hasThrottled = backendLog.Contains("THROTTLED", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(backendLog, @"Throttling\s+\w+\s+by", RegexOptions.IgnoreCase);
            var hasCallIncomplete = backendLog.Contains("CALL INCOMPLETE", StringComparison.OrdinalIgnoreCase);
            var hasExhaustedRetries = backendLog.Contains("RETRIES LEFT: exhausted", StringComparison.OrdinalIgnoreCase);

            // Process the final target (last "Using X URL") as the one that determined the final status
            var finalTarget = targets[targets.Count - 1];
            var finalTargetLabel = ResolveBackendLabel(item, finalTarget.Name);
            var finalAggregate = GetOrCreateAggregate(model, finalTargetLabel);

            finalAggregate.TargetName = finalTargetLabel;
            finalAggregate.TargetUrl = finalTarget.Url;
            finalAggregate.Calls++;
            finalAggregate.EndpointCount = 1;
            if (item.StatusCode is >= 400)
            {
                finalAggregate.Failures++;
            }
            finalAggregate.PriorityCounts[priority] = finalAggregate.PriorityCounts.TryGetValue(priority, out var finalPriorityCount)
                ? finalPriorityCount + 1
                : 1;

            if (!string.IsNullOrWhiteSpace(item.BackendHost))
            {
                finalAggregate.LastBackendHost = item.BackendHost;
            }

            if (policyCycleCounter.HasValue)
            {
                finalAggregate.EndpointAttemptsTotal += policyCycleCounter.Value;
                finalAggregate.EndpointAttemptsSamples++;
            }

            if (requestProcessDuration.HasValue)
            {
                finalAggregate.RequestProcessDurationTotal += requestProcessDuration.Value;
                finalAggregate.RequestProcessDurationSamples++;
            }

            if (queueDuration.HasValue)
            {
                finalAggregate.QueueDurationTotalMs += queueDuration.Value;
                finalAggregate.QueueDurationSamples++;
            }

            if (processDuration.HasValue)
            {
                finalAggregate.ProcessDurationTotalMs += processDuration.Value;
                finalAggregate.ProcessDurationSamples++;
            }

            if (hasThrottled)
            {
                finalAggregate.ThrottledEvents++;
            }

            if (IsTargetThrottled(backendLog, finalTarget.Name))
            {
                finalAggregate.TargetThrottledEvents++;
            }

            if (hasCallIncomplete)
            {
                finalAggregate.CallIncompleteEvents++;
            }

            if (hasExhaustedRetries)
            {
                finalAggregate.ExhaustedRetryEvents++;
            }

            // Track all intermediate targets (including the final one) that were attempted
            // but mark earlier ones as "attempted" without the final status code
            foreach (var target in targets)
            {
                if (string.Equals(target.Name, finalTarget.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // Already processed above as the final target
                }

                var retryAggregate = GetOrCreateAggregate(model, target.Name);

                retryAggregate.TargetName = target.Name;
                retryAggregate.TargetUrl = target.Url;
                retryAggregate.Calls++; // Count as an attempt
                retryAggregate.EndpointCount = 1;
                retryAggregate.PriorityCounts[priority] = retryAggregate.PriorityCounts.TryGetValue(priority, out var retryPriorityCount)
                    ? retryPriorityCount + 1
                    : 1;
                
                // Don't apply final status code to retry attempts, but track if this target was throttled
                if (IsTargetThrottled(backendLog, target.Name))
                {
                    retryAggregate.Failures++;
                    retryAggregate.TargetThrottledEvents++;
                    retryAggregate.ThrottledEvents++;
                }
            }
        }

        void AggregateFallbackTarget(MultiRequestStatusItem item)
        {
            var model = ResolveModel(item);
            var priority = NormalizePriority(
                ExtractHeaderValue(item.RequestHeadersText, "Priority")
                ?? ExtractHeaderValue(item.ResponseHeadersText, "Priority"));

            var requestId = ExtractHeaderValue(item.RequestHeadersText, "S7P-ID")
                ?? ExtractHeaderValue(item.ResponseHeadersText, "S7P-ID");

            if (string.Equals(item.EventType, "S7P-ProxyRequest", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(requestId)
                && backendFallbackRequestIds.Contains(requestId))
            {
                return;
            }

            var targetLabel = ExtractHeaderValue(item.ResponseHeadersText, "x-backend-label")
                ?? ExtractHeaderValue(item.RequestHeadersText, "x-backend-label")
                ?? (!string.IsNullOrWhiteSpace(requestId)
                    && requestLabelById.TryGetValue(requestId.Trim(), out var mappedLabel)
                        ? mappedLabel
                        : null);

            if (string.IsNullOrWhiteSpace(targetLabel))
            {
                return;
            }

            var targetUrl = ExtractHeaderValue(item.ResponseHeadersText, "Backend-Host")
                ?? ExtractHeaderValue(item.RequestHeadersText, "Backend-Host")
                ?? ExtractHeaderValue(item.RequestHeadersText, "Host-URL")
                ?? "-";

            var urlFromEvent = ExtractHeaderValue(item.ResponseHeadersText, "Url")
                ?? ExtractHeaderValue(item.RequestHeadersText, "Url");
            if (!string.IsNullOrWhiteSpace(urlFromEvent))
            {
                targetUrl = urlFromEvent;
            }
            else
            {
                var requestPath = ExtractPathFromHeaders(item.RequestHeadersText);
                if (!string.IsNullOrWhiteSpace(requestPath)
                    && !string.Equals(requestPath, "/", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(targetUrl)
                    && !string.Equals(targetUrl, "-", StringComparison.Ordinal))
                {
                    if (Uri.TryCreate(targetUrl, UriKind.Absolute, out var baseUri))
                    {
                        targetUrl = $"{baseUri.Scheme}://{baseUri.Host}{requestPath}";
                    }
                    else
                    {
                        targetUrl = $"{targetUrl.TrimEnd('/')}/{requestPath.TrimStart('/')}";
                    }
                }
            }

            var aggregate = GetOrCreateAggregate(model, targetLabel);

            aggregate.TargetName = targetLabel;
            aggregate.TargetUrl = targetUrl;
            aggregate.Calls++;
            aggregate.EndpointCount = 1;
            if (item.StatusCode is >= 400)
            {
                aggregate.Failures++;
            }
            aggregate.PriorityCounts[priority] = aggregate.PriorityCounts.TryGetValue(priority, out var priorityCount)
                ? priorityCount + 1
                : 1;

            if (!string.IsNullOrWhiteSpace(item.BackendHost))
            {
                aggregate.LastBackendHost = item.BackendHost;
            }

            if (string.Equals(item.EventType, "S7P-BackendRequest", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(requestId))
            {
                backendFallbackRequestIds.Add(requestId);
            }
        }

        // Process backend-request items first so their richer per-attempt headers
        // (x-PolicyCycleCounter, Request-Process-Duration) win over the proxy summary.
        var orderedItems = filteredRequests
            .Where(item => string.Equals(item.EventType, "S7P-BackendRequest", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.EventType, "S7P-ProxyRequest", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => string.Equals(item.EventType, "S7P-BackendRequest", StringComparison.OrdinalIgnoreCase));

        foreach (var item in orderedItems)
        {
            if (string.Equals(item.EventType, "S7P-BackendRequest", StringComparison.OrdinalIgnoreCase))
            {
                var usedBackendLog = false;
                var backendLog = ExtractHeaderValue(item.ResponseHeadersText, "backendLog");
                if (!string.IsNullOrWhiteSpace(backendLog))
                {
                    usedBackendLog = true;
                    if (seenLogs.Add(backendLog))
                    {
                        AggregateBackendLog(item, backendLog);
                    }
                }

                if (!usedBackendLog)
                {
                    AggregateFallbackTarget(item);
                }

                continue;
            }

            // S7P-ProxyRequest: pull every Attempt-N-backendLog from the raw event dump.
            var usedAttemptLogs = false;
            for (var attempt = 1; ; attempt++)
            {
                var backendLog = ExtractHeaderValue(item.ResponseHeadersText, $"Attempt-{attempt}-backendLog");
                if (string.IsNullOrWhiteSpace(backendLog))
                {
                    break;
                }

                usedAttemptLogs = true;
                if (seenLogs.Add(backendLog))
                {
                    AggregateBackendLog(item, backendLog);
                }
            }

            if (!usedAttemptLogs)
            {
                AggregateFallbackTarget(item);
            }
        }

        return byModelAndTarget
            .Select(modelEntry =>
            {
                var modelName = modelEntry.Key;
                var endpointAggregates = modelEntry.Value.Values
                    .OrderByDescending(aggregate => aggregate.Calls)
                    .ThenBy(aggregate => aggregate.TargetName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var calls = endpointAggregates.Sum(aggregate => aggregate.Calls);
                var failures = endpointAggregates.Sum(aggregate => aggregate.Failures);
                var endpointAttemptsTotal = endpointAggregates.Sum(aggregate => aggregate.EndpointAttemptsTotal);
                var endpointAttemptsSamples = endpointAggregates.Sum(aggregate => aggregate.EndpointAttemptsSamples);
                var requestProcessDurationTotal = endpointAggregates.Sum(aggregate => aggregate.RequestProcessDurationTotal);
                var requestProcessDurationSamples = endpointAggregates.Sum(aggregate => aggregate.RequestProcessDurationSamples);
                var queueDurationTotalMs = endpointAggregates.Sum(aggregate => aggregate.QueueDurationTotalMs);
                var queueDurationSamples = endpointAggregates.Sum(aggregate => aggregate.QueueDurationSamples);
                var processDurationTotalMs = endpointAggregates.Sum(aggregate => aggregate.ProcessDurationTotalMs);
                var processDurationSamples = endpointAggregates.Sum(aggregate => aggregate.ProcessDurationSamples);
                var throttledEvents = endpointAggregates.Sum(aggregate => aggregate.ThrottledEvents);
                var targetThrottledEvents = endpointAggregates.Sum(aggregate => aggregate.TargetThrottledEvents);
                var callIncompleteEvents = endpointAggregates.Sum(aggregate => aggregate.CallIncompleteEvents);
                var exhaustedRetryEvents = endpointAggregates.Sum(aggregate => aggregate.ExhaustedRetryEvents);

                var priorityCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var aggregate in endpointAggregates)
                {
                    foreach (var priorityPair in aggregate.PriorityCounts)
                    {
                        priorityCounts[priorityPair.Key] = priorityCounts.TryGetValue(priorityPair.Key, out var existing)
                            ? existing + priorityPair.Value
                            : priorityPair.Value;
                    }
                }

                var failureRate = calls == 0
                    ? 0
                    : (failures * 100.0) / calls;
                var css = failureRate >= 50 ? "down" : failureRate > 0 ? "degraded" : "healthy";

                var endpointUsages = endpointAggregates
                    .Select(aggregate =>
                    {
                        return new EndpointUsageTile(
                            aggregate.TargetName,
                            string.IsNullOrWhiteSpace(aggregate.TargetUrl) ? "-" : aggregate.TargetUrl,
                            aggregate.Calls,
                            aggregate.Failures,
                            aggregate.ThrottledEvents,
                            GetPriorityCount(aggregate.PriorityCounts, "1"),
                            GetPriorityCount(aggregate.PriorityCounts, "2"),
                            GetPriorityCount(aggregate.PriorityCounts, "3"));
                    })
                    .ToArray();

                return new EndpointTile(
                    modelName,
                    calls,
                    failures,
                    endpointAttemptsSamples == 0 ? 0 : endpointAttemptsTotal / endpointAttemptsSamples,
                    requestProcessDurationSamples == 0 ? 0 : requestProcessDurationTotal / requestProcessDurationSamples,
                    queueDurationSamples == 0 ? 0 : queueDurationTotalMs / queueDurationSamples,
                    processDurationSamples == 0 ? 0 : processDurationTotalMs / processDurationSamples,
                    throttledEvents,
                    targetThrottledEvents,
                    callIncompleteEvents,
                    exhaustedRetryEvents,
                    endpointAggregates.Length,
                    GetPriorityCount(priorityCounts, "1"),
                    GetPriorityCount(priorityCounts, "2"),
                    GetPriorityCount(priorityCounts, "3"),
                    BuildPrioritySummary(priorityCounts),
                    endpointUsages,
                    "-",
                    $"{endpointAggregates.Length} endpoint(s)",
                    "-",
                        failures == 0 ? "Healthy" : $"{failureRate:0.#}% attempts failed",
                    css);
            })
            .OrderByDescending(tile => tile.Calls)
            .ThenBy(tile => tile.Path, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();
    }

    private IReadOnlyList<PathCount> BuildPathCounts(bool success)
    {
        var filteredRequests = GetFilteredRequestItems();
        if (!IsConnected || !_snapshot.HasData || filteredRequests.Count == 0)
        {
            return Array.Empty<PathCount>();
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in filteredRequests)
        {
            if (!string.Equals(item.EventType, "S7P-ProxyRequest", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(item.EventType, "S7P-ProxyRequestExpired", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isSuccess = item.StatusCode is >= 200 and < 400;
            if (isSuccess != success)
            {
                continue;
            }

            var path = ExtractPathFromHeaders(item.RequestHeadersText);
            if (string.IsNullOrWhiteSpace(path))
            {
                path = "/";
            }

            counts[path] = counts.TryGetValue(path, out var count)
                ? count + 1
                : 1;
        }

        return counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .Select(pair => new PathCount(pair.Key, pair.Value))
            .ToArray();
    }

    private IReadOnlyList<UserCount> BuildUserCounts(bool success)
    {
        var filteredRequests = GetFilteredRequestItems();
        if (!IsConnected || !_snapshot.HasData || filteredRequests.Count == 0)
        {
            return Array.Empty<UserCount>();
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in filteredRequests)
        {
            if (!string.Equals(item.EventType, "S7P-ProxyRequest", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(item.EventType, "S7P-ProxyRequestExpired", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isSuccess = item.StatusCode is >= 200 and < 400;
            if (isSuccess != success)
            {
                continue;
            }

            var user = ExtractHeaderValue(item.RequestHeadersText, "UserID");
            if (string.IsNullOrWhiteSpace(user))
            {
                user = "(unknown)";
            }

            counts[user] = counts.TryGetValue(user, out var count)
                ? count + 1
                : 1;
        }

        return counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .Select(pair => new UserCount(pair.Key, pair.Value))
            .ToArray();
    }

    private static string ExtractPathFromHeaders(string? requestHeaders)
    {
        if (string.IsNullOrWhiteSpace(requestHeaders))
        {
            return "/";
        }

        // Phase-formatted text (enqueue / final sections) exposes the path as a "Path: ..." field.
        var pathField = ExtractHeaderValue(requestHeaders, "Path");
        if (!string.IsNullOrWhiteSpace(pathField))
        {
            return pathField;
        }

        // Legacy request-line format: "METHOD /path".
        var firstLine = requestHeaders
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return "/";
        }

        var firstSpace = firstLine.IndexOf(' ');
        if (firstSpace < 0 || firstSpace == firstLine.Length - 1)
        {
            return "/";
        }

        return firstLine[(firstSpace + 1)..].Trim();
    }

    public static string? ExtractHeaderValue(string? headers, string headerName)
    {
        if (string.IsNullOrWhiteSpace(headers) || string.IsNullOrWhiteSpace(headerName))
        {
            return null;
        }

        var lines = headers
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var prefix = $"{headerName}:";
        foreach (var line in lines)
        {
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return line[prefix.Length..].Trim();
        }

        return null;
    }

    private static Dictionary<string, string> BuildRequestModelMap(IReadOnlyList<MultiRequestStatusItem> items)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var requestId = ExtractHeaderValue(item.RequestHeadersText, "S7P-ID")
                ?? ExtractHeaderValue(item.ResponseHeadersText, "S7P-ID");
            if (string.IsNullOrWhiteSpace(requestId))
            {
                continue;
            }

            var model = ExtractHeaderValue(item.RequestHeadersText, "Model")
                ?? ExtractHeaderValue(item.ResponseHeadersText, "Model");
            if (string.IsNullOrWhiteSpace(model))
            {
                continue;
            }

            map[requestId.Trim()] = model.Trim();
        }

        return map;
    }

    private static Dictionary<string, string> BuildRequestLabelMap(IReadOnlyList<MultiRequestStatusItem> items)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var requestId = ExtractHeaderValue(item.RequestHeadersText, "S7P-ID")
                ?? ExtractHeaderValue(item.ResponseHeadersText, "S7P-ID");
            if (string.IsNullOrWhiteSpace(requestId))
            {
                continue;
            }

            var label = ExtractHeaderValue(item.ResponseHeadersText, "x-backend-label")
                ?? ExtractHeaderValue(item.RequestHeadersText, "x-backend-label");
            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            map[requestId.Trim()] = label.Trim();
        }

        return map;
    }

    private static double? TryReadBackendLogDuration(string backendLog, string metricName)
    {
        var match = Regex.Match(
            backendLog,
            $"{Regex.Escape(metricName)}\\s*:\\s*([0-9]+(?:\\.[0-9]+)?)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!match.Success)
        {
            return null;
        }

        return double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
            ? duration
            : null;
    }

    private static double? ParseDoubleHeaderValue(string? headers, string headerName)
    {
        var value = ExtractHeaderValue(headers, headerName);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool IsTargetThrottled(string backendLog, string targetName)
    {
        // Check for old format: "THROTTLED: {targetName}"
        if (backendLog.Contains($"THROTTLED: {targetName}", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check for new format: "Throttling {targetName} by XXs"
        if (Regex.IsMatch(backendLog, $@"Throttling\s+{Regex.Escape(targetName)}\s+by", RegexOptions.IgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static IReadOnlyList<(string Name, string Url)> ExtractAllBackendTargets(string backendLog)
    {
        var targets = new List<(string, string)>();
        var matches = Regex.Matches(
            backendLog,
            @"Using\s+([A-Za-z0-9_-]+)\s+URL:\s*(https?://[^\s|]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        foreach (Match match in matches)
        {
            if (match.Success)
            {
                var name = match.Groups[1].Value;
                var url = match.Groups[2].Value;
                targets.Add((name, url));
            }
        }

        return targets;
    }

    private static bool TryExtractBackendTarget(string backendLog, out string targetName, out string targetUrl)
    {
        var targets = ExtractAllBackendTargets(backendLog);
        if (targets.Count == 0)
        {
            targetName = string.Empty;
            targetUrl = string.Empty;
            return false;
        }

        // Return the final (last) target for backward compatibility
        var finalTarget = targets[targets.Count - 1];
        targetName = finalTarget.Name;
        targetUrl = finalTarget.Url;
        return true;
    }

    private sealed class EndpointAggregate
    {
        public int Calls { get; set; }
        public int Failures { get; set; }
        public double EndpointAttemptsTotal { get; set; }
        public int EndpointAttemptsSamples { get; set; }
        public double RequestProcessDurationTotal { get; set; }
        public int RequestProcessDurationSamples { get; set; }
        public double QueueDurationTotalMs { get; set; }
        public int QueueDurationSamples { get; set; }
        public double ProcessDurationTotalMs { get; set; }
        public int ProcessDurationSamples { get; set; }
        public int ThrottledEvents { get; set; }
        public int TargetThrottledEvents { get; set; }
        public int CallIncompleteEvents { get; set; }
        public int ExhaustedRetryEvents { get; set; }
        public string LastBackendHost { get; set; } = string.Empty;
        public string TargetName { get; set; } = string.Empty;
        public string TargetUrl { get; set; } = string.Empty;
        public int EndpointCount { get; set; }
        public Dictionary<string, int> PriorityCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed record EndpointTile(
        string Path,
        int Calls,
        int Failures,
        double AverageEndpointAttempts,
        double AverageRequestProcessDuration,
        double AverageQueueDurationMs,
        double AverageProcessDurationMs,
        int ThrottledEvents,
        int TargetThrottledEvents,
        int CallIncompleteEvents,
        int ExhaustedRetryEvents,
        int EndpointCount,
        int Priority1Count,
        int Priority2Count,
        int Priority3Count,
        string PrioritySummary,
        IReadOnlyList<EndpointUsageTile> EndpointUsages,
        string LastBackendHost,
        string TargetLabel,
        string TargetUrl,
        string Status,
        string Css);

    public sealed record EndpointUsageTile(
        string TargetName,
        string TargetUrl,
        int Calls,
        int Failures,
        int ThrottledEvents,
        int Priority1Count,
        int Priority2Count,
        int Priority3Count);

    private IReadOnlyList<MultiRequestStatusItem> GetFilteredRequestItems() => AnalyticsRequests;

    private bool MatchesSelectedFilters(MultiRequestStatusItem item)
    {
        return MatchesSelectedEnvironment(item)
            && MatchesSelectedUser(item)
            && MatchesSelectedModel(item);
    }

    private bool MatchesSelectedEnvironment(MultiRequestStatusItem item)
    {
        if (string.IsNullOrWhiteSpace(SelectedEnvironmentFilter)
            || string.Equals(SelectedEnvironmentFilter, "All envs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var scope = _scopeOptions.FirstOrDefault(option =>
            string.Equals(option.Label, SelectedEnvironmentFilter, StringComparison.OrdinalIgnoreCase));
        if (scope is not null)
        {
            if (scope.IsAll)
            {
                return true;
            }

            if (scope.IsReplica)
            {
                return string.Equals(item.ContainerApp, scope.ContainerApp, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.Replica, scope.Replica, StringComparison.OrdinalIgnoreCase);
            }

            if (!string.IsNullOrWhiteSpace(scope.ContainerApp))
            {
                return string.Equals(item.ContainerApp, scope.ContainerApp, StringComparison.OrdinalIgnoreCase);
            }
        }

        return string.Equals(item.ContainerApp, SelectedEnvironmentFilter, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesSelectedUser(MultiRequestStatusItem item)
    {
        if (string.IsNullOrWhiteSpace(SelectedUserFilter)
            || string.Equals(SelectedUserFilter, "All users", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var user = string.IsNullOrWhiteSpace(item.UserId)
            ? ExtractHeaderValue(item.RequestHeadersText, "UserID")
            : item.UserId;

        return !string.IsNullOrWhiteSpace(user)
            && string.Equals(user.Trim(), SelectedUserFilter.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesSelectedModel(MultiRequestStatusItem item)
    {
        if (string.IsNullOrWhiteSpace(SelectedModelFilter)
            || string.Equals(SelectedModelFilter, "All models", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var model = ResolveRequestModelForFilter(item);
        return !string.IsNullOrWhiteSpace(model)
            && string.Equals(model.Trim(), SelectedModelFilter.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyList<MultiRequestStatusItem> FilterHistoryItemsByScope()
    {
        if (string.Equals(EffectiveScopeId, "all", StringComparison.OrdinalIgnoreCase))
        {
            return _snapshot.Requests;
        }

        if (EffectiveScopeId.StartsWith("app:", StringComparison.OrdinalIgnoreCase))
        {
            var app = EffectiveScopeId["app:".Length..];
            return _snapshot.Requests
                .Where(item => string.Equals(item.ContainerApp, app, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        if (EffectiveScopeId.StartsWith("replica:", StringComparison.OrdinalIgnoreCase))
        {
            var scopePayload = EffectiveScopeId["replica:".Length..];
            var separatorIndex = scopePayload.IndexOf(':');
            if (separatorIndex <= 0)
            {
                return _snapshot.Requests;
            }

            var app = scopePayload[..separatorIndex];
            var replica = scopePayload[(separatorIndex + 1)..];
            return _snapshot.Requests
                .Where(item => string.Equals(item.ContainerApp, app, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.Replica, replica, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        return _snapshot.Requests;
    }

    private IReadOnlyList<string> BuildEnvironmentFilterOptions()
    {
        var options = new List<string> { "All envs" };

        foreach (var scope in _scopeOptions)
        {
            if (scope.IsAll)
            {
                continue;
            }

            options.Add(scope.Label);
        }

        return options;
    }

    private IReadOnlyList<string> BuildUserFilterOptions()
    {
        var users = _snapshot.Requests
            .Select(item => string.IsNullOrWhiteSpace(item.UserId) ? ExtractHeaderValue(item.RequestHeadersText, "UserID") : item.UserId)
            .Where(user => !string.IsNullOrWhiteSpace(user))
            .Select(user => user!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(user => user, StringComparer.OrdinalIgnoreCase)
            .ToList();

        users.Insert(0, "All users");
        return users;
    }

    private IReadOnlyList<string> BuildModelFilterOptions()
    {
        var models = _snapshot.Requests
            .Select(ResolveRequestModelForFilter)
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .ToList();

        models.Insert(0, "All models");
        return models;
    }

    private static string? ResolveRequestModelForFilter(MultiRequestStatusItem item)
    {
        return FirstPhaseValue(item.Phases, "model")
            ?? FirstPhaseValue(item.Phases, "Model")
            ?? FirstPhaseValue(item.Phases, "ModelName")
            ?? FirstPhaseValue(item.Phases, "ModelDisplayName")
            ?? FirstPhaseValue(item.Phases, "ModelId")
            ?? FirstPhaseValue(item.Phases, "ModelKey")
            ?? FirstPhaseValue(item.Phases, "x-model")
            ?? FirstPhaseValue(item.Phases, "x-model-name")
            ?? FirstPhaseValue(item.Phases, "x-model-id")
            ?? FirstPhaseValue(item.Phases, "Deployment")
            ?? FirstPhaseValue(item.Phases, "DeploymentName")
            ?? FirstPhaseValue(item.Phases, "ModelDeployment")
            ?? FirstPhaseValue(item.Phases, "AOAIModel")
            ?? ExtractHeaderValue(item.RequestHeadersText, "Model")
            ?? ExtractHeaderValue(item.ResponseHeadersText, "Model");
    }

    private static string? FirstPhaseValue(RequestPhaseView? phases, string key)
    {
        if (phases is null)
        {
            return null;
        }

        if (TryGetPhaseValue(phases.Enqueue, key, out var enqueueValue))
        {
            return enqueueValue;
        }

        foreach (var attempt in phases.Attempts)
        {
            if (TryGetPhaseValue(attempt, key, out var attemptValue))
            {
                return attemptValue;
            }
        }

        if (TryGetPhaseValue(phases.Final, key, out var finalValue))
        {
            return finalValue;
        }

        return null;
    }

    private static bool TryGetPhaseValue(IReadOnlyList<KeyValuePair<string, string>>? fields, string key, out string value)
    {
        if (fields is not null)
        {
            foreach (var field in fields)
            {
                if (string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(field.Value))
                {
                    value = field.Value;
                    return true;
                }
            }
        }

        value = string.Empty;
        return false;
    }

}
