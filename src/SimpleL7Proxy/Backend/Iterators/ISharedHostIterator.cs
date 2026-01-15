namespace SimpleL7Proxy.Backend.Iterators;

/// <summary>
/// Thread-safe wrapper around IHostIterator for sharing across multiple concurrent requests.
/// Provides atomic TryGetNextHost operations and automatic reset when exhausted.
/// </summary>
public interface ISharedHostIterator
{
    /// <summary>
    /// Atomically gets the next host from the iterator.
    /// Thread-safe: multiple concurrent callers will each get a different host.
    /// Auto-resets when all hosts have been visited.
    /// </summary>
    /// <param name="host">The next host, or null if no hosts are available</param>
    /// <returns>True if a host was retrieved, false if no hosts are available</returns>
    bool TryGetNextHost(out BaseHostHealth? host);

    /// <summary>
    /// Records the result of a request to a host for load balancing feedback.
    /// Thread-safe.
    /// </summary>
    /// <param name="host">The host that was used</param>
    /// <param name="success">Whether the request was successful</param>
    void RecordResult(BaseHostHealth host, bool success);

    /// <summary>
    /// Gets the path this iterator is associated with.
    /// </summary>
    string Path { get; }

    /// <summary>
    /// Gets the timestamp when this iterator was last used.
    /// </summary>
    DateTime LastUsed { get; }

    /// <summary>
    /// Gets the number of hosts in this iterator.
    /// </summary>
    int HostCount { get; }

    /// <summary>
    /// Gets the iteration mode for this iterator.
    /// </summary>
    IterationModeEnum Mode { get; }
}
