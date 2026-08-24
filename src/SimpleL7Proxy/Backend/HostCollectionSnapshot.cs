using System.Collections.Frozen;
using Microsoft.Extensions.Logging;

namespace SimpleL7Proxy.Backend;

/// <summary>
/// An immutable snapshot of all backend hosts, pre-categorized into specific-path and catch-all.
/// Once built, the lists are never mutated — readers grab a reference and iterate safely.
/// Old snapshots are kept alive by in-flight workers; GC reclaims them naturally.
/// </summary>
public sealed class HostCollectionSnapshot
{
  /// <summary>Every configured host, including logical hosts routed through APIM.</summary>
  public IReadOnlyList<HostConfig> Configs { get; }

  /// <summary>Every registered host (specific-path + catch-all).</summary>
  public List<BaseHostHealth> Hosts { get; }

  /// <summary>Hosts whose PartialPath targets a specific route prefix.</summary>
  public List<BaseHostHealth> SpecificPathHosts { get; }

  /// <summary>Hosts that match any request path (/, /*, or empty).</summary>
  public List<BaseHostHealth> CatchAllHosts { get; }

  /// <summary>Named path routes ordered by longest prefix first.</summary>
  public IReadOnlyList<PathRoute> PathRoutes { get; }

  /// <summary>Unresolved route definitions retained for configuration comparisons and CRUD rebuilds.</summary>
  public IReadOnlyList<PathRouteDefinition> RouteDefinitions { get; }

  /// <summary>Monotonically increasing version for diagnostics / cache invalidation.</summary>
  public int Version { get; }

  /// <summary>Frozen lookup of all hosts by their Guid. Populated by <see cref="Freeze"/>.</summary>
  public FrozenDictionary<Guid, HostConfig>? HostsByGuid { get; private set; }

  /// <summary>Frozen lookup of all hosts by their Host URL (e.g. "https://foo.openai.azure.com"). Populated by <see cref="Freeze"/>.</summary>
  public FrozenDictionary<string, HostConfig>? HostsByUrl { get; private set; }

  /// <summary>Frozen lookup of all named host configurations, including logical APIM backends.</summary>
  public FrozenDictionary<string, HostConfig>? ConfigsByKey { get; private set; }

  /// <summary>Whether <see cref="Freeze"/> has been called.</summary>
  public bool IsFrozen { get; private set; }

  private readonly ILogger? _logger;

  private HostCollectionSnapshot(
      IReadOnlyList<HostConfig> configs,
      List<BaseHostHealth> hosts,
      List<BaseHostHealth> specificPathHosts,
      List<BaseHostHealth> catchAllHosts,
      IReadOnlyList<PathRoute> pathRoutes,
      IReadOnlyList<PathRouteDefinition> routeDefinitions,
      int version,
      ILogger? logger = null)
  {
    Configs = configs;
    Hosts = hosts;
    SpecificPathHosts = specificPathHosts;
    CatchAllHosts = catchAllHosts;
    PathRoutes = pathRoutes;
    RouteDefinitions = routeDefinitions;
    Version = version;
    _logger = logger;
  }

  /// <summary>Empty snapshot for startup / error states.</summary>
  public static HostCollectionSnapshot Empty { get; } = CreateEmpty();

  private static HostCollectionSnapshot CreateEmpty()
  {
    var empty = new HostCollectionSnapshot([], [], [], [], [], [], 0);
    empty.Freeze();
    return empty;
  }

  /// <summary>
  /// Freezes the snapshot by building <see cref="FrozenDictionary{TKey, TValue}"/>
  /// lookups for all <see cref="HostConfig"/> instances contained in this snapshot.
  /// After this call, <see cref="IsFrozen"/> is <c>true</c> and the dictionaries are available.
  /// Calling Freeze more than once is a no-op.
  /// Duplicate keys are detected, logged, and only the first occurrence is kept.
  /// </summary>
  public void Freeze()
  {
    if (IsFrozen) return;

    // Deduplicate by Guid — log any duplicates
    var guidGroups = Hosts.GroupBy(h => h.guid).ToList();
    foreach (var group in guidGroups) {
      if (group.Count() > 1) {
        var duplicateHosts = string.Join(", ", group.Select(h => h.Host));
        _logger?.LogWarning(
            "[CONFIGS] Duplicate host Guid {Guid} found across hosts: [{Hosts}]. Only the first occurrence will be used.",
            group.Key, duplicateHosts);
      }
    }

    // Deduplicate by Host URL — log any duplicates
    var urlGroups = Hosts.GroupBy(h => h.Host, StringComparer.OrdinalIgnoreCase).ToList();
    foreach (var group in urlGroups) {
      if (group.Count() > 1) {
        var duplicateGuids = string.Join(", ", group.Select(h => h.guid));
        _logger?.LogWarning(
            "[CONFIGS] Duplicate host URL '{Url}' found {Count} times (Guids: [{Guids}]). Only the first occurrence will be used.",
            group.Key, group.Count(), duplicateGuids);
      }
    }

    HostsByGuid = guidGroups
        .ToFrozenDictionary(g => g.Key, g => g.First().Config);
    HostsByUrl = urlGroups
        .ToFrozenDictionary(g => g.Key, g => g.First().Config, StringComparer.OrdinalIgnoreCase);
    ConfigsByKey = Configs
        .Where(config => !string.IsNullOrWhiteSpace(config.ConfigKey))
        .ToFrozenDictionary(config => config.ConfigKey, StringComparer.OrdinalIgnoreCase);
    IsFrozen = true;
  }

  /// <summary>Returns the longest named route matching the request path.</summary>
  public PathRouteMatch? MatchRoute(string requestPath)
  {
    foreach (var route in PathRoutes)
    {
      var result = route.Match(requestPath);
      if (result.IsMatch)
        return new PathRouteMatch(route, result.StrippedPath);
    }

    return null;
  }

  /// <summary>
  /// Builds a new snapshot from a list of HostConfigs, categorizing each host.
  /// </summary>
  public static HostCollectionSnapshot Build(
      IEnumerable<HostConfig> hostConfigs,
      ILogger logger,
      int version = 1)
  {
    return Build(hostConfigs, [], logger, version);
  }

  /// <summary>
  /// Builds a snapshot and resolves named route and gateway references against the same host set.
  /// </summary>
  public static HostCollectionSnapshot Build(
      IEnumerable<HostConfig> hostConfigs,
      IEnumerable<PathRouteDefinition> routeDefinitions,
      ILogger logger,
      int version = 1)
  {
    var configs = hostConfigs.ToList();
    var definitions = routeDefinitions.ToList();
    var hosts = new List<BaseHostHealth>();
    var specificPathHosts = new List<BaseHostHealth>();
    var catchAllHosts = new List<BaseHostHealth>();
    var hostsByKey = new Dictionary<string, BaseHostHealth>(StringComparer.OrdinalIgnoreCase);

    var duplicateConfigKey = configs
        .Where(config => !string.IsNullOrWhiteSpace(config.ConfigKey))
        .GroupBy(config => config.ConfigKey, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault(group => group.Count() > 1);
    if (duplicateConfigKey != null)
      throw new InvalidOperationException($"Duplicate backend host key '{duplicateConfigKey.Key}'.");

    var configsByKey = configs
        .Where(config => !string.IsNullOrWhiteSpace(config.ConfigKey))
        .ToDictionary(config => config.ConfigKey, StringComparer.OrdinalIgnoreCase);

    foreach (var config in configs.Where(config => config.IndirectMode))
    {
      if (string.IsNullOrWhiteSpace(config.ConfigKey))
        throw new InvalidOperationException($"Backend '{config.Host}' uses via but has no configuration key.");
      if (!configsByKey.TryGetValue(config.Via, out var gatewayConfig))
        throw new InvalidOperationException($"Backend '{config.ConfigKey}' references missing gateway '{config.Via}'.");
      if (string.Equals(config.ConfigKey, gatewayConfig.ConfigKey, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"Backend '{config.ConfigKey}' cannot route via itself.");
      if (gatewayConfig.Mode != HostModeEnum.Apim)
        throw new InvalidOperationException(
            $"Gateway '{gatewayConfig.ConfigKey}' must use mode=apim.");
    }

    foreach (var hostConfig in configs.Where(config => !config.IndirectMode))
    {
      BaseHostHealth host;

      // Determine if host supports probing based on DirectMode or ProbePath
      if (hostConfig.DirectMode || string.IsNullOrEmpty(hostConfig.ProbePath) || hostConfig.ProbePath == "/")
      {
        host = new NonProbeableHostHealth(hostConfig, logger);
      }
      else
      {
        host = new ProbeableHostHealth(hostConfig, logger);
      }

      hosts.Add(host);
      if (!string.IsNullOrWhiteSpace(hostConfig.ConfigKey))
        hostsByKey.Add(hostConfig.ConfigKey, host);
    }

    var duplicatePrefix = definitions
        .GroupBy(definition => definition.Prefix, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault(group => group.Count() > 1);
    if (duplicatePrefix != null)
      throw new InvalidOperationException($"Duplicate path route prefix '{duplicatePrefix.Key}'.");

    var routes = new List<PathRoute>(definitions.Count);
    var routeOwnedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var definition in definitions)
    {
      var configuredHosts = new List<HostConfig>(definition.HostKeys.Count);
      foreach (var hostKey in definition.HostKeys)
      {
        if (!configsByKey.TryGetValue(hostKey, out var config))
          throw new InvalidOperationException($"Path route '{definition.Name}' references missing host '{hostKey}'.");
        configuredHosts.Add(config);
        routeOwnedKeys.Add(hostKey);
      }

      var viaValues = configuredHosts
          .Select(config => config.Via)
          .Distinct(StringComparer.OrdinalIgnoreCase)
          .ToList();
      if (viaValues.Count > 1)
        throw new InvalidOperationException($"Path route '{definition.Name}' cannot mix direct hosts or multiple via gateways.");

      BaseHostHealth? gatewayHost = null;
      var directHosts = new List<BaseHostHealth>();
      var via = viaValues[0];
      if (string.IsNullOrWhiteSpace(via))
      {
        foreach (var config in configuredHosts)
          directHosts.Add(hostsByKey[config.ConfigKey]);
      }
      else
      {
        gatewayHost = hostsByKey[via];
        routeOwnedKeys.Add(via);
      }

      routes.Add(new PathRoute(definition, configuredHosts, directHosts, gatewayHost));
    }

      var orphanedViaConfig = configs.FirstOrDefault(config =>
        config.IndirectMode && !routeOwnedKeys.Contains(config.ConfigKey));
      if (orphanedViaConfig != null)
        throw new InvalidOperationException(
          $"Backend '{orphanedViaConfig.ConfigKey}' uses via but is not referenced by a Path_* route.");

    foreach (var host in hosts)
    {
      if (!string.IsNullOrWhiteSpace(host.Config.ConfigKey) && routeOwnedKeys.Contains(host.Config.ConfigKey))
        continue;
      CategorizeHost(host, specificPathHosts, catchAllHosts);
    }

    routes.Sort((left, right) => right.Prefix.Length.CompareTo(left.Prefix.Length));

    logger.LogInformation("[HOSTMGR] Categorized: {RouteCount} named routes, {SpecificCount} legacy specific-path, {CatchAllCount} legacy catch-all",
        routes.Count, specificPathHosts.Count, catchAllHosts.Count);

    return new HostCollectionSnapshot(configs, hosts, specificPathHosts, catchAllHosts, routes, definitions, version, logger);
  }

  /// <summary>
  /// Builds a new snapshot from existing BaseHostHealth instances (used by CRUD to re-categorize).
  /// </summary>
  public static HostCollectionSnapshot BuildFromHosts(
      List<BaseHostHealth> hosts,
      int version,
      ILogger? logger = null)
  {
    var specificPathHosts = new List<BaseHostHealth>();
    var catchAllHosts = new List<BaseHostHealth>();

    foreach (var host in hosts)
    {
      CategorizeHost(host, specificPathHosts, catchAllHosts);
    }

    var configs = hosts.Select(host => host.Config).ToList();
    return new HostCollectionSnapshot(configs, hosts, specificPathHosts, catchAllHosts, [], [], version, logger);
  }

  private static void CategorizeHost(
      BaseHostHealth host,
      List<BaseHostHealth> specificPathHosts,
      List<BaseHostHealth> catchAllHosts)
  {
    var hostPartialPath = host.Config.PartialPath?.Trim();

    if (string.IsNullOrEmpty(hostPartialPath) ||
        hostPartialPath == "/" ||
        hostPartialPath == "/*")
    {
      catchAllHosts.Add(host);
    }
    else
    {
      specificPathHosts.Add(host);
    }
  }
}