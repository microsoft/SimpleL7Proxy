using System;
using System.Collections.Generic;
using System.Linq;

namespace SimpleL7Proxy.Backend;

/// <summary>
/// Iterator that selects backend hosts in randomized order.
/// Pre-generates a shuffled order for each pass to ensure all hosts are visited.
/// </summary>
public class RandomHostIterator : BaseHostIterator
{
    private List<int> _hostIndices;
    private int _currentIndex;

    public RandomHostIterator(List<BackendHostHealth> hosts, IterationModeEnum mode, int maxAttempts)
        : base(hosts, mode, maxAttempts)
    {
        _hostIndices = Enumerable.Range(0, _hosts.Count).ToList();
        ShuffleList(_hostIndices);
        _currentIndex = -1; // Will be incremented on first MoveNext
    }

    /// <summary>
    /// Gets the current host being pointed to by the iterator.
    /// </summary>
    public override BackendHostHealth Current => _hosts[_hostIndices[_currentIndex]];

    /// <summary>
    /// Moves to the next host in the randomized order.
    /// </summary>
    protected override bool MoveToNextHost()
    {
        _currentIndex++;
        return _currentIndex < _hostIndices.Count;
    }

    /// <summary>
    /// Called when starting a new pass - re-shuffle the host order.
    /// </summary>
    protected override void OnNewPassStarted()
    {
        _currentIndex = -1; // Will be incremented on next MoveToNextHost call
        ShuffleList(_hostIndices); // Re-shuffle for the new pass
    }

    /// <summary>
    /// Resets the iterator to its initial state.
    /// </summary>
    protected override void ResetToInitialState()
    {
        _currentIndex = -1;
        ShuffleList(_hostIndices);
    }

    /// <summary>
    /// Shuffles the host indices list using thread-safe random number generation.
    /// </summary>
    private static void ShuffleList<T>(List<T> list)
    {
        var random = BackendHostIteratorFactory.GetRandomIndex(int.MaxValue);
        var rng = new Random(random);
        
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>
    /// Records the result of a request to a host.
    /// Random selection doesn't need result tracking.
    /// </summary>
    public override void RecordResult(BackendHostHealth host, bool success)
    {
        // Random selection doesn't need result tracking
    }
}