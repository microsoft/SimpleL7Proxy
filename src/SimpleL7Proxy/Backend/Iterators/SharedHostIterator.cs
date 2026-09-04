using System;
using System.Collections.Generic;
using System.Threading;

namespace SimpleL7Proxy.Backend.Iterators;

/// <summary>
/// Shares one circular, atomically advanced host sequence across concurrent requests.
/// Per-request attempt and circuit-breaker state remains in <see cref="IterationState"/>.
/// </summary>
public sealed class SharedHostIterator : BaseIterator, IDisposable
{
    private readonly List<BaseHostHealth> _hosts;
    private readonly string _path;
    private readonly string _modifiedPath;
    private readonly object _lock = new();  // Only used for Dispose and GetHostsSnapshot
    
    private int _currentIndex;
    private DateTime _lastUsed;
    private volatile bool _disposed;  // Volatile for lock-free read in TryGetNextHost

    /// <summary>
    /// Creates a new SharedHostIterator wrapping a snapshot of hosts.
    /// </summary>
    /// <param name="hosts">The list of hosts to iterate over (a snapshot is taken)</param>
    /// <param name="path">The path this iterator is associated with</param>
    /// <param name="modifiedPath">The path with matched prefix stripped</param>
    public SharedHostIterator(List<BaseHostHealth> hosts, string path, string modifiedPath)
    {
        _hosts = new List<BaseHostHealth>(hosts ?? throw new ArgumentNullException(nameof(hosts)));
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _modifiedPath = modifiedPath ?? path;
        _currentIndex = -1;
        _lastUsed = DateTime.UtcNow;
    }

    /// <summary>Gets the normalized request path used as the registry key.</summary>
    public string Path => _path;

    /// <summary>Gets the request path after removing its matched routing prefix.</summary>
    public string ModifiedPath => _modifiedPath;

    /// <summary>Gets the approximate time at which a request last selected a host.</summary>
    public DateTime LastUsed => _lastUsed;

    /// <inheritdoc/>
    public override int HostCount => _hosts.Count;

    /// <summary>
    /// Circular and never exhausts on its own, so SinglePass is capped by host count instead.
    /// </summary>
    protected override int SinglePassCap => HostCount;

    /// <inheritdoc/>
    protected override IReadOnlyList<BaseHostHealth> Hosts => GetHostsSnapshot();

    /// <summary>
    /// Fetches one candidate via the atomic circular index — no lap concept to reset.
    /// </summary>
    protected override bool FetchCandidate(IterationState state, out BaseHostHealth? host)
        => TryGetNextHost(out host);

    /// <summary>
    /// Atomically gets the next host from the iterator.
    /// Uses circular iteration - automatically wraps around when all hosts have been visited.
    /// Thread-safe: multiple concurrent callers will each get a different host in round-robin fashion.
    /// Lock-free implementation using Interlocked.Increment for high throughput.
    /// </summary>
    /// <param name="host">The next host, or null if no hosts are available</param>
    /// <returns>True if a host was retrieved, false if no hosts are available</returns>
    private bool TryGetNextHost(out BaseHostHealth? host)
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
    /// Gets a snapshot of the current hosts for debugging.
    /// </summary>
    private IReadOnlyList<BaseHostHealth> GetHostsSnapshot()
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
