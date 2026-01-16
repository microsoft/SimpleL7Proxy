using System;
using System.Collections.Generic;

namespace SimpleL7Proxy.Backend.Iterators;

/// <summary>
/// Iterator used when there are no active backend hosts available.
/// Always returns false on MoveNext() to indicate no hosts to iterate.
/// </summary>
public class EmptyBackendHostIterator : IHostIterator
{
    /// <summary>
    /// Gets the current host. Always throws since there are no hosts.
    /// </summary>
    public BaseHostHealth Current => throw new InvalidOperationException("No active hosts available.");

    /// <summary>
    /// Gets the current host as object. Always throws since there are no hosts.
    /// </summary>
    object System.Collections.IEnumerator.Current => Current;

    /// <summary>
    /// Indicates whether there are more hosts. Always returns false.
    /// </summary>
    public bool HasMoreHosts => false;

    /// <summary>
    /// Gets the maximum number of attempts. Always returns 0 since there are no hosts.
    /// </summary>
    public int MaxAttempts => 0;

    /// <summary>
    /// Gets the iteration mode. Returns SinglePass by default.
    /// </summary>
    public IterationModeEnum Mode => IterationModeEnum.SinglePass;

    /// <summary>
    /// Attempts to move to the next host. Always returns false since there are no hosts.
    /// </summary>
    public bool MoveNext() => false;

    /// <summary>
    /// Records the result of a request. Does nothing since there are no hosts.
    /// </summary>
    public void RecordResult(BaseHostHealth host, bool success)
    {
        // No-op - there are no hosts to record results for
    }

    /// <summary>
    /// Resets the iterator. Does nothing since there are no hosts.
    /// </summary>
    public void Reset()
    {
        // No-op - nothing to reset
    }

    /// <summary>
    /// Disposes the iterator. Does nothing since there are no resources to clean up.
    /// </summary>
    public void Dispose()
    {
        // No-op - no resources to dispose
    }
}