using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SimpleL7Proxy.Backend.Iterators;

/// <summary>
/// Iterator that distributes requests across backend hosts in round-robin fashion.
/// Uses a global counter to ensure fair distribution across multiple concurrent iterators.
/// </summary>
public class RoundRobinHostIterator : HostIterator
{
    private static long _globalCounter = 0;
    private int _currentIndex;
    private int _hostsVisitedInCurrentLap;

    public RoundRobinHostIterator(List<BaseHostHealth> hosts)
        : base(hosts)
    {
        _currentIndex = -1;
        _hostsVisitedInCurrentLap = 0;
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
    /// Moves to the next host in round-robin order. Returns false once every host in
    /// this lap has been visited; NextHost calls Reset() to start a new lap.
    /// </summary>
    public override bool MoveNext()
    {
        if (_hosts.Count == 0) return false;

        // Complete the current lap after every host has been visited.
        if (_hostsVisitedInCurrentLap >= _hosts.Count)
        {
            return false;
        }
        
        // Use global counter to ensure fair distribution across all iterators
        long counter = Interlocked.Increment(ref _globalCounter);
        _currentIndex = (int)(counter % _hosts.Count);
        _hostsVisitedInCurrentLap++;
        
        return true;
    }

    /// <summary>
    /// Resets the iterator to start a fresh lap.
    /// </summary>
    public override void Reset()
    {
        _currentIndex = -1;
        _hostsVisitedInCurrentLap = 0;
    }
}
