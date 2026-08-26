using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SimpleL7Proxy.Backend.Iterators;

/// <summary>
/// Orders and traverses backend hosts for one request. The configured load-balancing mode
/// determines the order for each lap; <see cref="BaseIterator"/> controls pass and attempt limits.
/// </summary>
public sealed class PerRequestHostIterator : BaseIterator
{
    private enum HostOrder
    {
        Fixed,
        RoundRobin,
        Latency,
        TimeToFirstByte,
        PriorityGroup,
        Random
    }

    private static long _roundRobinCounter;

    private readonly HostOrder _hostOrder;
    private readonly List<BaseHostHealth> _hosts;
    private int _currentIndex;
    private int _hostsVisitedInCurrentLap;

    /// <summary>
    /// Creates an iterator over a snapshot of the supplied hosts using the requested ordering mode.
    /// A null mode preserves the supplied order.
    /// </summary>
    public PerRequestHostIterator(List<BaseHostHealth> hosts, string? loadBalanceMode)
    {
        ArgumentNullException.ThrowIfNull(hosts);

        _hostOrder = ResolveHostOrder(loadBalanceMode);
        _hosts = CreateOrderedSnapshot(hosts, _hostOrder);
        _currentIndex = -1;
    }

    /// <inheritdoc/>
    public override int HostCount => _hosts.Count;

    protected override IReadOnlyList<BaseHostHealth> Hosts => _hosts;

    /// <summary>Gets the host at the iterator's current position.</summary>
    public BaseHostHealth Current
    {
        get
        {
            if (_currentIndex < 0 || _currentIndex >= _hosts.Count)
                throw new InvalidOperationException("Iterator is not positioned at a valid element.");

            return _hosts[_currentIndex];
        }
    }

    /// <summary>Moves to the next host in the current lap.</summary>
    public bool MoveNext()
    {
        if (_hosts.Count == 0)
            return false;

        if (_hostOrder == HostOrder.RoundRobin)
        {
            if (_hostsVisitedInCurrentLap >= _hosts.Count)
                return false;

            var counter = Interlocked.Increment(ref _roundRobinCounter);
            _currentIndex = (int)((ulong)counter % (ulong)_hosts.Count);
            _hostsVisitedInCurrentLap++;
            return true;
        }

        _currentIndex++;
        return _currentIndex < _hosts.Count;
    }

    /// <summary>Starts a new lap and reshuffles randomized ordering.</summary>
    public void Reset()
    {
        _currentIndex = -1;
        _hostsVisitedInCurrentLap = 0;

        if (_hostOrder == HostOrder.Random)
            Shuffle(_hosts);
    }

    internal static List<BaseHostHealth> CreateOrderedSnapshot(
        List<BaseHostHealth> hosts,
        string? loadBalanceMode)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        return CreateOrderedSnapshot(hosts, ResolveHostOrder(loadBalanceMode));
    }

    protected override bool FetchCandidate(IterationState state, out BaseHostHealth? host)
    {
        while (true)
        {
            if (MoveNext())
            {
                host = Current;
                return true;
            }

            if (_hosts.Count == 0 ||
                state.Mode != IterationModeEnum.MultiPass ||
                state.TotalAttempts >= EffectiveMaxAttempts(state))
            {
                host = null;
                return false;
            }

            Reset();
        }
    }

    private static HostOrder ResolveHostOrder(string? loadBalanceMode) =>
        loadBalanceMode switch
        {
            null => HostOrder.Fixed,
            Constants.RoundRobin => HostOrder.RoundRobin,
            Constants.Latency => HostOrder.Latency,
            Constants.TimeToFirstByte => HostOrder.TimeToFirstByte,
            Constants.PriorityGroup => HostOrder.PriorityGroup,
            Constants.Random => HostOrder.Random,
            _ => HostOrder.Random
        };

    private static List<BaseHostHealth> CreateOrderedSnapshot(
        List<BaseHostHealth> hosts,
        HostOrder hostOrder)
    {
        var orderedHosts = hostOrder switch
        {
            HostOrder.Latency => hosts.OrderBy(host => host.AverageLatency()).ToList(),
            HostOrder.TimeToFirstByte => hosts.OrderBy(host => host.TimeToFirstByteMs).ToList(),
            HostOrder.PriorityGroup => hosts
                .OrderBy(host => host.Config.PriorityGroup)
                .ToList(),
            _ => new List<BaseHostHealth>(hosts)
        };

        if (hostOrder == HostOrder.Random)
            Shuffle(orderedHosts);

        return orderedHosts;
    }

    private static void Shuffle<T>(List<T> items)
    {
        for (var index = items.Count - 1; index > 0; index--)
        {
            var swapIndex = Random.Shared.Next(index + 1);
            (items[index], items[swapIndex]) = (items[swapIndex], items[index]);
        }
    }
}
