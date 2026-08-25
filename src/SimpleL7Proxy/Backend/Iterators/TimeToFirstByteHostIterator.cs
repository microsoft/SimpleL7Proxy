using System;
using System.Collections.Generic;
using System.Linq;

namespace SimpleL7Proxy.Backend.Iterators;

public class TimeToFirstByteHostIterator : HostIterator
{
    private int _currentHostIndex;

    public TimeToFirstByteHostIterator(List<BaseHostHealth> hosts)
        : base(hosts?.OrderBy(h => h.TimeToFirstByteMs).ToList() ?? throw new ArgumentNullException(nameof(hosts)))
    {
        _currentHostIndex = -1; // Will be incremented on first MoveNext
    }

    /// <summary>
    /// Gets the current host being pointed to by the iterator.
    /// </summary>
    public override BaseHostHealth Current => _hosts[_currentHostIndex];

    /// <summary>
    /// Moves to the next host in TTFB order.
    /// </summary>
    public override bool MoveNext()
    {
        _currentHostIndex++;
        return _currentHostIndex < _hosts.Count;
    }

    /// <summary>
    /// Resets the iterator to start a fresh lap.
    /// </summary>
    public override void Reset()
    {
        _currentHostIndex = -1;
    }
}
