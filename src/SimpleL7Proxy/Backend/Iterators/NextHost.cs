namespace SimpleL7Proxy.Backend.Iterators;

using Microsoft.Extensions.Logging;
using SimpleL7Proxy.Proxy;

/// <summary>
/// Walks a host iterator on behalf of a single request, owning the "how many laps"
/// decision (SinglePass vs MultiPass, MaxAttempts) that the iterator itself no longer
/// tracks — the iterator only knows how to order hosts within one lap. Also skips
/// circuit-broken hosts and detects/handles the "all matching hosts are open" case.
/// </summary>
internal sealed class NextHost
{
    private readonly IHostIterator? _hostIterator;
    private readonly ISharedHostIterator? _sharedIterator;
    private readonly IEndpointMonitorService _backends;
    private readonly string _loadBalanceMode;
    private readonly string _requestPath;
    private readonly IterationModeEnum _mode;
    private readonly int _maxAttempts;
    private readonly ILogger? _logger;
    private int _totalAttempts;
    private IReadOnlyList<BaseHostHealth>? _circuitBreakerHosts;
    private BaseHostHealth? _availableCircuitBreakerHost;

    internal NextHost(
        IHostIterator? hostIterator,
        ISharedHostIterator? sharedIterator,
        IEndpointMonitorService backends,
        string loadBalanceMode,
        string requestPath,
        IterationModeEnum mode,
        int maxAttempts,
        ILogger? logger = null,
        int priorAttempts = 0)
    {
        _hostIterator = hostIterator;
        _sharedIterator = sharedIterator;
        _backends = backends;
        _loadBalanceMode = loadBalanceMode;
        _requestPath = requestPath;
        _mode = mode;
        _maxAttempts = maxAttempts;
        _logger = logger;
        // Seeded from the request's lifetime attempt count so MaxAttempts is a true
        // ceiling across requeue cycles, not just within the current one (which the
        // requeue worker resets BackendAttempts on).
        _totalAttempts = priorAttempts;
    }

    internal int HostCount => _sharedIterator?.HostCount ?? _hostIterator?.HostCount ?? 0;

    /// <summary>The raw configured MaxAttempts (route override or global), 0 = unlimited. For display/exhaustion-message use.</summary>
    internal int ConfiguredMaxAttempts => _maxAttempts;

    /// <summary>
    /// Effective attempt cap for this request: MultiPass is bounded by MaxAttempts (0 = unlimited);
    /// SinglePass stops naturally once the iterator's one lap is exhausted, except shared iterators
    /// are circular and never stop on their own, so they're capped by host count instead.
    /// </summary>
    private int EffectiveMaxAttempts()
    {
        if (_mode == IterationModeEnum.MultiPass)
            return _maxAttempts > 0 ? _maxAttempts : int.MaxValue;

        return _sharedIterator != null ? HostCount : int.MaxValue;
    }

    /// <summary>
    /// Gets the next host to try, skipping any that are currently circuit-broken.
    /// If every matching host is circuit-broken, throws <see cref="S7PRequeueException"/>
    /// (MultiPass — requeue and retry later) or returns false (SinglePass — caller treats
    /// this as exhaustion and returns the terminal result).
    /// </summary>
    internal bool TryGet(out BaseHostHealth? host)
    {
        while (TryGetNextCandidate(out host))
        {
            var timeToRetry = host!.Config.GetMsToNextRetry();
            if (timeToRetry <= 0)
            {
                return true;
            }

            var (allOpen, retryAfterMs, checkedHostCount) = EvalHostAvailability(host, timeToRetry);
            if (!allOpen)
            {
                continue; // this host is blocked but another is available — skip it, try the next
            }

            // Lifetime attempt budget already exhausted across prior requeue cycles — stop here
            // instead of scheduling yet another sleep+retry that would never respect MaxAttempts.
            if (_maxAttempts > 0 && _totalAttempts >= _maxAttempts)
            {
                host = null;
                return false;
            }

            // if (_mode != IterationModeEnum.MultiPass)
            // {
            //     _logger?.LogWarning(
            //         "All {HostCount} matching backend circuit breakers are open; SinglePass will return the terminal backend result",
            //         checkedHostCount);
            //     host = null;
            //     return false;
            // }

            _logger?.LogWarning(
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
    /// Gets the next host from the underlying iterator, honoring pass/attempt limits.
    /// No circuit-breaker awareness — that's layered on top by <see cref="TryGet"/>.
    /// </summary>
    private bool TryGetNextCandidate(out BaseHostHealth? host)
    {
        if (_totalAttempts >= EffectiveMaxAttempts())
        {
            host = null;
            return false;
        }

        if (_sharedIterator is not null)
        {
            return _sharedIterator.TryGetNextHost(out host);
        }

        if (_hostIterator is null)
        {
            host = null;
            return false;
        }

        while (true)
        {
            if (_hostIterator.MoveNext())
            {
                host = _hostIterator.Current;
                return true;
            }

            // This lap is done. SinglePass stops here; MultiPass starts a fresh lap.
            if (_mode != IterationModeEnum.MultiPass || _totalAttempts >= EffectiveMaxAttempts())
            {
                host = null;
                return false;
            }

            _hostIterator.Reset();
        }
    }

    /// <summary>
    /// Records an actual backend attempt (not a circuit-breaker skip), which is what
    /// counts against MaxAttempts / the shared-iterator host-count cap.
    /// </summary>
    internal void RecordResult(BaseHostHealth host, bool success)
    {
        _sharedIterator?.RecordResult(host, success);
        _hostIterator?.RecordResult(host, success);
        _totalAttempts++;
    }

    internal (bool AllOpen, int RetryAfterMs, int HostCount) EvalHostAvailability( BaseHostHealth currentHost, int currentRetryAfterMs)
    {
        var hostCount = HostCount;
        if (hostCount == 1)
        {
            return (true, currentRetryAfterMs, hostCount);
        }

        if (_availableCircuitBreakerHost is not null
            && !ReferenceEquals(_availableCircuitBreakerHost, currentHost)
            && _availableCircuitBreakerHost.Config.GetMsToNextRetry() <= 0)
        {
            return (false, currentRetryAfterMs, hostCount);
        }

        _availableCircuitBreakerHost = null;
        _circuitBreakerHosts ??=
            (_sharedIterator as SharedHostIterator)?.GetHostsSnapshot()
                        ?? _hostIterator?.Hosts
                        ?? IteratorFactory.GetFilteredHosts(
                            _backends,
                            _loadBalanceMode,
                            _requestPath,
                            out _);

        hostCount = _circuitBreakerHosts.Count;
        var allOpen = hostCount > 0;
        var retryAfterMs = currentRetryAfterMs;

        for (int hostIndex = 0; hostIndex < hostCount; hostIndex++)
        {
            var candidateHost = _circuitBreakerHosts[hostIndex];
            if (ReferenceEquals(candidateHost, currentHost)) continue;

            var candidateRetryAfterMs = candidateHost.Config.GetMsToNextRetry();
            if (candidateRetryAfterMs <= 0)
            {
                _availableCircuitBreakerHost = candidateHost;
                allOpen = false;
                break;
            }

            retryAfterMs = Math.Min(retryAfterMs, candidateRetryAfterMs);
        }

        return (allOpen, retryAfterMs, hostCount);
    }
}
