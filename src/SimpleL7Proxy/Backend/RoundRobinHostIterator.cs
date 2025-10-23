using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SimpleL7Proxy.Backend;

/// <summary>
/// Iterator that distributes requests across backend hosts in round-robin fashion.
/// Uses a global counter to ensure fair distribution across multiple concurrent iterators.
/// </summary>
public class RoundRobinHostIterator : BaseHostIterator
{
    private static long _globalCounter = 0;
    private int _currentIndex;

    public RoundRobinHostIterator(List<BackendHostHealth> hosts, IterationModeEnum mode, int maxLoop)
        : base(hosts, mode, maxLoop)
    {
        _currentIndex = -1; // Will be incremented on first MoveNext
    }

    /// <summary>
    /// Gets the current host being pointed to by the iterator.
    /// </summary>
    public override BackendHostHealth Current 
    {
        get
        {
            if (_currentIndex < 0 || _currentIndex >= _hosts.Count)
                throw new InvalidOperationException("Iterator is not positioned at a valid element.");
            return _hosts[_currentIndex];
        }
    }

    /// <summary>
    /// Moves to the next host in round-robin order.
    /// </summary>
    protected override bool MoveToNextHost()
    {
        if (_hosts.Count == 0) return false;
        
        // Use global counter to ensure fair distribution across all iterators
        long counter = Interlocked.Increment(ref _globalCounter);
        _currentIndex = (int)(counter % _hosts.Count);
        
        return true;
    }

    /// <summary>
    /// Called when starting a new pass - continue round-robin from current position.
    /// </summary>
    protected override void OnNewPassStarted()
    {
        _currentIndex = -1; // Will be set properly on next MoveToNextHost call
    }

    /// <summary>
    /// Resets the iterator to its initial state.
    /// </summary>
    protected override void ResetToInitialState()
    {
        _currentIndex = -1;
    }

    /// <summary>
    /// Records the result of a request to a host.
    /// Round robin doesn't adjust selection based on results.
    /// </summary>
    public override void RecordResult(BackendHostHealth host, bool success)
    {
        // Round robin doesn't adjust selection based on results
    }
}