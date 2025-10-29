using System;
using System.Collections.Generic;
using System.Linq;

namespace SimpleL7Proxy.Backend;

public class LatencyBasedHostIterator : BaseHostIterator
{
    private int _currentHostIndex;

    public LatencyBasedHostIterator(List<BaseHostHealth> hosts, IterationModeEnum mode, int maxAttempts)
        : base(hosts?.OrderBy(h => h.AverageLatency()).ToList() ?? throw new ArgumentNullException(nameof(hosts)), mode, maxAttempts)
    {
        _currentHostIndex = -1; // Will be incremented on first MoveNext
    }


    /// <summary>
    /// Gets the current host being pointed to by the iterator.
    /// </summary>
    public override BaseHostHealth Current => _hosts[_currentHostIndex];

    /// <summary>
    /// Moves to the next host in latency order.
    /// </summary>
    protected override bool MoveToNextHost()
    {
        _currentHostIndex++;
        return _currentHostIndex < _hosts.Count;
    }

    /// <summary>
    /// Called when starting a new pass - reset to beginning of host list.
    /// </summary>
    protected override void OnNewPassStarted()
    {
        _currentHostIndex = -1; // Will be incremented on next MoveToNextHost call
    }

    /// <summary>
    /// Resets the iterator to its initial state.
    /// </summary>
    protected override void ResetToInitialState()
    {
        _currentHostIndex = -1;
    }

    /// <summary>
    /// Records the result of a request to a host.
    /// For latency-based iteration, latency is updated by the backend service health checker.
    /// </summary>
    public override void RecordResult(BaseHostHealth host, bool success)
    {
        // Latency tracking is handled by the backend service health checker
        // No additional tracking needed for this iterator type
    }
}