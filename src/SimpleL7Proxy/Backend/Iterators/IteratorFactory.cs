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

    /// <summary>
    /// Path-matches hosts for one request, then filters by request priority.
    /// Specific-path hosts win over catch-all. Does not apply priority-group ordering.
    /// </summary>
    internal static List<BaseHostHealth> ResolveHosts(
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

        if (requestPriority == Constants.AnyPriority)
            return filteredHosts;

        var acceptedHosts = new List<BaseHostHealth>(filteredHosts.Count);
        for (var index = 0; index < filteredHosts.Count; index++)
        {
            var host = filteredHosts[index];
            if (host.Config.AcceptsPriority(requestPriority))
                acceptedHosts.Add(host);
        }

        return acceptedHosts;
    }

    private static (List<BaseHostHealth> Hosts, string ModifiedPath) FilterHostsByPath(
        List<BaseHostHealth> specificHosts,
        List<BaseHostHealth> catchAllHosts,
        string requestPath)
    {
        List<BaseHostHealth>? matchedHosts = null;
        string? strippedPath = null;

        for (var index = 0; index < specificHosts.Count; index++)
        {
            var host = specificHosts[index];
            if (host.Config.IsSpinningDown)
                continue;

            var match = host.Config.SupportsPath(requestPath);
            if (!match.IsMatch)
                continue;

            matchedHosts ??= new List<BaseHostHealth>();
            strippedPath ??= match.StrippedPath;
            matchedHosts.Add(host);
        }

        if (matchedHosts != null)
            return (matchedHosts, strippedPath!);

        var catchAllMatchedHosts = new List<BaseHostHealth>(catchAllHosts.Count);
        for (var index = 0; index < catchAllHosts.Count; index++)
        {
            var host = catchAllHosts[index];
            if (!host.Config.IsSpinningDown)
                catchAllMatchedHosts.Add(host);
        }

        return (catchAllMatchedHosts, requestPath);
    }
}
