namespace SimpleL7Proxy.Backend.Iterators;

/// <summary>
/// Iterator used when there are no active backend hosts available.
/// Always returns false on MoveNext() to indicate no hosts to iterate.
/// </summary>
public class EmptyBackendHostIterator : HostIterator
{
    public EmptyBackendHostIterator() : base([])
    {
    }

    /// <summary>
    /// Gets the current host. Always throws since there are no hosts.
    /// </summary>
    public override BaseHostHealth Current => throw new InvalidOperationException("No active hosts available.");

    /// <summary>
    /// Attempts to move to the next host. Always returns false since there are no hosts.
    /// </summary>
    public override bool MoveNext() => false;

    /// <summary>
    /// Resets the iterator. Does nothing since there are no hosts.
    /// </summary>
    public override void Reset()
    {
        // No-op - nothing to reset
    }
}
