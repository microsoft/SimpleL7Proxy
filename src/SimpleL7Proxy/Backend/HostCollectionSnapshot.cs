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

  private HostCollectionSnapshot(
      List<BaseHostHealth> hosts,
      List<BaseHostHealth> specificPathHosts,
      List<BaseHostHealth> catchAllHosts,
      int version)
  {
    Hosts = hosts;
    SpecificPathHosts = specificPathHosts;
    CatchAllHosts = catchAllHosts;
    Version = version;
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
  /// </summary>
  public void Freeze()
  {
    if (IsFrozen) return;

    HostsByGuid = Hosts.ToFrozenDictionary(h => h.guid, h => h.Config);
    HostsByUrl = Hosts.ToFrozenDictionary(h => h.Host, h => h.Config, StringComparer.OrdinalIgnoreCase);
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

    logger.LogCritical("[CONFIG] Host categorization complete: {SpecificCount} specific hosts, {CatchAllCount} catch-all hosts",
        specificPathHosts.Count, catchAllHosts.Count);

    return new HostCollectionSnapshot(hosts, specificPathHosts, catchAllHosts, version);
  }

  /// <summary>
  /// Builds a new snapshot from existing BaseHostHealth instances (used by CRUD to re-categorize).
  /// </summary>
  public static HostCollectionSnapshot BuildFromHosts(
      List<BaseHostHealth> hosts,
      int version)
  {
    var specificPathHosts = new List<BaseHostHealth>();
    var catchAllHosts = new List<BaseHostHealth>();

    foreach (var host in hosts)
    {
      CategorizeHost(host, specificPathHosts, catchAllHosts);
    }

    return new HostCollectionSnapshot(hosts, specificPathHosts, catchAllHosts, version);
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