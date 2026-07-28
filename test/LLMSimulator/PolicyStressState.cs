using System.Collections.Concurrent;

namespace Company.Function;

public sealed class PolicyStressState
{
    public const int TokensPerMinute = 100_000;
    public const int ResponseTokens = 1_000;

    private const int RetainedMinuteCount = 60;
    private readonly ConcurrentDictionary<EndpointKey, EndpointState> _endpoints = new();

    public TokenDecision TryConsume(string runId, string endpointId, DateTime utcNow)
    {
        var key = EndpointKey.Create(runId, endpointId);
        var state = _endpoints.GetOrAdd(key, static _ => new EndpointState());
        var windowStartUtc = StartOfMinute(utcNow);

        lock (state.Gate)
        {
            RollWindow(state, windowStartUtc);
            state.WindowRequests++;
            state.TotalRequests++;

            var retryAfterMilliseconds = (int)Math.Max(
                1,
                (windowStartUtc.AddMinutes(1) - utcNow).TotalMilliseconds);
            if (state.WindowTokens + ResponseTokens > TokensPerMinute)
            {
                state.WindowThrottled++;
                state.TotalThrottled++;
                return new TokenDecision(
                    false,
                    retryAfterMilliseconds,
                    windowStartUtc,
                    state.WindowTokens,
                    TokensPerMinute,
                    ResponseTokens);
            }

            state.WindowAccepted++;
            state.WindowTokens += ResponseTokens;
            state.TotalAccepted++;
            state.TotalTokens += ResponseTokens;
            return new TokenDecision(
                true,
                0,
                windowStartUtc,
                state.WindowTokens,
                TokensPerMinute,
                ResponseTokens);
        }
    }

    public StressRunSnapshot GetSnapshot(string runId, DateTime utcNow)
    {
        var normalizedRunId = Normalize(runId);
        var windowStartUtc = StartOfMinute(utcNow);
        var endpoints = new List<StressEndpointSnapshot>();

        foreach (var entry in _endpoints)
        {
            if (!string.Equals(entry.Key.RunId, normalizedRunId, StringComparison.Ordinal))
            {
                continue;
            }

            var state = entry.Value;
            lock (state.Gate)
            {
                RollWindow(state, windowStartUtc);
                endpoints.Add(new StressEndpointSnapshot(
                    entry.Key.EndpointId,
                    TokensPerMinute,
                    ResponseTokens,
                    new StressMinuteSnapshot(
                        state.WindowStartUtc,
                        state.WindowRequests,
                        state.WindowAccepted,
                        state.WindowThrottled,
                        state.WindowTokens),
                    new StressTotalsSnapshot(
                        state.TotalRequests,
                        state.TotalAccepted,
                        state.TotalThrottled,
                        state.TotalTokens),
                    state.CompletedMinutes.ToArray()));
            }
        }

        endpoints.Sort(static (left, right) =>
            string.Compare(left.EndpointId, right.EndpointId, StringComparison.Ordinal));
        return new StressRunSnapshot(runId, utcNow, endpoints);
    }

    public int Reset(string runId)
    {
        var normalizedRunId = Normalize(runId);
        var removed = 0;
        foreach (var key in _endpoints.Keys)
        {
            if (string.Equals(key.RunId, normalizedRunId, StringComparison.Ordinal) &&
                _endpoints.TryRemove(key, out _))
            {
                removed++;
            }
        }
        return removed;
    }

    private static void RollWindow(EndpointState state, DateTime windowStartUtc)
    {
        if (state.WindowStartUtc == default)
        {
            state.WindowStartUtc = windowStartUtc;
            return;
        }

        if (state.WindowStartUtc == windowStartUtc)
        {
            return;
        }

        state.CompletedMinutes.Enqueue(new StressMinuteSnapshot(
            state.WindowStartUtc,
            state.WindowRequests,
            state.WindowAccepted,
            state.WindowThrottled,
            state.WindowTokens));
        while (state.CompletedMinutes.Count > RetainedMinuteCount)
        {
            state.CompletedMinutes.Dequeue();
        }

        state.WindowStartUtc = windowStartUtc;
        state.WindowRequests = 0;
        state.WindowAccepted = 0;
        state.WindowThrottled = 0;
        state.WindowTokens = 0;
    }

    private static DateTime StartOfMinute(DateTime utcNow)
    {
        var normalized = utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime();
        return new DateTime(
            normalized.Year,
            normalized.Month,
            normalized.Day,
            normalized.Hour,
            normalized.Minute,
            0,
            DateTimeKind.Utc);
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private readonly record struct EndpointKey(string RunId, string EndpointId)
    {
        public static EndpointKey Create(string runId, string endpointId) =>
            new(Normalize(runId), Normalize(endpointId));
    }

    private sealed class EndpointState
    {
        public object Gate { get; } = new();
        public DateTime WindowStartUtc { get; set; }
        public long WindowRequests { get; set; }
        public long WindowAccepted { get; set; }
        public long WindowThrottled { get; set; }
        public long WindowTokens { get; set; }
        public long TotalRequests { get; set; }
        public long TotalAccepted { get; set; }
        public long TotalThrottled { get; set; }
        public long TotalTokens { get; set; }
        public Queue<StressMinuteSnapshot> CompletedMinutes { get; } = new();
    }
}

public sealed record TokenDecision(
    bool Accepted,
    int RetryAfterMilliseconds,
    DateTime WindowStartUtc,
    long TokensUsed,
    int TokenLimit,
    int ResponseTokens);

public sealed record StressRunSnapshot(
    string RunId,
    DateTime GeneratedAtUtc,
    IReadOnlyList<StressEndpointSnapshot> Endpoints);

public sealed record StressEndpointSnapshot(
    string EndpointId,
    int TokenLimit,
    int ResponseTokens,
    StressMinuteSnapshot CurrentMinute,
    StressTotalsSnapshot Totals,
    IReadOnlyList<StressMinuteSnapshot> CompletedMinutes);

public sealed record StressMinuteSnapshot(
    DateTime WindowStartUtc,
    long Requests,
    long Accepted,
    long Throttled,
    long TokensReturned);

public sealed record StressTotalsSnapshot(
    long Requests,
    long Accepted,
    long Throttled,
    long TokensReturned);