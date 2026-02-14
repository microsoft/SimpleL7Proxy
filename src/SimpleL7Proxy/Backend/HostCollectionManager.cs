using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SimpleL7Proxy.Backend.Iterators;
using SimpleL7Proxy.Config;

namespace SimpleL7Proxy.Backend;

/// <summary>
/// Singleton manager that owns the authoritative host list.
/// Reads are lock-free (volatile snapshot reference).
/// Writes (CRUD) take a lock, build a new snapshot, and atomically swap.
/// Old snapshots remain valid for any in-flight workers holding a reference.
///
/// Startup flow:
///   1. Constructor starts with Empty snapshot
///   2. LoadFromConfig() builds hosts from BackendOptions into a pending snapshot
///   3. Activate() swaps the pending snapshot in as Current
///   After activation, CRUD operations modify Current directly.
/// </summary>
public sealed class HostCollectionManager : IHostHealthCollection
{
  private readonly object _writeLock = new();
  private volatile HostCollectionSnapshot _current;
  private HostCollectionSnapshot? _pending;
  private int _version;
  private readonly ILogger<HostCollectionManager> _logger;

  /// <inheritdoc />
  public HostCollectionSnapshot Current => _current;

  public HostCollectionManager(ILogger<HostCollectionManager> logger)
  {
    ArgumentNullException.ThrowIfNull(logger, nameof(logger));

    _logger = logger;
    _version = 0;

    // Start empty — hosts are loaded via LoadFromConfig() then Activate()
    _current = HostCollectionSnapshot.Empty;
    _logger.LogDebug("[HOST-MANAGER] Initialized with empty snapshot");
  }

  /// <summary>
  /// Builds a pending snapshot from the configured host list.
  /// Does NOT activate it — call Activate() to make it Current.
  /// </summary>
  public void LoadFromConfig(IEnumerable<HostConfig> hostConfigs)
  {
    ArgumentNullException.ThrowIfNull(hostConfigs, nameof(hostConfigs));

    lock (_writeLock)
    {
      _version++;
      _pending = HostCollectionSnapshot.Build(hostConfigs, _logger, _version);
      _logger.LogInformation("[HOST-MANAGER] Pending snapshot built (v{Version}, {Count} hosts)",
          _version, _pending.Hosts.Count);
    }
  }

  /// <summary>
  /// Atomically swaps the pending snapshot in as Current.
  /// After this, readers see the new hosts immediately.
  /// Old snapshots remain valid for in-flight workers until GC reclaims them.
  /// </summary>
  public void Activate()
  {
    lock (_writeLock)
    {
      if (_pending == null)
      {
        _logger.LogWarning("[HOST-MANAGER] Activate() called with no pending snapshot");
        return;
      }

      var oldVersion = _current.Version;
      _current = _pending;
      _pending = null;

      _logger.LogInformation("[HOST-MANAGER] ✓ Snapshot activated (v{OldVersion} → v{NewVersion}, {Count} hosts)",
          oldVersion, _current.Version, _current.Hosts.Count);

      IteratorFactory.InvalidateCache();
    }
  }

  /// <inheritdoc />
  public BaseHostHealth AddHost(HostConfig config)
  {
    ArgumentNullException.ThrowIfNull(config, nameof(config));

    lock (_writeLock)
    {
      // Create the new host health instance
      BaseHostHealth host;
      if (config.DirectMode || string.IsNullOrEmpty(config.ProbePath) || config.ProbePath == "/")
      {
        host = new NonProbeableHostHealth(config, _logger);
      }
      else
      {
        host = new ProbeableHostHealth(config, _logger);
      }

      // Build new list including the new host
      var newHosts = new List<BaseHostHealth>(_current.Hosts) { host };
      _version++;
      _current = HostCollectionSnapshot.BuildFromHosts(newHosts, _version);

      _logger.LogInformation("[CRUD] ✓ Host added: {Host} (v{Version}, total: {Count})",
          config.Host, _version, _current.Hosts.Count);

      IteratorFactory.InvalidateCache();
      return host;
    }
  }

  /// <inheritdoc />
  public bool RemoveHost(Guid hostId)
  {
    lock (_writeLock)
    {
      var existing = _current.Hosts.FirstOrDefault(h => h.guid == hostId);
      if (existing == null)
      {
        _logger.LogWarning("[CRUD] Host not found for removal: {HostId}", hostId);
        return false;
      }

      var newHosts = _current.Hosts.Where(h => h.guid != hostId).ToList();
      _version++;
      _current = HostCollectionSnapshot.BuildFromHosts(newHosts, _version);

      _logger.LogInformation("[CRUD] ✓ Host removed: {Host} (v{Version}, total: {Count})",
          existing.Host, _version, _current.Hosts.Count);

      IteratorFactory.InvalidateCache();
      return true;
    }
  }

  /// <inheritdoc />
  public bool UpdateHost(Guid hostId, Action<HostConfig> mutate)
  {
    ArgumentNullException.ThrowIfNull(mutate, nameof(mutate));

    lock (_writeLock)
    {
      var existing = _current.Hosts.FirstOrDefault(h => h.guid == hostId);
      if (existing == null)
      {
        _logger.LogWarning("[CRUD] Host not found for update: {HostId}", hostId);
        return false;
      }

      // Apply the mutation
      mutate(existing.Config);

      // Re-categorize (host may have moved between specific-path and catch-all)
      _version++;
      _current = HostCollectionSnapshot.BuildFromHosts(
          new List<BaseHostHealth>(_current.Hosts), _version);

      _logger.LogInformation("[CRUD] ✓ Host updated: {Host} (v{Version})",
          existing.Host, _version);

      IteratorFactory.InvalidateCache();
      return true;
    }
  }
}
