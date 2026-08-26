namespace SimpleL7Proxy.Backend.Iterators;

using SimpleL7Proxy.Proxy;

/// <summary>
/// Base for anything that can hand out backend hosts to a request: a
/// <see cref="PerRequestHostIterator"/> or the path-wide, concurrently-shared
/// <see cref="SharedHostIterator"/>. Implements the "how many attempts, skip circuit-broken
/// hosts" algorithm once; subclasses only supply how to fetch one raw candidate and their
/// host list. Per-request state lives in the caller-owned <see cref="IterationState"/>, never
/// on the iterator instance, since a shared iterator is reused by many concurrent requests.
/// </summary>
public abstract class BaseIterator : IHostIterator
{
    /// <summary>Gets the total number of hosts this iterator can select from.</summary>
    public abstract int HostCount { get; }

    /// <summary>Gets the full host list, used only for circuit-breaker "all open" scanning.</summary>
    protected abstract IReadOnlyList<BaseHostHealth> Hosts { get; }

    /// <summary>
    /// SinglePass normally stops once a lap is exhausted on its own; a shared iterator is
    /// circular and never stops by itself, so it caps itself by host count instead.
    /// </summary>
    protected virtual int SinglePassCap => int.MaxValue;

    /// <summary>
    /// Fetches one raw candidate honoring this iterator's own pass semantics (lap reset for
    /// ordering iterators; atomic circular fetch for the shared iterator). No circuit-breaker
    /// awareness — that's layered on by <see cref="TryGet"/>.
    /// </summary>
    protected abstract bool FetchCandidate(IterationState state, out BaseHostHealth? host);

    /// <summary>
    /// Records an actual backend attempt (not a circuit-breaker skip), counting it against
    /// this request's MaxAttempts budget.
    /// </summary>
    public void RecordResult(IterationState state, BaseHostHealth host, bool success)
    {
        state.TotalAttempts++;
    }

    /// <summary>
    /// Effective attempt cap for this request: MultiPass is bounded by MaxAttempts (0 = unlimited);
    /// SinglePass stops naturally, capped by <see cref="SinglePassCap"/> for circular iterators.
    /// </summary>
    protected int EffectiveMaxAttempts(IterationState state) =>
        state.Mode == IterationModeEnum.MultiPass
            ? (state.MaxAttempts > 0 ? state.MaxAttempts : int.MaxValue)
            : SinglePassCap;

    /// <summary>
    /// Gets the next host to try, skipping any that are currently circuit-broken.
    /// If every matching host is circuit-broken, throws <see cref="S7PRequeueException"/>
    /// (MultiPass — requeue and retry later) or returns false (SinglePass — caller treats
    /// this as exhaustion and returns the terminal result).
    /// </summary>
    public bool TryGet(IterationState state, out BaseHostHealth? host)
    {
        if (state.TotalAttempts >= EffectiveMaxAttempts(state))
        {
            host = null;
            return false;
        }

        while (FetchCandidate(state, out host))
        {
            var timeToRetry = host!.Config.GetMsToNextRetry();
            if (timeToRetry <= 0)
            {
                return true;
            }

            var (allOpen, retryAfterMs, checkedHostCount) = EvalHostAvailability(state, host, timeToRetry);
            if (!allOpen)
            {
                continue; // this host is blocked but another is available — skip it, try the next
            }

            state.Logger?.LogDebug(
                "All {HostCount} matching backend circuit breakers are open; requeueing after {RetryAfterMs}ms",
                checkedHostCount, retryAfterMs);
            throw new S7PRequeueException(
                "All matching backend circuit breakers are open",
                new ProxyData(),
                retryAfterMs);
        }

        return false;
    }

    /// <summary>
    /// Checks whether every host but the current one is circuit-broken. Caches the last-known
    /// available peer per request so repeated calls don't rescan the full host list.
    /// </summary>
    internal (bool AllOpen, int RetryAfterMs, int CheckedHostCount) EvalHostAvailability(
        IterationState state, BaseHostHealth currentHost, int currentRetryAfterMs)
    {
        var hostCount = HostCount;
        if (hostCount == 1)
        {
            return (true, currentRetryAfterMs, hostCount);
        }

        if (state.AvailableCircuitBreakerHost is not null
            && !ReferenceEquals(state.AvailableCircuitBreakerHost, currentHost)
            && state.AvailableCircuitBreakerHost.Config.GetMsToNextRetry() <= 0)
        {
            return (false, currentRetryAfterMs, hostCount);
        }

        state.AvailableCircuitBreakerHost = null;
        state.CircuitBreakerHosts ??= Hosts;

        var hosts = state.CircuitBreakerHosts;
        hostCount = hosts.Count;
        var allOpen = hostCount > 0;
        var retryAfterMs = currentRetryAfterMs;

        for (int hostIndex = 0; hostIndex < hostCount; hostIndex++)
        {
            var candidateHost = hosts[hostIndex];
            if (ReferenceEquals(candidateHost, currentHost)) continue;

            var candidateRetryAfterMs = candidateHost.Config.GetMsToNextRetry();
            if (candidateRetryAfterMs <= 0)
            {
                state.AvailableCircuitBreakerHost = candidateHost;
                allOpen = false;
                break;
            }

            retryAfterMs = Math.Min(retryAfterMs, candidateRetryAfterMs);
        }

        return (allOpen, retryAfterMs, hostCount);
    }
}
