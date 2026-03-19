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
  /// <summary>Every registered host (specific-path + catch-all).</summary>
  public List<BaseHostHealth> Hosts { get; }

  /// <summary>Hosts whose PartialPath targets a specific route prefix.</summary>
  public List<BaseHostHealth> SpecificPathHosts { get; }

  /// <summary>Hosts that match any request path (/, /*, or empty).</summary>
  public List<BaseHostHealth> CatchAllHosts { get; }

  /// <summary>Monotonically increasing version for diagnostics / cache invalidation.</summary>
  public int Version { get; }

  /// <summary>Frozen lookup of all hosts by their Guid. Populated by <see cref="Freeze"/>.</summary>
  public FrozenDictionary<Guid, HostConfig>? HostsByGuid { get; private set; }

  /// <summary>Frozen lookup of all hosts by their Host URL (e.g. "https://foo.openai.azure.com"). Populated by <see cref="Freeze"/>.</summary>
  public FrozenDictionary<string, HostConfig>? HostsByUrl { get; private set; }

  /// <summary>Whether <see cref="Freeze"/> has been called.</summary>
  public bool IsFrozen { get; private set; }

  private readonly ILogger? _logger;

  private HostCollectionSnapshot(
      List<BaseHostHealth> hosts,
      List<BaseHostHealth> specificPathHosts,
      List<BaseHostHealth> catchAllHosts,
      int version,
      ILogger? logger = null)
  {
    Hosts = hosts;
    SpecificPathHosts = specificPathHosts;
    CatchAllHosts = catchAllHosts;
    Version = version;
    _logger = logger;
  }

  /// <summary>Empty snapshot for startup / error states.</summary>
  public static HostCollectionSnapshot Empty { get; } = CreateEmpty();

  private static HostCollectionSnapshot CreateEmpty()
  {
    var empty = new HostCollectionSnapshot([], [], [], 0);
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
            "[CONFIG] Duplicate host Guid {Guid} found across hosts: [{Hosts}]. Only the first occurrence will be used.",
            group.Key, duplicateHosts);
      }
    }

    // Deduplicate by Host URL — log any duplicates
    var urlGroups = Hosts.GroupBy(h => h.Host, StringComparer.OrdinalIgnoreCase).ToList();
    foreach (var group in urlGroups) {
      if (group.Count() > 1) {
        var duplicateGuids = string.Join(", ", group.Select(h => h.guid));
        _logger?.LogWarning(
            "[CONFIG] Duplicate host URL '{Url}' found {Count} times (Guids: [{Guids}]). Only the first occurrence will be used.",
            group.Key, group.Count(), duplicateGuids);
      }
    }

    HostsByGuid = guidGroups
        .ToFrozenDictionary(g => g.Key, g => g.First().Config);
    HostsByUrl = urlGroups
        .ToFrozenDictionary(g => g.Key, g => g.First().Config, StringComparer.OrdinalIgnoreCase);
    IsFrozen = true;
  }

  /// <summary>
  /// Builds a new snapshot from a list of HostConfigs, categorizing each host.
  /// </summary>
  public static HostCollectionSnapshot Build(
      IEnumerable<HostConfig> hostConfigs,
      ILogger logger,
      int version = 1)
  {
    var hosts = new List<BaseHostHealth>();
    var specificPathHosts = new List<BaseHostHealth>();
    var catchAllHosts = new List<BaseHostHealth>();

    foreach (var hostConfig in hostConfigs)
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
      CategorizeHost(host, specificPathHosts, catchAllHosts);
    }

    logger.LogInformation("[HOST-MANAGER] Categorized: {SpecificCount} specific-path, {CatchAllCount} catch-all",
        specificPathHosts.Count, catchAllHosts.Count);

    return new HostCollectionSnapshot(hosts, specificPathHosts, catchAllHosts, version, logger);
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

    return new HostCollectionSnapshot(hosts, specificPathHosts, catchAllHosts, version, logger);
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