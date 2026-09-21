using System.Collections.Concurrent;
using System.Threading;

namespace MetricsServer;

/// <summary>
/// In-memory store of rollup data. Nothing is persisted: all state lives in process memory and is
/// lost on restart. Memory is bounded by the configured series limit and per-series bucket ring.
/// </summary>
public sealed class MetricsStore
{
    private const string UnknownName = "unknown";
    private const double DegradedFailureRatio = 0.05;
    private const double UnhealthyFailureRatio = 0.25;

    private readonly MetricsOptions _options;
    private readonly ConcurrentDictionary<MetricKey, MetricSeries> _series = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<MetricKey, MetricSeries>> _byUser =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<MetricKey, MetricSeries>> _byModel =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly long _startedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private long _recordsIngested;
    private long _recordsDropped;

    public MetricsStore(MetricsOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Merges a rollup record into the store.
    /// </summary>
    /// <returns><c>true</c> when the record was merged; <c>false</c> when it was dropped.</returns>
    public bool Ingest(RollupRecord record)
    {
        if (record is null)
        {
            Interlocked.Increment(ref _recordsDropped);
            return false;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var timestamp = record.Timestamp > 0 ? record.Timestamp : now;

        // Reject timestamps outside the retention window; they cannot be represented in the ring.
        if (timestamp > now + _options.BucketSeconds || timestamp < now - _options.RetentionSeconds)
        {
            Interlocked.Increment(ref _recordsDropped);
            return false;
        }

        var user = Normalize(record.User);
        var model = Normalize(record.Model);
        var key = new MetricKey(user, model);

        if (!_series.TryGetValue(key, out var series))
        {
            if (_series.Count >= _options.MaxSeries)
            {
                Interlocked.Increment(ref _recordsDropped);
                return false;
            }

            series = _series.GetOrAdd(key, _ => new MetricSeries(user, model, _options.BucketCount));
            IndexSeries(key, series);
        }

        series.Add(record, timestamp / _options.BucketSeconds, timestamp);
        Interlocked.Increment(ref _recordsIngested);
        return true;
    }

    /// <summary>
    /// Aggregates the matching series over the requested window.
    /// </summary>
    public StatusResponse Query(string? user, string? model, int windowSeconds)
    {
        var window = ClampWindow(windowSeconds);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var maxBucketId = now / _options.BucketSeconds;
        var minBucketId = maxBucketId - ((window / _options.BucketSeconds) - 1);

        var aggregate = default(MetricAggregate);
        var seriesCount = 0;
        long lastUpdate = 0;

        foreach (var series in Select(user, model))
        {
            seriesCount++;
            series.Accumulate(minBucketId, maxBucketId, ref aggregate);
            if (series.LastUpdate > lastUpdate)
            {
                lastUpdate = series.LastUpdate;
            }
        }

        var response = new StatusResponse
        {
            User = string.IsNullOrWhiteSpace(user) ? "*" : user.Trim(),
            Model = string.IsNullOrWhiteSpace(model) ? "*" : model.Trim(),
            WindowSeconds = window,
            WindowStart = minBucketId * _options.BucketSeconds,
            WindowEnd = (maxBucketId + 1) * _options.BucketSeconds,
            SeriesCount = seriesCount,
            Requests = aggregate.Requests,
            Successes = aggregate.Successes,
            Failures = aggregate.Failures,
            MaxLatencyMs = aggregate.LatencyMsMax,
            PromptTokens = aggregate.PromptTokens,
            CompletionTokens = aggregate.CompletionTokens,
            LastUpdate = lastUpdate
        };

        if (aggregate.Requests > 0)
        {
            response.SuccessRate = (double)aggregate.Successes / aggregate.Requests;
            response.AverageLatencyMs = (double)aggregate.LatencyMsTotal / aggregate.Requests;
        }

        response.Status = Classify(aggregate);
        return response;
    }

    /// <summary>
    /// Returns the retained buckets for the matching series, ordered from oldest to newest.
    /// </summary>
    public SeriesResponse QuerySeries(string? user, string? model, int windowSeconds)
    {
        var window = ClampWindow(windowSeconds);
        var bucketCount = window / _options.BucketSeconds;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var maxBucketId = now / _options.BucketSeconds;
        var minBucketId = maxBucketId - (bucketCount - 1);

        var buckets = new MetricAggregate[bucketCount];
        foreach (var series in Select(user, model))
        {
            series.AccumulateByBucket(minBucketId, maxBucketId, buckets);
        }

        var response = new SeriesResponse
        {
            User = string.IsNullOrWhiteSpace(user) ? "*" : user.Trim(),
            Model = string.IsNullOrWhiteSpace(model) ? "*" : model.Trim(),
            BucketSeconds = _options.BucketSeconds
        };

        for (var i = 0; i < buckets.Length; i++)
        {
            var aggregate = buckets[i];
            response.Buckets.Add(new BucketResponse
            {
                Start = (minBucketId + i) * _options.BucketSeconds,
                Requests = aggregate.Requests,
                Successes = aggregate.Successes,
                Failures = aggregate.Failures,
                AverageLatencyMs = aggregate.Requests > 0
                    ? (double)aggregate.LatencyMsTotal / aggregate.Requests
                    : 0,
                MaxLatencyMs = aggregate.LatencyMsMax,
                PromptTokens = aggregate.PromptTokens,
                CompletionTokens = aggregate.CompletionTokens
            });
        }

        return response;
    }

    /// <summary>
    /// Lists known users, optionally restricted to those reporting for a given model.
    /// </summary>
    public NamesResponse ListUsers(string? model)
    {
        var names = string.IsNullOrWhiteSpace(model)
            ? _byUser.Values.Select(entries => FirstName(entries, byUser: true))
            : Select(null, model).Select(series => series.User);

        return BuildNames(names);
    }

    /// <summary>
    /// Lists known models, optionally restricted to those reported by a given user.
    /// </summary>
    public NamesResponse ListModels(string? user)
    {
        var names = string.IsNullOrWhiteSpace(user)
            ? _byModel.Values.Select(entries => FirstName(entries, byUser: false))
            : Select(user, null).Select(series => series.Model);

        return BuildNames(names);
    }

    /// <summary>
    /// Returns server counters describing ingest volume and the current memory footprint.
    /// </summary>
    public StatsResponse Stats() => new()
    {
        UptimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _startedAt,
        SeriesCount = _series.Count,
        MaxSeries = _options.MaxSeries,
        UserCount = _byUser.Count,
        ModelCount = _byModel.Count,
        BucketSeconds = _options.BucketSeconds,
        BucketCount = _options.BucketCount,
        RetentionSeconds = _options.RetentionSeconds,
        RecordsIngested = Interlocked.Read(ref _recordsIngested),
        RecordsDropped = Interlocked.Read(ref _recordsDropped)
    };

    /// <summary>
    /// Removes series that have not been updated within the retention window, keeping the memory
    /// footprint proportional to currently active users and models.
    /// </summary>
    /// <returns>The number of removed series.</returns>
    public int Prune()
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _options.RetentionSeconds;
        var removed = 0;

        foreach (var pair in _series)
        {
            if (pair.Value.LastUpdate > cutoff)
            {
                continue;
            }

            if (!_series.TryRemove(pair))
            {
                continue;
            }

            removed++;
            RemoveFromIndex(_byUser, pair.Value.User, pair.Key);
            RemoveFromIndex(_byModel, pair.Value.Model, pair.Key);
        }

        return removed;
    }

    private IEnumerable<MetricSeries> Select(string? user, string? model)
    {
        var hasUser = !string.IsNullOrWhiteSpace(user);
        var hasModel = !string.IsNullOrWhiteSpace(model);

        if (hasUser && hasModel)
        {
            if (_series.TryGetValue(new MetricKey(user!.Trim(), model!.Trim()), out var series))
            {
                yield return series;
            }

            yield break;
        }

        if (hasUser)
        {
            if (_byUser.TryGetValue(user!.Trim(), out var byUser))
            {
                foreach (var series in byUser.Values)
                {
                    yield return series;
                }
            }

            yield break;
        }

        if (hasModel)
        {
            if (_byModel.TryGetValue(model!.Trim(), out var byModel))
            {
                foreach (var series in byModel.Values)
                {
                    yield return series;
                }
            }

            yield break;
        }

        foreach (var series in _series.Values)
        {
            yield return series;
        }
    }

    private void IndexSeries(MetricKey key, MetricSeries series)
    {
        _byUser.GetOrAdd(series.User, _ => new ConcurrentDictionary<MetricKey, MetricSeries>())[key] = series;
        _byModel.GetOrAdd(series.Model, _ => new ConcurrentDictionary<MetricKey, MetricSeries>())[key] = series;
    }

    private static void RemoveFromIndex(
        ConcurrentDictionary<string, ConcurrentDictionary<MetricKey, MetricSeries>> index,
        string name,
        MetricKey key)
    {
        if (!index.TryGetValue(name, out var entries))
        {
            return;
        }

        entries.TryRemove(key, out _);
        if (entries.IsEmpty)
        {
            // Racing writers may re-add the name; removing an empty bucket is safe because
            // IndexSeries re-creates it on demand.
            index.TryRemove(new KeyValuePair<string, ConcurrentDictionary<MetricKey, MetricSeries>>(name, entries));
        }
    }

    private static string FirstName(ConcurrentDictionary<MetricKey, MetricSeries> entries, bool byUser)
    {
        foreach (var series in entries.Values)
        {
            return byUser ? series.User : series.Model;
        }

        return string.Empty;
    }

    private static NamesResponse BuildNames(IEnumerable<string> names)
    {
        var ordered = names
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new NamesResponse { Count = ordered.Count, Names = ordered };
    }

    private int ClampWindow(int windowSeconds)
    {
        if (windowSeconds <= 0 || windowSeconds > _options.RetentionSeconds)
        {
            windowSeconds = _options.RetentionSeconds;
        }

        // Round up to whole buckets so a window always covers at least one bucket.
        var buckets = (windowSeconds + _options.BucketSeconds - 1) / _options.BucketSeconds;
        return Math.Max(1, buckets) * _options.BucketSeconds;
    }

    private static string Classify(in MetricAggregate aggregate)
    {
        if (aggregate.Requests <= 0)
        {
            return "unknown";
        }

        var failureRatio = (double)aggregate.Failures / aggregate.Requests;
        if (failureRatio <= DegradedFailureRatio)
        {
            return "healthy";
        }

        return failureRatio <= UnhealthyFailureRatio ? "degraded" : "unhealthy";
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return UnknownName;
        }

        var trimmed = value.Trim();
        return trimmed.Length > 256 ? trimmed[..256] : trimmed;
    }
}
