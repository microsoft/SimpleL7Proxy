namespace SimpleL7Proxy.Backend.Iterators;

/// <summary>
/// Iterator that visits hosts strictly in the order they were supplied — no reordering,
/// no rotation. Used for named Path_* routes, whose configured "hosts=" list order is
/// the explicit failover sequence the operator asked for.
/// </summary>
public sealed class FixedOrderHostIterator : HostIterator
{
    private int _currentIndex;

    public FixedOrderHostIterator(List<BaseHostHealth> hosts)
        : base(hosts)
    {
        _currentIndex = -1;
    }

    public override BaseHostHealth Current
    {
        get
        {
            if (_currentIndex < 0 || _currentIndex >= _hosts.Count)
                throw new InvalidOperationException("Iterator is not positioned at a valid element.");
            return _hosts[_currentIndex];
        }
    }

    public override bool MoveNext()
    {
        if (_currentIndex + 1 >= _hosts.Count)
            return false;

        _currentIndex++;
        return true;
    }

    /// <summary>
    /// Resets the iterator to start a fresh lap in the same fixed order.
    /// </summary>
    public override void Reset()
    {
        _currentIndex = -1;
    }
}
