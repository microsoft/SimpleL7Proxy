using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using SimpleL7Proxy.Config;

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
public sealed class SharedIteratorRegistry : ISharedIteratorRegistry, IShutdownParticipant, IDisposable, IConfigChangeSubscriber
{
    public int ShutdownOrder => 200;

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    private readonly ConcurrentDictionary<string, Lazy<SharedHostIterator>> _iterators = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<SharedIteratorRegistry> _logger;
    private readonly ProxyConfig _options;
    private readonly Timer _cleanupTimer;
    private TimeSpan _iteratorTTL;
    private TimeSpan _cleanupInterval;
    private volatile bool _disposed;

    /// <summary>
    /// Creates a new SharedIteratorRegistry with the specified TTL and cleanup interval.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output</param>
    /// <param name="backendOptions">Backend configuration options</param>
    public SharedIteratorRegistry(
        ILogger<SharedIteratorRegistry> logger,
        ProxyConfig backendOptions,
        ConfigChangeNotifier configChangeNotifier)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = backendOptions ?? throw new ArgumentNullException(nameof(backendOptions));

        InitVars();
        // Start cleanup timer
        _cleanupTimer = new Timer(
            CleanupStaleIterators,
            null,
            _cleanupInterval,
            _cleanupInterval);

        _logger.LogDebug(
            "[SharedIteratorRegistry] Initialized with TTL={TTL}s, CleanupInterval={Interval}s",
            _iteratorTTL.TotalSeconds, _cleanupInterval.TotalSeconds);

        configChangeNotifier.Subscribe(this,
            o => o.SharedIteratorTTLSeconds,
            o => o.SharedIteratorCleanupIntervalSeconds);
    }

    public Task OnConfigChangedAsync(IReadOnlyList<ConfigChange> changes, ProxyConfig backendOptions, CancellationToken cancellationToken)
    {
        InitVars();
        return Task.CompletedTask;
    }

    public void InitVars()
    {
        _iteratorTTL = TimeSpan.FromSeconds(Math.Max(10, _options.SharedIteratorTTLSeconds));
        _cleanupInterval = TimeSpan.FromSeconds(Math.Max(5, _options.SharedIteratorCleanupIntervalSeconds));
    }
    /// <inheritdoc/>
    public int Count => _iterators.Count;

    /// <inheritdoc/>
    public ISharedHostIterator GetOrCreate(string path, Func<(IHostIterator iterator, string modifiedPath)> factory)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SharedIteratorRegistry));

        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(factory);

        var normalizedPath = NormalizePath(path);

        var lazyIterator = _iterators.GetOrAdd(
            normalizedPath,
            static (_, state) => new Lazy<SharedHostIterator>(() =>
            {
                var (baseIterator, modifiedPath) = state.factory();
                var hosts = ExtractHostsFromIterator(baseIterator);
                var iterator = new SharedHostIterator(hosts, state.normalizedPath, modifiedPath);
                return iterator;
            }, LazyThreadSafetyMode.ExecutionAndPublication),
            (factory, normalizedPath));

        var iterator = lazyIterator.Value;
        _logger.LogDebug(
            "[SharedIteratorRegistry] Using iterator for path '{Path}' with {HostCount} hosts",
            normalizedPath, iterator.HostCount);

        return iterator;
    }

    /// <inheritdoc/>
    public void InvalidateAll()
    {
        var entries = _iterators.ToArray();
        var count = entries.Length;

        foreach (var entry in entries)
        {
            if (_iterators.TryRemove(entry.Key, out var lazyIterator) && lazyIterator.IsValueCreated)
            {
                lazyIterator.Value.Dispose();
            }
        }

        if (count > 0)
            _logger.LogInformation(
                "[SharedIteratorRegistry] Invalidated all {Count} cached iterators",
                count);
    }

    /// <inheritdoc/>
    public void Invalidate(string path)
    {
        var normalizedPath = NormalizePath(path);

        if (_iterators.TryRemove(normalizedPath, out var lazyIterator))
        {
            if (lazyIterator.IsValueCreated)
            {
                lazyIterator.Value.Dispose();
            }

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
        List<(string Key, SharedHostIterator Iterator)> toDispose = new();

        foreach (var kvp in _iterators)
        {
            if (!kvp.Value.IsValueCreated)
            {
                continue;
            }

            var iterator = kvp.Value.Value;
            if (iterator.LastUsed < cutoff)
            {
                if (_iterators.TryRemove(kvp.Key, out _))
                {
                    toDispose.Add((kvp.Key, iterator));
                }
            }
        }

        foreach (var entry in toDispose)
        {
            entry.Iterator.Dispose();
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

        foreach (var entry in _iterators)
        {
            if (entry.Value.IsValueCreated)
            {
                entry.Value.Value.Dispose();
            }
        }

        _iterators.Clear();
    }

}
