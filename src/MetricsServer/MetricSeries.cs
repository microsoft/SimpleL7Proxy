using System.Threading;

namespace MetricsServer;

/// <summary>
/// Identity of a rollup series. Comparisons are case-insensitive so that callers can publish
/// user and model names with inconsistent casing.
/// </summary>
public readonly struct MetricKey : IEquatable<MetricKey>
{
    public MetricKey(string user, string model)
    {
        User = user;
        Model = model;
    }

    /// <summary>User the series belongs to.</summary>
    public string User { get; }

    /// <summary>Model the series belongs to.</summary>
    public string Model { get; }

    public bool Equals(MetricKey other) =>
        string.Equals(User, other.User, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Model, other.Model, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is MetricKey other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.OrdinalIgnoreCase.GetHashCode(User),
        StringComparer.OrdinalIgnoreCase.GetHashCode(Model));
}

/// <summary>
/// Counters for a single time bucket. Counters are updated with interlocked operations so that
/// concurrent ingest calls never block each other.
/// </summary>
internal sealed class MetricBucket
{
    internal long BucketId = -1;
    internal long Requests;
    internal long Successes;
    internal long Failures;
    internal long LatencyMsTotal;
    internal long LatencyMsMax;
    internal long PromptTokens;
    internal long CompletionTokens;

    internal void Reset(long bucketId)
    {
        Requests = 0;
        Successes = 0;
        Failures = 0;
        LatencyMsTotal = 0;
        LatencyMsMax = 0;
        PromptTokens = 0;
        CompletionTokens = 0;
        Volatile.Write(ref BucketId, bucketId);
    }
}

/// <summary>
/// Fixed-size ring of time buckets for one user/model pair. Memory per series is constant, so the
/// in-memory footprint is bounded by the configured series limit.
/// </summary>
internal sealed class MetricSeries
{
    private readonly MetricBucket[] _buckets;
    private long _lastUpdate;

    internal MetricSeries(string user, string model, int bucketCount)
    {
        User = user;
        Model = model;
        _buckets = new MetricBucket[bucketCount];
        for (var i = 0; i < bucketCount; i++)
        {
            _buckets[i] = new MetricBucket();
        }
    }

    /// <summary>User name using the casing first seen for this series.</summary>
    internal string User { get; }

    /// <summary>Model name using the casing first seen for this series.</summary>
    internal string Model { get; }

    /// <summary>Unix timestamp, in seconds, of the most recent ingest for this series.</summary>
    internal long LastUpdate => Volatile.Read(ref _lastUpdate);

    internal void Add(RollupRecord record, long bucketId, long timestamp)
    {
        var index = (int)(((bucketId % _buckets.Length) + _buckets.Length) % _buckets.Length);
        var bucket = _buckets[index];

        if (Volatile.Read(ref bucket.BucketId) != bucketId)
        {
            lock (bucket)
            {
                if (Volatile.Read(ref bucket.BucketId) != bucketId)
                {
                    bucket.Reset(bucketId);
                }
            }
        }

        var requests = record.Requests > 0
            ? record.Requests
            : record.Successes + record.Failures;

        if (requests > 0)
        {
            Interlocked.Add(ref bucket.Requests, requests);
        }

        if (record.Successes > 0)
        {
            Interlocked.Add(ref bucket.Successes, record.Successes);
        }

        if (record.Failures > 0)
        {
            Interlocked.Add(ref bucket.Failures, record.Failures);
        }

        if (record.LatencyMsTotal > 0)
        {
            Interlocked.Add(ref bucket.LatencyMsTotal, record.LatencyMsTotal);
        }

        if (record.PromptTokens > 0)
        {
            Interlocked.Add(ref bucket.PromptTokens, record.PromptTokens);
        }

        if (record.CompletionTokens > 0)
        {
            Interlocked.Add(ref bucket.CompletionTokens, record.CompletionTokens);
        }

        if (record.LatencyMsMax > 0)
        {
            UpdateMax(ref bucket.LatencyMsMax, record.LatencyMsMax);
        }

        UpdateMax(ref _lastUpdate, timestamp);
    }

    /// <summary>
    /// Folds every bucket within the requested inclusive bucket range into the accumulator.
    /// </summary>
    internal void Accumulate(long minBucketId, long maxBucketId, ref MetricAggregate aggregate)
    {
        for (var i = 0; i < _buckets.Length; i++)
        {
            var bucket = _buckets[i];
            var bucketId = Volatile.Read(ref bucket.BucketId);
            if (bucketId < minBucketId || bucketId > maxBucketId)
            {
                continue;
            }

            aggregate.Requests += Volatile.Read(ref bucket.Requests);
            aggregate.Successes += Volatile.Read(ref bucket.Successes);
            aggregate.Failures += Volatile.Read(ref bucket.Failures);
            aggregate.LatencyMsTotal += Volatile.Read(ref bucket.LatencyMsTotal);
            aggregate.PromptTokens += Volatile.Read(ref bucket.PromptTokens);
            aggregate.CompletionTokens += Volatile.Read(ref bucket.CompletionTokens);

            var max = Volatile.Read(ref bucket.LatencyMsMax);
            if (max > aggregate.LatencyMsMax)
            {
                aggregate.LatencyMsMax = max;
            }
        }
    }

    /// <summary>
    /// Folds every bucket within the requested inclusive bucket range into per-bucket accumulators,
    /// indexed by offset from <paramref name="minBucketId"/>.
    /// </summary>
    internal void AccumulateByBucket(long minBucketId, long maxBucketId, MetricAggregate[] target)
    {
        for (var i = 0; i < _buckets.Length; i++)
        {
            var bucket = _buckets[i];
            var bucketId = Volatile.Read(ref bucket.BucketId);
            if (bucketId < minBucketId || bucketId > maxBucketId)
            {
                continue;
            }

            var offset = (int)(bucketId - minBucketId);
            if (offset < 0 || offset >= target.Length)
            {
                continue;
            }

            ref var aggregate = ref target[offset];
            aggregate.Requests += Volatile.Read(ref bucket.Requests);
            aggregate.Successes += Volatile.Read(ref bucket.Successes);
            aggregate.Failures += Volatile.Read(ref bucket.Failures);
            aggregate.LatencyMsTotal += Volatile.Read(ref bucket.LatencyMsTotal);
            aggregate.PromptTokens += Volatile.Read(ref bucket.PromptTokens);
            aggregate.CompletionTokens += Volatile.Read(ref bucket.CompletionTokens);

            var max = Volatile.Read(ref bucket.LatencyMsMax);
            if (max > aggregate.LatencyMsMax)
            {
                aggregate.LatencyMsMax = max;
            }
        }
    }

    private static void UpdateMax(ref long location, long value)
    {
        var current = Volatile.Read(ref location);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref location, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}

/// <summary>
/// Mutable accumulator used while folding buckets together.
/// </summary>
public struct MetricAggregate
{
    public long Requests;
    public long Successes;
    public long Failures;
    public long LatencyMsTotal;
    public long LatencyMsMax;
    public long PromptTokens;
    public long CompletionTokens;
}
