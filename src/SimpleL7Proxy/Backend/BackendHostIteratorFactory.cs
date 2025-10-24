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
    /// Creates an iterator that tries each host once in a single pass.
    /// Best for scenarios where you want to try all backends once and fail fast.
    /// </summary>
    /// <param name="backendService">The backend service to get active hosts from</param>
    /// <param name="loadBalanceMode">Load balancing strategy: "roundrobin", "latency", or "random"</param>
    /// <returns>An iterator configured for single-pass iteration</returns>
    public static IBackendHostIterator CreateSinglePassIterator(
        IBackendService backendService,
        string loadBalanceMode)
    {
        return CreateIteratorInternal(backendService, loadBalanceMode, IterationModeEnum.SinglePass, 1);
    }

    /// <summary>
    /// Creates an iterator that retries across hosts up to a maximum total number of attempts.
    /// Will cycle through all hosts multiple times if needed until maxAttempts is reached.
    /// Best for high-availability scenarios where you want to retry aggressively.
    /// </summary>
    /// <param name="backendService">The backend service to get active hosts from</param>
    /// <param name="loadBalanceMode">Load balancing strategy: "roundrobin", "latency", or "random"</param>
    /// <param name="maxAttempts">Maximum total number of host attempts across all passes (e.g., 30)</param>
    /// <returns>An iterator configured for multi-pass iteration with retry limit</returns>
    public static IBackendHostIterator CreateMultiPassIterator(
        IBackendService backendService,
        string loadBalanceMode,
        int maxAttempts)
    {
        return CreateIteratorInternal(backendService, loadBalanceMode, IterationModeEnum.MultiPass, maxAttempts);
    }

    /// <summary>
    /// Internal method to create a thread-safe iterator for the specified load balance mode.
    /// This method is optimized for high concurrency with hundreds of proxy workers.
    /// </summary>
    private static IBackendHostIterator CreateIteratorInternal(
        IBackendService backendService,
        string loadBalanceMode, 
        IterationModeEnum mode, 
        int maxAttempts)
    {
        var activeHosts = GetCachedActiveHosts(backendService);
        
        if (activeHosts == null || activeHosts.Count == 0)
        {
            return new EmptyBackendHostIterator();
        }

        return loadBalanceMode switch
        {
            Constants.RoundRobin => new RoundRobinHostIterator(activeHosts, mode, maxAttempts),
            Constants.Latency => new LatencyBasedHostIterator(activeHosts, mode, maxAttempts),
            Constants.Random => new RandomHostIterator(activeHosts, mode, maxAttempts),
            _ => new RandomHostIterator(activeHosts, mode, maxAttempts)
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