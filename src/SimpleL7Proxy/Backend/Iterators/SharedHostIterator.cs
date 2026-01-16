using System;
using System.Collections.Generic;
using System.Threading;

namespace SimpleL7Proxy.Backend.Iterators;

/// <summary>
/// Thread-safe wrapper around IHostIterator for sharing across multiple concurrent requests.
/// Provides atomic TryGetNextHost operations and automatic reset when exhausted (circular behavior).
/// 
/// DESIGN:
/// ┌─────────────────────────────────────────────────────────────────────────────┐
/// │  SharedHostIterator - Thread-Safe Circular Iterator                         │
/// ├─────────────────────────────────────────────────────────────────────────────┤
/// │                                                                             │
/// │  Request1 ───┐                                                              │
/// │              │    ┌──────────────────────────────────┐                      │
/// │  Request2 ───┼───►│  TryGetNextHost() [lock]         │───► Host A           │
/// │              │    │  - Atomic MoveNext + Current     │                      │
/// │  Request3 ───┘    │  - Auto-reset when exhausted     │                      │
/// │                   └──────────────────────────────────┘                      │
/// │                                                                             │
/// │  Hosts: [A, B, C] ──► Position cycles: 0→1→2→0→1→2→...                      │
/// │                                                                             │
/// └─────────────────────────────────────────────────────────────────────────────┘
/// </summary>
public sealed class SharedHostIterator : ISharedHostIterator, IDisposable
{
    private readonly List<BaseHostHealth> _hosts;
    private readonly string _path;
    private readonly IterationModeEnum _mode;
    private readonly object _lock = new();  // Only used for Dispose and GetHostsSnapshot
    
    private int _currentIndex;
    private DateTime _lastUsed;
    private volatile bool _disposed;  // Volatile for lock-free read in TryGetNextHost

    /// <summary>
    /// Creates a new SharedHostIterator wrapping a snapshot of hosts.
    /// </summary>
    /// <param name="hosts">The list of hosts to iterate over (a snapshot is taken)</param>
    /// <param name="path">The path this iterator is associated with</param>
    /// <param name="mode">The iteration mode</param>
    public SharedHostIterator(List<BaseHostHealth> hosts, string path, IterationModeEnum mode)
    {
        _hosts = new List<BaseHostHealth>(hosts ?? throw new ArgumentNullException(nameof(hosts)));
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _mode = mode;
        _currentIndex = -1;
        _lastUsed = DateTime.UtcNow;
    }

    /// <inheritdoc/>
    public string Path => _path;

    /// <inheritdoc/>
    public DateTime LastUsed => _lastUsed;

    /// <inheritdoc/>
    public int HostCount => _hosts.Count;

    /// <inheritdoc/>
    public IterationModeEnum Mode => _mode;

    /// <summary>
    /// Atomically gets the next host from the iterator.
    /// Uses circular iteration - automatically wraps around when all hosts have been visited.
    /// Thread-safe: multiple concurrent callers will each get a different host in round-robin fashion.
    /// Lock-free implementation using Interlocked.Increment for high throughput.
    /// </summary>
    /// <param name="host">The next host, or null if no hosts are available</param>
    /// <returns>True if a host was retrieved, false if no hosts are available</returns>
    public bool TryGetNextHost(out BaseHostHealth? host)
    {
        if (_disposed)
        {
            host = null;
            return false;
        }

        var count = _hosts.Count;
        if (count == 0)
        {
            host = null;
            return false;
        }

        // Lock-free circular increment using Interlocked
        // Cast to uint handles int overflow gracefully (wraps to 0 instead of going negative)
        var index = Interlocked.Increment(ref _currentIndex);
        var actualIndex = (int)((uint)index % (uint)count);
        
        host = _hosts[actualIndex];
        _lastUsed = DateTime.UtcNow;  // Doesn't need to be precise for TTL
        return true;
    }

    /// <summary>
    /// Records the result of a request to a host.
    /// Currently a no-op for round-robin style sharing, but can be extended
    /// for adaptive load balancing.
    /// </summary>
    /// <param name="host">The host that was used</param>
    /// <param name="success">Whether the request was successful</param>
    public void RecordResult(BaseHostHealth host, bool success)
    {
        // For shared iterators, we don't adjust selection based on individual results
        // The circuit breaker at the host level handles failure tracking
        // This can be extended later for adaptive load balancing if needed
    }

    /// <summary>
    /// Gets a snapshot of the current hosts for debugging.
    /// </summary>
    public IReadOnlyList<BaseHostHealth> GetHostsSnapshot()
    {
        lock (_lock)
        {
            return _hosts.AsReadOnly();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        lock (_lock)
        {
            _disposed = true;
            _hosts.Clear();
        }
    }
}
