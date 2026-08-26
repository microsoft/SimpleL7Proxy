namespace SimpleL7Proxy.Backend.Iterators;

/// <summary>
/// Resolves matching backend hosts and creates request-scoped iterator instances.
/// </summary>
public static class IteratorFactory
{
    /// <summary>
    /// Creates an iterator for all priorities after resolving hosts for the request path.
    /// </summary>
    public static IHostIterator CreateSinglePassIterator(
        IEndpointMonitorService backendService,
        string loadBalanceMode,
        string requestPath,
        out string modifiedPath)
    {
        return CreateSinglePassIterator(
            backendService,
            loadBalanceMode,
            requestPath,
            Constants.AnyPriority,
            out modifiedPath);
    }

    /// <summary>
    /// Creates an iterator after resolving hosts for the request path and priority.
    /// </summary>
    public static IHostIterator CreateSinglePassIterator(
        IEndpointMonitorService backendService,
        string loadBalanceMode,
        string requestPath,
        int requestPriority,
        out string modifiedPath)
    {
        var hosts = ResolveHosts(
            backendService,
            requestPath,
            requestPriority,
            out modifiedPath);

        return new PerRequestHostIterator(hosts, loadBalanceMode);
    }

    /// <summary>Creates an iterator that preserves the supplied host order.</summary>
    public static IHostIterator CreateFixedOrderIterator(List<BaseHostHealth> hosts)
    {
        return new PerRequestHostIterator(hosts, loadBalanceMode: null);
    }

    internal static (List<BaseHostHealth> Hosts, string ModifiedPath) CreateSharedHostSnapshot(
        IEndpointMonitorService backendService,
        string loadBalanceMode,
        string requestPath,
        int requestPriority)
    {
        var hosts = ResolveHosts(
            backendService,
            requestPath,
            requestPriority,
            out var modifiedPath);

        return (
            PerRequestHostIterator.CreateOrderedSnapshot(hosts, loadBalanceMode),
            modifiedPath);
    }

    private static List<BaseHostHealth> ResolveHosts(
        IEndpointMonitorService backendService,
        string requestPath,
        int requestPriority,
        out string modifiedPath)
    {
        var specificHosts = backendService.GetSpecificPathHosts();
        var catchAllHosts = backendService.GetCatchAllHosts();

        if (specificHosts.Count == 0 && catchAllHosts.Count == 0)
        {
            modifiedPath = requestPath;
            return [];
        }

        var (filteredHosts, path) = FilterHostsByPath(
            specificHosts,
            catchAllHosts,
            requestPath);
        modifiedPath = path;

        if (requestPriority != Constants.AnyPriority)
        {
            filteredHosts = filteredHosts
                .Where(host => host.Config.AcceptsPriority(requestPriority))
                .ToList();
        }

        return filteredHosts;
    }

    private static (List<BaseHostHealth> Hosts, string ModifiedPath) FilterHostsByPath(
        List<BaseHostHealth> specificHosts,
        List<BaseHostHealth> catchAllHosts,
        string requestPath)
    {
        var matchedHosts = specificHosts
            .Where(host => !host.Config.IsSpinningDown)
            .Select(host => (Host: host, Match: host.Config.SupportsPath(requestPath)))
            .Where(candidate => candidate.Match.IsMatch)
            .ToList();

        if (matchedHosts.Count > 0)
        {
            return (
                matchedHosts.Select(candidate => candidate.Host).ToList(),
                matchedHosts[0].Match.StrippedPath);
        }

        return (
            catchAllHosts.Where(host => !host.Config.IsSpinningDown).ToList(),
            requestPath);
    }
}
