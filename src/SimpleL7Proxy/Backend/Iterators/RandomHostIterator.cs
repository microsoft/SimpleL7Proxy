using System;
using System.Collections.Generic;
using System.Linq;

namespace SimpleL7Proxy.Backend.Iterators;

/// <summary>
/// Iterator that selects backend hosts in randomized order.
/// Pre-generates a shuffled order for each lap to ensure all hosts are visited.
/// </summary>
public class RandomHostIterator : HostIterator
{
    private List<int> _hostIndices;
    private int _currentIndex;

    public RandomHostIterator(List<BaseHostHealth> hosts)
        : base(hosts)
    {
        _hostIndices = Enumerable.Range(0, _hosts.Count).ToList();
        ShuffleList(_hostIndices);
        _currentIndex = -1; // Will be incremented on first MoveNext
    }

    /// <summary>
    /// Gets the current host being pointed to by the iterator.
    /// </summary>
    public override BaseHostHealth Current => _hosts[_hostIndices[_currentIndex]];

    /// <summary>
    /// Moves to the next host in the randomized order.
    /// </summary>
    public override bool MoveNext()
    {
        _currentIndex++;
        return _currentIndex < _hostIndices.Count;
    }

    /// <summary>
    /// Resets the iterator to start a fresh lap, re-shuffling the host order.
    /// </summary>
    public override void Reset()
    {
        _currentIndex = -1;
        ShuffleList(_hostIndices);
    }

    /// <summary>
    /// Shuffles the host indices list using thread-safe random number generation.
    /// </summary>
    private static void ShuffleList<T>(List<T> list)
    {
        var random = IteratorFactory.GetRandomIndex(int.MaxValue);
        var rng = new Random(random);
        
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
