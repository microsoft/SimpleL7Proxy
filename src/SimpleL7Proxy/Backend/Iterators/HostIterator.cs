using System;
using System.Collections;
using System.Collections.Generic;

namespace SimpleL7Proxy.Backend.Iterators;

/// <summary>
/// Abstract base class for backend host ordering strategies (RoundRobin, Latency,
/// TimeToFirstByte, PriorityGroup, Random, FixedOrder). Each subclass defines a single
/// lap's traversal order; <see cref="NextHost"/> owns how many laps to run (SinglePass
/// vs MultiPass, MaxAttempts) and calls <see cref="Reset"/> to start a new lap.
/// </summary>
public abstract class HostIterator : IHostIterator
{
    protected readonly List<BaseHostHealth> _hosts;

    protected HostIterator(List<BaseHostHealth> hosts)
    {
        _hosts = hosts ?? throw new ArgumentNullException(nameof(hosts));
    }

    /// <summary>
    /// Gets the current host. Must be implemented by derived classes.
    /// </summary>
    public abstract BaseHostHealth Current { get; }

    /// <summary>
    /// Gets the current host as object for IEnumerator interface.
    /// </summary>
    object IEnumerator.Current => Current;

    /// <summary>
    /// Gets the total number of hosts in this iterator.
    /// </summary>
    public int HostCount => _hosts.Count;
    public IReadOnlyList<BaseHostHealth> Hosts => _hosts;

    /// <summary>
    /// Moves to the next host in this lap's order. Returns false once every host in
    /// this lap has been visited; derived classes implement the specific ordering.
    /// </summary>
    public abstract bool MoveNext();

    /// <summary>
    /// Records an actual request attempt after the host passes pre-send checks.
    /// Derived classes can override for strategy-specific tracking.
    /// </summary>
    public virtual void RecordResult(BaseHostHealth host, bool success)
    {
        // Default implementation does nothing; pass/attempt accounting lives in NextHost.
    }

    /// <summary>
    /// Resets the iterator to start a fresh lap (re-shuffle/re-rotate/re-sort as needed
    /// for the specific ordering strategy).
    /// </summary>
    public abstract void Reset();

    /// <summary>
    /// Disposes the iterator. Default implementation does nothing.
    /// </summary>
    public virtual void Dispose()
    {
        // Default implementation does nothing
    }
}
