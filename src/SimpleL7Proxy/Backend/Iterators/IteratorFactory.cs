using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SimpleL7Proxy.Backend.Iterators;

/// <summary>
/// Static factory for creating thread-safe backend host iterators.
/// Provides consistent load balancing behavior across multiple concurrent proxy workers.
/// </summary>
public static class IteratorFactory
{
    private static readonly object _lock = new object();
    private static volatile int _roundRobinCounter = 0;
    private static volatile List<BaseHostHealth>? _cachedActiveHosts;
    private static volatile int _cacheVersion = 0; // Incremented when cache is invalidated
    
    // Thread-safe random number generator
    private static readonly ThreadLocal<Random> _threadRandom = new(() => new Random(Guid.NewGuid().GetHashCode()));

    /// <summary>
    /// Creates a host-ordering iterator for the given load balance mode and path.
    /// Pass/repeat control (SinglePass vs MultiPass, MaxAttempts) is owned by BaseIterator,
    /// not the iterator, so the same iterator works for either mode.
    /// </summary>
    /// <param name="backendService">The backend service to get active hosts from</param>
    /// <param name="loadBalanceMode">Load balancing strategy: "roundrobin", "latency", "timetofirstbyte", "prioritygroup", or "random"</param>
    /// <param name="requestPath">The normalized request path (e.g., /openai/v1/chat) to filter hosts by</param>
    public static IHostIterator CreateSinglePassIterator(
        IEndpointMonitorService backendService,
        string loadBalanceMode,
        string requestPath,
        out string modifiedPath)
    {
        return CreateSinglePassIterator(backendService, loadBalanceMode, requestPath, Constants.AnyPriority, out modifiedPath);
    }

    /// <summary>
    /// Creates a host-ordering iterator restricted to hosts that accept the given request priority.
    /// </summary>
    public static IHostIterator CreateSinglePassIterator(
        IEndpointMonitorService backendService,
        string loadBalanceMode,
        string requestPath,
        int requestPriority,
        out string modifiedPath)
    {
        // Get pre-categorized hosts from backend service
        var specificHosts = backendService.GetSpecificPathHosts();
        var catchAllHosts = backendService.GetCatchAllHosts();

        if ((specificHosts?.Count ?? 0) == 0 && (catchAllHosts?.Count ?? 0) == 0)
        {
            modifiedPath = requestPath; // No modification
            return new EmptyBackendHostIterator();
        }

        // requestPath is already normalized by server.cs
        var (filteredHosts, mp) = FilterHostsByPath(specificHosts!, catchAllHosts!, requestPath);
        modifiedPath = mp;

        if (requestPriority != Constants.AnyPriority)
        {
            filteredHosts = filteredHosts.Where(host => host.Config.AcceptsPriority(requestPriority)).ToList();
        }

        if (filteredHosts.Count == 0)
        {
            return new EmptyBackendHostIterator();
        }

        return loadBalanceMode switch
        {
            Constants.RoundRobin => new RoundRobinHostIterator(filteredHosts),
            Constants.Latency => new LatencyBasedHostIterator(filteredHosts),
            Constants.TimeToFirstByte => new TimeToFirstByteHostIterator(filteredHosts),
            Constants.PriorityGroup => new PriorityGroupHostIterator(filteredHosts, loadBalanceMode),
            Constants.Random => new RandomHostIterator(filteredHosts),
            _ => new RandomHostIterator(filteredHosts)
        };
    }

    /// <summary>
    /// Creates an iterator that visits an explicit host list strictly in the given order.
    /// Used by named Path_* routes, whose "hosts=" order is the explicit failover sequence —
    /// unlike the other iterators, this never reorders or rotates the supplied hosts.
    /// </summary>
    public static IHostIterator CreateFixedOrderIterator(List<BaseHostHealth> hosts)
    {
        return hosts.Count == 0
            ? new EmptyBackendHostIterator()
            : new FixedOrderHostIterator(hosts);
    }

    /// <summary>
    /// Filters hosts by path and returns both the matching hosts and the path with matched prefix removed.
    /// This enables backend hosts to handle requests without needing to know their routing prefix.
    /// </summary>
    private static (List<BaseHostHealth> hosts, string modifiedPath) FilterHostsByPath(
        List<BaseHostHealth> specificHosts, 
        List<BaseHostHealth> catchAllHosts, 
        string requestPath)
    {
        // Evaluate all matches once, excluding hosts marked for spin-down
        var matchedHosts = specificHosts
            .Where(host => !host.Config.IsSpinningDown)
            .Select(host => (host, result: host.Config.SupportsPath(requestPath)))
            .Where(x => x.result.IsMatch)
            .ToList();
        
        if (matchedHosts.Count > 0)
        {
            // Use the stripped path from the first match (all should strip the same way)
            return (matchedHosts.Select(x => x.host).ToList(), matchedHosts[0].result.StrippedPath);
        }
        
        // No specific match - return catch-all hosts, excluding spinning-down ones
        var activeCatchAll = catchAllHosts.Where(h => !h.Config.IsSpinningDown).ToList();
        return (activeCatchAll, requestPath);
    }



    /// <summary>
    /// Gets cached active hosts. Cache is invalidated only when explicitly requested
    /// by the backend service when host list changes.
    /// </summary>
    private static List<BaseHostHealth>? GetCachedActiveHosts(IEndpointMonitorService backendService)
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

    /// <summary>
    /// Creates a SharedHostIterator for use with the SharedIteratorRegistry.
    /// This creates a circular iterator that can be shared across multiple concurrent requests.
    /// </summary>
    /// <param name="backendService">The backend service to get active hosts from</param>
    /// <param name="loadBalanceMode">Load balancing strategy (used for initial ordering)</param>
    /// <param name="requestPath">The normalized request path to filter hosts by</param>
    /// <param name="modifiedPath">Output: the path with matched prefix removed</param>
    /// <returns>A SharedHostIterator configured for circular iteration</returns>
    public static SharedHostIterator CreateSharedIterator(
        IEndpointMonitorService backendService,
        string loadBalanceMode,
        string requestPath,
        out string modifiedPath)
    {
        // Get pre-categorized hosts from backend service
        var specificHosts = backendService.GetSpecificPathHosts();
        var catchAllHosts = backendService.GetCatchAllHosts();
        
        if ((specificHosts?.Count ?? 0) == 0 && (catchAllHosts?.Count ?? 0) == 0)
        {
            modifiedPath = requestPath;
            return new SharedHostIterator(new List<BaseHostHealth>(), requestPath, requestPath);
        }

        // requestPath is already normalized by server.cs
        var (filteredHosts, mp) = FilterHostsByPath(specificHosts!, catchAllHosts!, requestPath);
        modifiedPath = mp;

        // Order hosts based on load balance mode for initial distribution
        var orderedHosts = loadBalanceMode switch
        {
            Constants.Latency => filteredHosts.OrderBy(h => h.AverageLatencyMs).ToList(),
            Constants.TimeToFirstByte => filteredHosts.OrderBy(h => h.TimeToFirstByteMs).ToList(),
            Constants.PriorityGroup => filteredHosts
                .GroupBy(h => h.Config.PriorityGroup)
                .OrderBy(group => group.Key)
                .SelectMany(group => group)
                .ToList(),
            Constants.Random => filteredHosts.OrderBy(_ => _threadRandom.Value!.Next()).ToList(),
            _ => filteredHosts // Round-robin uses natural order
        };

        return new SharedHostIterator(orderedHosts, requestPath, modifiedPath);
    }

    /// <summary>
    /// Gets the filtered hosts for a given path without creating an iterator.
    /// Useful for the SharedIteratorRegistry to create SharedHostIterators.
    /// </summary>
    /// <param name="backendService">The backend service to get active hosts from</param>
    /// <param name="loadBalanceMode">Load balancing strategy (used for initial ordering)</param>
    /// <param name="requestPath">The normalized request path to filter hosts by</param>
    /// <param name="modifiedPath">Output: the path with matched prefix removed</param>
    /// <returns>List of filtered and ordered hosts</returns>
    public static List<BaseHostHealth> GetFilteredHosts(
        IEndpointMonitorService backendService,
        string loadBalanceMode,
        string requestPath,
        out string modifiedPath)
    {
        var specificHosts = backendService.GetSpecificPathHosts();
        var catchAllHosts = backendService.GetCatchAllHosts();
        
        if ((specificHosts?.Count ?? 0) == 0 && (catchAllHosts?.Count ?? 0) == 0)
        {
            modifiedPath = requestPath;
            return new List<BaseHostHealth>();
        }

        // requestPath is already normalized by server.cs
        var (filteredHosts, mp) = FilterHostsByPath(specificHosts!, catchAllHosts!, requestPath);
        modifiedPath = mp;

        // Order hosts based on load balance mode
        return loadBalanceMode switch
        {
            Constants.Latency => filteredHosts.OrderBy(h => h.AverageLatencyMs).ToList(),
            Constants.TimeToFirstByte => filteredHosts.OrderBy(h => h.TimeToFirstByteMs).ToList(),
            Constants.PriorityGroup => filteredHosts
                .GroupBy(h => h.Config.PriorityGroup)
                .OrderBy(group => group.Key)
                .SelectMany(group => group)
                .ToList(),
            Constants.Random => filteredHosts.OrderBy(_ => _threadRandom.Value!.Next()).ToList(),
            _ => filteredHosts
        };
    }
}