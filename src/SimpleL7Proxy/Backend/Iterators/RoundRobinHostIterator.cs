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
    private int _hostsVisitedInCurrentPass;

    public RoundRobinHostIterator(List<BaseHostHealth> hosts, IterationModeEnum mode, int maxAttempts)
        : base(hosts, mode, maxAttempts)
    {
        _currentIndex = -1;
        _hostsVisitedInCurrentPass = 0;
    }

    /// <summary>
    /// Gets the current host being pointed to by the iterator.
    /// </summary>
    public override BaseHostHealth Current 
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
    /// In SinglePass mode: stops after visiting each host once.
    /// In MultiPass mode: continues until maxAttempts total attempts are reached (handled by base class).
    /// </summary>
    protected override bool MoveToNextHost()
    {
        if (_hosts.Count == 0) return false;
        
        // SinglePass mode: stop after visiting all hosts once
        if (_mode == IterationModeEnum.SinglePass && _hostsVisitedInCurrentPass >= _hosts.Count)
        {
            return false; // Completed this pass
        }
        
        // Use global counter to ensure fair distribution across all iterators
        long counter = Interlocked.Increment(ref _globalCounter);
        _currentIndex = (int)(counter % _hosts.Count);
        _hostsVisitedInCurrentPass++;
        
        return true;
    }

    /// <summary>
    /// Called when starting a new pass - reset the visit counter for the new pass.
    /// </summary>
    protected override void OnNewPassStarted()
    {
        _currentIndex = -1;
        _hostsVisitedInCurrentPass = 0; // Reset counter for new pass
    }

    /// <summary>
    /// Resets the iterator to its initial state.
    /// </summary>
    protected override void ResetToInitialState()
    {
        _currentIndex = -1;
        _hostsVisitedInCurrentPass = 0;
    }

    /// <summary>
    /// Records the result of a request to a host.
    /// Round robin doesn't adjust selection based on results.
    /// </summary>
    public override void RecordResult(BaseHostHealth host, bool success)
    {
        // Round robin doesn't adjust selection based on results
    }
}