namespace SimpleL7Proxy.Backend.Iterators;

/// <summary>
/// Traverses priority groups in ascending order and applies the selected load-balancing
/// strategy only within each group.
/// </summary>
public sealed class PriorityGroupHostIterator : HostIterator
{
    private readonly string _loadBalanceMode;
    private int _currentHostIndex;

    public PriorityGroupHostIterator(
        List<BaseHostHealth> hosts,
        string loadBalanceMode)
        : base(hosts)
    {
        _loadBalanceMode = loadBalanceMode;
        OrderHosts();
        _currentHostIndex = -1;
    }

    public override BaseHostHealth Current => _hosts[_currentHostIndex];

    public override bool MoveNext()
    {
        _currentHostIndex++;
        return _currentHostIndex < _hosts.Count;
    }

    /// <summary>
    /// Resets the iterator to start a fresh lap, re-ordering groups/hosts as appropriate.
    /// </summary>
    public override void Reset()
    {
        OrderHosts();
        _currentHostIndex = -1;
    }

    private void OrderHosts()
    {
        var ordered = new List<BaseHostHealth>(_hosts.Count);
        foreach (var group in _hosts.GroupBy(host => host.Config.PriorityGroup).OrderBy(group => group.Key))
        {
            var groupHosts = group.ToList();
            switch (_loadBalanceMode)
            {
                case Constants.Latency:
                    groupHosts = groupHosts.OrderBy(host => host.AverageLatencyMs).ToList();
                    break;
                case Constants.TimeToFirstByte:
                    groupHosts = groupHosts.OrderBy(host => host.TimeToFirstByteMs).ToList();
                    break;
                case Constants.Random:
                    Shuffle(groupHosts);
                    break;
                case Constants.RoundRobin:
                    Rotate(groupHosts);
                    break;
            }
            ordered.AddRange(groupHosts);
        }

        _hosts.Clear();
        _hosts.AddRange(ordered);
    }

    private static void Shuffle(List<BaseHostHealth> hosts)
    {
        for (var index = hosts.Count - 1; index > 0; index--)
        {
            var swapIndex = IteratorFactory.GetRandomIndex(index + 1);
            (hosts[index], hosts[swapIndex]) = (hosts[swapIndex], hosts[index]);
        }
    }

    private static void Rotate(List<BaseHostHealth> hosts)
    {
        if (hosts.Count < 2) return;

        var start = IteratorFactory.GetNextRoundRobinIndex(hosts.Count);
        if (start == 0) return;

        var rotated = hosts.Skip(start).Concat(hosts.Take(start)).ToList();
        hosts.Clear();
        hosts.AddRange(rotated);
    }
}