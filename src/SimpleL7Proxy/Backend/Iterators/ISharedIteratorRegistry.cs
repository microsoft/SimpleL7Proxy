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
    /// <param name="factory">Factory function to create a new iterator if one doesn't exist</param>
    /// <returns>A shared iterator for the path</returns>
    ISharedHostIterator GetOrCreate(string path, Func<IHostIterator> factory);

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
