using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace SimpleL7Proxy.Backend.Iterators;

/// <summary>
/// Registry for managing shared host iterators by path.
/// Uses Dictionary with lock for thread-safe access (optimized for small number of paths).
/// Includes automatic cleanup of stale iterators.
/// 
/// ARCHITECTURE:
/// ┌─────────────────────────────────────────────────────────────────────────────┐
/// │  SharedIteratorRegistry                                                     │
/// ├─────────────────────────────────────────────────────────────────────────────┤
/// │                                                                             │
/// │  Dictionary<string, SharedHostIterator> + lock                              │
/// │  ┌─────────────────────────────────────────────────────────────────────┐   │
/// │  │  "/openai/deployments/gpt-4"  →  SharedHostIterator [Host1,Host2]   │   │
/// │  │  "/openai/deployments/gpt-35" →  SharedHostIterator [Host3,Host4]   │   │
/// │  │  "/"                          →  SharedHostIterator [Host1..Host4]  │   │
/// │  └─────────────────────────────────────────────────────────────────────┘   │
/// │                                                                             │
/// │  Note: Number of paths is bounded by number of hosts (typically small)      │
/// │  Cleanup Timer: Removes iterators not used for > TTL seconds               │
/// │                                                                             │
/// └─────────────────────────────────────────────────────────────────────────────┘
/// </summary>
public sealed class SharedIteratorRegistry : ISharedIteratorRegistry, IDisposable
{
    private readonly Dictionary<string, SharedHostIterator> _iterators = new();
    private readonly object _lock = new();
    private readonly ILogger<SharedIteratorRegistry> _logger;
    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _iteratorTTL;
    private readonly TimeSpan _cleanupInterval;
    private volatile bool _disposed;

    /// <summary>
    /// Creates a new SharedIteratorRegistry with the specified TTL and cleanup interval.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output</param>
    /// <param name="iteratorTTLSeconds">How long an unused iterator lives before cleanup (default: 60 seconds)</param>
    /// <param name="cleanupIntervalSeconds">How often to run cleanup (default: 30 seconds)</param>
    public SharedIteratorRegistry(
        ILogger<SharedIteratorRegistry> logger,
        int iteratorTTLSeconds = 60,
        int cleanupIntervalSeconds = 30)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _iteratorTTL = TimeSpan.FromSeconds(Math.Max(10, iteratorTTLSeconds));
        _cleanupInterval = TimeSpan.FromSeconds(Math.Max(5, cleanupIntervalSeconds));

        // Start cleanup timer
        _cleanupTimer = new Timer(
            CleanupStaleIterators,
            null,
            _cleanupInterval,
            _cleanupInterval);

        _logger.LogInformation(
            "[SharedIteratorRegistry] Initialized with TTL={TTL}s, CleanupInterval={Interval}s",
            _iteratorTTL.TotalSeconds, _cleanupInterval.TotalSeconds);
    }

    /// <inheritdoc/>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _iterators.Count;
            }
        }
    }

    /// <inheritdoc/>
    public ISharedHostIterator GetOrCreate(string path, Func<IHostIterator> factory)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SharedIteratorRegistry));

        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(factory);

        var normalizedPath = NormalizePath(path);

        lock (_lock)
        {
            // Fast path: iterator already exists
            if (_iterators.TryGetValue(normalizedPath, out var existing))
                return existing;

            // Slow path: create new iterator (factory called exactly once)
            var baseIterator = factory();
            var hosts = ExtractHostsFromIterator(baseIterator);
            
            _logger.LogDebug(
                "[SharedIteratorRegistry] Created new iterator for path '{Path}' with {HostCount} hosts",
                normalizedPath, hosts.Count);

            var iterator = new SharedHostIterator(hosts, normalizedPath, baseIterator.Mode);
            _iterators[normalizedPath] = iterator;
            return iterator;
        }
    }

    /// <inheritdoc/>
    public void InvalidateAll()
    {
        List<SharedHostIterator> toDispose;
        int count;
        
        lock (_lock)
        {
            count = _iterators.Count;
            toDispose = new List<SharedHostIterator>(_iterators.Values);
            _iterators.Clear();
        }

        // Dispose outside lock
        foreach (var iterator in toDispose)
        {
            iterator.Dispose();
        }

        _logger.LogInformation(
            "[SharedIteratorRegistry] Invalidated all {Count} cached iterators",
            count);
    }

    /// <inheritdoc/>
    public void Invalidate(string path)
    {
        var normalizedPath = NormalizePath(path);
        SharedHostIterator? iterator = null;
        
        lock (_lock)
        {
            if (_iterators.TryGetValue(normalizedPath, out iterator))
            {
                _iterators.Remove(normalizedPath);
            }
        }

        // Dispose outside lock
        if (iterator != null)
        {
            iterator.Dispose();
            _logger.LogDebug(
                "[SharedIteratorRegistry] Invalidated iterator for path '{Path}'",
                normalizedPath);
        }
    }

    /// <summary>
    /// Normalizes a path for consistent dictionary keying.
    /// - Converts to lowercase
    /// - Ensures leading slash
    /// - Removes trailing slash (except for root "/")
    /// </summary>
    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        var normalized = path.Trim().ToLowerInvariant();
        
        // Ensure leading slash
        if (!normalized.StartsWith('/'))
            normalized = "/" + normalized;
        
        // Remove trailing slash (but keep root "/")
        if (normalized.Length > 1 && normalized.EndsWith('/'))
            normalized = normalized.TrimEnd('/');

        return normalized;
    }

    /// <summary>
    /// Extracts the list of hosts from a base iterator by iterating through it once.
    /// </summary>
    private static List<BaseHostHealth> ExtractHostsFromIterator(IHostIterator iterator)
    {
        var hosts = new List<BaseHostHealth>();
        
        while (iterator.MoveNext())
        {
            hosts.Add(iterator.Current);
        }
        
        iterator.Reset();
        return hosts;
    }

    /// <summary>
    /// Timer callback to clean up iterators that haven't been used recently.
    /// </summary>
    private void CleanupStaleIterators(object? state)
    {
        if (_disposed) return;

        var cutoff = DateTime.UtcNow - _iteratorTTL;
        List<SharedHostIterator> toDispose = new();

        lock (_lock)
        {
            var keysToRemove = new List<string>();
            
            foreach (var kvp in _iterators)
            {
                if (kvp.Value.LastUsed < cutoff)
                {
                    keysToRemove.Add(kvp.Key);
                    toDispose.Add(kvp.Value);
                }
            }

            foreach (var key in keysToRemove)
            {
                _iterators.Remove(key);
            }
        }

        // Dispose outside lock
        foreach (var iterator in toDispose)
        {
            iterator.Dispose();
        }

        if (toDispose.Count > 0)
        {
            _logger.LogDebug(
                "[SharedIteratorRegistry] Cleanup removed {Count} stale iterators, {Remaining} remaining",
                toDispose.Count, Count);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cleanupTimer.Dispose();
        
        List<SharedHostIterator> toDispose;
        lock (_lock)
        {
            toDispose = new List<SharedHostIterator>(_iterators.Values);
            _iterators.Clear();
        }

        foreach (var iterator in toDispose)
        {
            iterator.Dispose();
        }

        _logger.LogInformation("[SharedIteratorRegistry] Disposed");
    }
}
