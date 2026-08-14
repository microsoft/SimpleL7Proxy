namespace SimpleL7Proxy.Backend.Iterators;

internal sealed class NextHost
{
    private readonly IHostIterator? _hostIterator;
    private readonly ISharedHostIterator? _sharedIterator;
    private readonly IEndpointMonitorService _backends;
    private readonly string _loadBalanceMode;
    private readonly string _requestPath;
    private IReadOnlyList<BaseHostHealth>? _circuitBreakerHosts;
    private BaseHostHealth? _availableCircuitBreakerHost;

    internal NextHost(
        IHostIterator? hostIterator,
        ISharedHostIterator? sharedIterator,
        IEndpointMonitorService backends,
        string loadBalanceMode,
        string requestPath)
    {
        _hostIterator = hostIterator;
        _sharedIterator = sharedIterator;
        _backends = backends;
        _loadBalanceMode = loadBalanceMode;
        _requestPath = requestPath;
    }

    internal int HostCount => _sharedIterator?.HostCount ?? _hostIterator?.HostCount ?? 0;

    internal bool TryGet(out BaseHostHealth? host)
    {
        if (_sharedIterator is not null)
        {
            return _sharedIterator.TryGetNextHost(out host);
        }

        if (_hostIterator is not null && _hostIterator.MoveNext())
        {
            host = _hostIterator.Current;
            return true;
        }

        host = null;
        return false;
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