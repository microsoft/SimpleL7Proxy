using System.Collections.Concurrent;

namespace SimpleL7Proxy.Backend;

/// <summary>
/// Static factory for creating thread-safe backend host iterators.
/// Provides consistent load balancing behavior across multiple concurrent proxy workers.
/// </summary>
public static class BackendHostIteratorFactory
{
    private static readonly object _lock = new object();
    private static volatile int _roundRobinCounter = 0;
    private static volatile List<BackendHostHealth>? _cachedActiveHosts;
    private static volatile int _cacheVersion = 0; // Incremented when cache is invalidated
    
    // Thread-safe random number generator
    private static readonly ThreadLocal<Random> _threadRandom = new(() => new Random(Guid.NewGuid().GetHashCode()));

    /// <summary>
    /// Creates a thread-safe iterator for the specified load balance mode.
    /// This method is optimized for high concurrency with hundreds of proxy workers.
    /// </summary>
    public static IBackendHostIterator CreateIterator(
        IBackendService backendService,
        string loadBalanceMode, 
        IterationModeEnum mode = IterationModeEnum.SinglePass, 
        int maxRetries = 1)
    {
        var activeHosts = GetCachedActiveHosts(backendService);
        
        if (activeHosts == null || activeHosts.Count == 0)
        {
            return new EmptyBackendHostIterator();
        }

        return loadBalanceMode switch
        {
            Constants.RoundRobin => new RoundRobinHostIterator(activeHosts, mode, maxRetries),
            Constants.Latency => new LatencyBasedHostIterator(activeHosts, mode, maxRetries),
            Constants.Random => new RandomHostIterator(activeHosts, mode, maxRetries),
            _ => new RandomHostIterator(activeHosts, mode, maxRetries)
        };
    }

    /// <summary>
    /// Gets cached active hosts. Cache is invalidated only when explicitly requested
    /// by the backend service when host list changes.
    /// </summary>
    private static List<BackendHostHealth>? GetCachedActiveHosts(IBackendService backendService)
    {
        // Fast path: read the cached value without locking
        var cached = _cachedActiveHosts;
        if (cached != null)
        {
            return cached;
        }

        // Slow path: need to fetch hosts
        lock (_lock)
        {
            // Double-check: another thread may have populated the cache
            if (_cachedActiveHosts != null)
            {
                return _cachedActiveHosts;
            }

            _cachedActiveHosts = backendService.GetActiveHosts();
            return _cachedActiveHosts;
        }
    }

    /// <summary>
    /// Gets the next host index using thread-safe round-robin algorithm.
    /// </summary>
    public static int GetNextRoundRobinIndex(int hostCount)
    {
        if (hostCount <= 0) return 0;
        return Interlocked.Increment(ref _roundRobinCounter) % hostCount;
    }

    /// <summary>
    /// Gets a thread-safe random index.
    /// </summary>
    public static int GetRandomIndex(int hostCount)
    {
        if (hostCount <= 0) return 0;
        return _threadRandom.Value!.Next(hostCount);
    }

    /// <summary>
    /// Invalidates the cached hosts. Called by Backends service when host list changes.
    /// Thread-safe and optimized for frequent reads with infrequent invalidations.
    /// </summary>
    public static void InvalidateCache()
    {
        lock (_lock)
        {
            _cachedActiveHosts = null;
            Interlocked.Increment(ref _cacheVersion); // Track cache version for diagnostics
        }
    }

    /// <summary>
    /// Gets the current cache version for diagnostics.
    /// </summary>
    public static int GetCacheVersion() => _cacheVersion;
}