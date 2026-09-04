namespace SimpleL7Proxy.Backend.Iterators;

/// <summary>
/// Registry for managing shared host iterators by path.
/// Allows multiple requests to the same path to share the same iterator,
/// ensuring fair round-robin distribution across concurrent requests.
/// </summary>
public interface ISharedIteratorRegistry
{
    /// <summary>
    /// Gets an existing iterator for the path or creates a new one using the factory.
    /// Thread-safe: multiple concurrent requests to the same path will share the same iterator.
    /// </summary>
    /// <param name="path">The request path (normalized) to use as the key</param>
    /// <param name="factory">Factory function that resolves the hosts and modified path for a new entry</param>
    /// <returns>A shared iterator for the path (includes ModifiedPath for prefix-stripped path)</returns>
    SharedHostIterator GetOrCreate(
        string path,
        Func<(List<BaseHostHealth> hosts, string modifiedPath)> factory);

    /// <summary>
    /// Invalidates all cached iterators. Call when backend configuration changes.
    /// </summary>
    void InvalidateAll();

    /// <summary>
    /// Invalidates the iterator for a specific path.
    /// </summary>
    /// <param name="path">The path whose iterator should be invalidated</param>
    void Invalidate(string path);

    /// <summary>
    /// Gets the number of currently cached iterators.
    /// </summary>
    int Count { get; }
}
