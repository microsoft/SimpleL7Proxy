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
    private static volatile List<BaseHostHealth>? _cachedSpecificPathHosts;
    private static volatile List<BaseHostHealth>? _cachedCatchAllHosts;
    private static volatile int _cacheVersion = 0; // Incremented when cache is invalidated
    
    // Thread-safe random number generator
    private static readonly ThreadLocal<Random> _threadRandom = new(() => new Random(Guid.NewGuid().GetHashCode()));

    /// <summary>
    /// Creates an iterator that tries each host once in a single pass.
    /// Best for scenarios where you want to try all backends once and fail fast.
    /// </summary>
    /// <param name="backendService">The backend service to get active hosts from</param>
    /// <param name="loadBalanceMode">Load balancing strategy: "roundrobin", "latency", or "random"</param>
    /// <param name="fullURL">The full URL for the request (without host part) to filter hosts by path</param>
    /// <returns>An iterator configured for single-pass iteration</returns>
    public static IHostIterator CreateSinglePassIterator(
        IBackendService backendService,
        string loadBalanceMode,
        string fullURL,
        out string modifiedPath)
    {
        return CreateIteratorInternal(backendService, loadBalanceMode, IterationModeEnum.SinglePass, 1, fullURL, out modifiedPath);
    }

    /// <summary>
    /// Creates an iterator that retries across hosts up to a maximum total number of attempts.
    /// Will cycle through all hosts multiple times if needed until maxAttempts is reached.
    /// Best for high-availability scenarios where you want to retry aggressively.
    /// </summary>
    /// <param name="backendService">The backend service to get active hosts from</param>
    /// <param name="loadBalanceMode">Load balancing strategy: "roundrobin", "latency", or "random"</param>
    /// <param name="maxAttempts">Maximum total number of host attempts across all passes (e.g., 30)</param>
    /// <param name="fullURL">The full URL for the request (without host part) to filter hosts by path</param>
    /// <returns>An iterator configured for multi-pass iteration with retry limit</returns>
    public static IHostIterator CreateMultiPassIterator(
        IBackendService backendService,
        string loadBalanceMode,
        int maxAttempts,
        string fullURL,
        out string modifiedPath)
    {
        return CreateIteratorInternal(backendService, loadBalanceMode, IterationModeEnum.MultiPass, maxAttempts, fullURL, out modifiedPath);
    }

    /// <summary>
    /// Internal method to create a thread-safe iterator for the specified load balance mode.
    /// This method is optimized for high concurrency with hundreds of proxy workers.
    /// Filters hosts based on the request path extracted from the full URL.
    /// </summary>
    private static IHostIterator CreateIteratorInternal(
        IBackendService backendService,
        string loadBalanceMode,
        IterationModeEnum mode,
        int maxAttempts,
        string fullURL,
        out string modifiedPath)
    {
        // Get pre-categorized hosts from backend service
        var specificHosts = backendService.GetSpecificPathHosts();
        var catchAllHosts = backendService.GetCatchAllHosts();    
        
        if ((specificHosts?.Count ?? 0) == 0 && (catchAllHosts?.Count ?? 0) == 0)
        {
            modifiedPath = fullURL; // No modification
            return new EmptyBackendHostIterator();
        }

        // Extract path from fullURL to filter hosts
        var requestPath = ExtractPathFromURL(fullURL);
        var (filteredHosts, mp) = FilterHostsByPath(specificHosts!, catchAllHosts!, requestPath);
        modifiedPath = mp;

        if (filteredHosts.Count == 0)
        {
            return new EmptyBackendHostIterator();
        }

        // TODO: Store or use modifiedPath - it needs to be passed to the iterator or stored somewhere
        // For now, you'll need to decide where to use the modifiedPath

        return loadBalanceMode switch
        {
            Constants.RoundRobin => new RoundRobinHostIterator(filteredHosts, mode, maxAttempts),
            Constants.Latency => new LatencyBasedHostIterator(filteredHosts, mode, maxAttempts),
            Constants.Random => new RandomHostIterator(filteredHosts, mode, maxAttempts),
            _ => new RandomHostIterator(filteredHosts, mode, maxAttempts)
        };
    }

    /// <summary>
    /// Extracts the path portion from a full URL (without host part).
    /// Handles both absolute paths (/api/users) and relative paths (api/users).
    /// </summary>
    private static string ExtractPathFromURL(string fullURL)
    {
        if (string.IsNullOrEmpty(fullURL))
            return "/";

        // Try to parse as absolute URI first
        if (Uri.TryCreate(fullURL, UriKind.Absolute, out Uri? uri))
        {
            return uri.PathAndQuery;
        }

        // For relative paths, ensure they start with '/'
        return fullURL.StartsWith('/') ? fullURL : "/" + fullURL;
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
        // Evaluate all matches once
        var matchedHosts = specificHosts
            .Select(host => (host, result: host.Config.SupportsPath(requestPath)))
            .Where(x => x.result.IsMatch)
            .ToList();
        
        if (matchedHosts.Count > 0)
        {
            // Use the stripped path from the first match (all should strip the same way)
            return (matchedHosts.Select(x => x.host).ToList(), matchedHosts[0].result.StrippedPath);
        }
        
        // No specific match - return catch-all hosts with original path
        return (catchAllHosts, requestPath);
    }

    /// <summary>
    /// Gets cached categorized hosts (specific vs catch-all) with thread-safe lazy initialization.
    /// </summary>
    private static (List<BaseHostHealth> specificHosts, List<BaseHostHealth> catchAllHosts) GetCategorizedHosts(IBackendService backendService)
    {
        // Fast path: read cached values without locking
        var cachedSpecific = _cachedSpecificPathHosts;
        var cachedCatchAll = _cachedCatchAllHosts;
        
        if (cachedSpecific != null && cachedCatchAll != null)
        {
            return (cachedSpecific, cachedCatchAll);
        }

        // Slow path: need to categorize hosts
        lock (_lock)
        {
            // Double-check: another thread may have populated the cache
            if (_cachedSpecificPathHosts != null && _cachedCatchAllHosts != null)
            {
                return (_cachedSpecificPathHosts, _cachedCatchAllHosts);
            }

            var activeHosts = backendService.GetActiveHosts();
            var specificHosts = new List<BaseHostHealth>();
            var catchAllHosts = new List<BaseHostHealth>();
            
            // Categorize hosts once at startup
            foreach (var host in activeHosts)
            {
                var hostPartialPath = host.Config.PartialPath?.Trim();
                
                if (string.IsNullOrEmpty(hostPartialPath) || 
                    hostPartialPath == "/" || 
                    hostPartialPath == "/*")
                {
                    catchAllHosts.Add(host);
                }
                else
                {
                    specificHosts.Add(host);
                }
            }

            _cachedSpecificPathHosts = specificHosts;
            _cachedCatchAllHosts = catchAllHosts;
            _cachedActiveHosts = activeHosts; // Also update the active hosts cache
            
            return (specificHosts, catchAllHosts);
        }
    }

    /// <summary>
    /// Gets cached active hosts. Cache is invalidated only when explicitly requested
    /// by the backend service when host list changes.
    /// </summary>
    private static List<BaseHostHealth>? GetCachedActiveHosts(IBackendService backendService)
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
            _cachedSpecificPathHosts = null;
            _cachedCatchAllHosts = null;
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
    /// <param name="fullURL">The full URL for the request to filter hosts by path</param>
    /// <param name="modifiedPath">Output: the path with matched prefix removed</param>
    /// <returns>A SharedHostIterator configured for circular iteration</returns>
    public static SharedHostIterator CreateSharedIterator(
        IBackendService backendService,
        string loadBalanceMode,
        string fullURL,
        out string modifiedPath)
    {
        // Get pre-categorized hosts from backend service
        var specificHosts = backendService.GetSpecificPathHosts();
        var catchAllHosts = backendService.GetCatchAllHosts();
        
        if ((specificHosts?.Count ?? 0) == 0 && (catchAllHosts?.Count ?? 0) == 0)
        {
            modifiedPath = fullURL;
            return new SharedHostIterator(new List<BaseHostHealth>(), fullURL, IterationModeEnum.SinglePass);
        }

        // Extract path from fullURL to filter hosts
        var requestPath = ExtractPathFromURL(fullURL);
        var (filteredHosts, mp) = FilterHostsByPath(specificHosts!, catchAllHosts!, requestPath);
        modifiedPath = mp;

        // Order hosts based on load balance mode for initial distribution
        var orderedHosts = loadBalanceMode switch
        {
            Constants.Latency => filteredHosts.OrderBy(h => h.CalculatedAverageLatency).ToList(),
            Constants.Random => filteredHosts.OrderBy(_ => _threadRandom.Value!.Next()).ToList(),
            _ => filteredHosts // Round-robin uses natural order
        };

        return new SharedHostIterator(orderedHosts, requestPath, IterationModeEnum.SinglePass);
    }

    /// <summary>
    /// Gets the filtered hosts for a given path without creating an iterator.
    /// Useful for the SharedIteratorRegistry to create SharedHostIterators.
    /// </summary>
    /// <param name="backendService">The backend service to get active hosts from</param>
    /// <param name="loadBalanceMode">Load balancing strategy (used for initial ordering)</param>
    /// <param name="fullURL">The full URL for the request to filter hosts by path</param>
    /// <param name="modifiedPath">Output: the path with matched prefix removed</param>
    /// <returns>List of filtered and ordered hosts</returns>
    public static List<BaseHostHealth> GetFilteredHosts(
        IBackendService backendService,
        string loadBalanceMode,
        string fullURL,
        out string modifiedPath)
    {
        var specificHosts = backendService.GetSpecificPathHosts();
        var catchAllHosts = backendService.GetCatchAllHosts();
        
        if ((specificHosts?.Count ?? 0) == 0 && (catchAllHosts?.Count ?? 0) == 0)
        {
            modifiedPath = fullURL;
            return new List<BaseHostHealth>();
        }

        var requestPath = ExtractPathFromURL(fullURL);
        var (filteredHosts, mp) = FilterHostsByPath(specificHosts!, catchAllHosts!, requestPath);
        modifiedPath = mp;

        // Order hosts based on load balance mode
        return loadBalanceMode switch
        {
            Constants.Latency => filteredHosts.OrderBy(h => h.CalculatedAverageLatency).ToList(),
            Constants.Random => filteredHosts.OrderBy(_ => _threadRandom.Value!.Next()).ToList(),
            _ => filteredHosts
        };
    }
}