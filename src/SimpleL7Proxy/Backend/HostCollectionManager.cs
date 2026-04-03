using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SimpleL7Proxy.Backend.Iterators;
using SimpleL7Proxy.Config;

namespace SimpleL7Proxy.Backend;

/// <summary>
/// Singleton manager that owns the authoritative host list.
/// Reads are lock-free (volatile snapshot reference).
/// Writes build a new snapshot and atomically swap via volatile reference.
/// Old snapshots remain valid for any in-flight workers holding a reference.
///
/// Startup flow:
///   1. Constructor starts with Empty snapshot
///   2. StageHost() adds individual hosts to a pending list
///   3. Activate() builds, freezes, and swaps the pending snapshot in as Current
///   After activation, CRUD operations modify Current directly.
/// </summary>
public sealed class HostCollectionManager : IHostHealthCollection
{
  private volatile HostCollectionSnapshot _current;
  private HostCollectionSnapshot? _pending;
  private List<HostConfig>? _stagedConfigs;
  private int _version;
  private readonly ILogger<HostCollectionManager> _logger;

  /// <inheritdoc />
  public HostCollectionSnapshot Current => _current;

  public HostCollectionManager(ILogger<HostCollectionManager> logger)
  {
    ArgumentNullException.ThrowIfNull(logger, nameof(logger));

    _logger = logger;
    _version = 0;

    // Start empty — hosts are staged via StageHost() then Activate()
    _current = HostCollectionSnapshot.Empty;
    _logger.LogDebug("[HOSTMGR] Initialized with empty snapshot");
  }

  /// <inheritdoc />
  public void StageHost(HostConfig config)
  {
    ArgumentNullException.ThrowIfNull(config, nameof(config));

    config.FreezeHash();
    _stagedConfigs ??= [];
    _stagedConfigs.Add(config);
    _logger.LogDebug("[HOSTMGR] Staged host: {Host} ({Count} staged)",
        config.Host, _stagedConfigs.Count);
  }

  /// <summary>
  /// Builds a pending snapshot from the configured host list.
  /// Does NOT activate it — call Activate() to make it Current.
  /// </summary>
  public void LoadFromConfig(IEnumerable<HostConfig> hostConfigs)
  {
    ArgumentNullException.ThrowIfNull(hostConfigs, nameof(hostConfigs));

    _version++;
    _pending = HostCollectionSnapshot.Build(hostConfigs, _logger, _version);
    _logger.LogInformation("[HOSTMGR] Pending snapshot built (v{Version}, {Count} hosts)",
        _version, _pending.Hosts.Count);
  }

  /// <summary>
  /// Atomically swaps the pending snapshot in as Current.
  /// If hosts were staged via <see cref="StageHost"/>, deduplicates them by
  /// <see cref="HostConfig.FrozenHash"/>, activates only unique configs
  /// (creating circuit breakers), and builds the snapshot.
  /// If <see cref="LoadFromConfig"/> was used, uses the pre-built pending snapshot.
  /// Freezes the snapshot before activation.
  /// </summary>
  public void Activate()
  {
    // Build from staged configs if present
    if (_stagedConfigs != null && _stagedConfigs.Count > 0)
    {
      var uniqueConfigs = DeduplicateStagedConfigs(_stagedConfigs);
      _stagedConfigs = null;

      // Compare staged hashes against current snapshot — skip rebuild if identical
      if (MatchesCurrentSnapshot(uniqueConfigs))
      {
        _logger.LogDebug("[HOSTMGR] Staged hosts match current snapshot — no changes, skipping activation");
        return;
      }

      // Activate circuit breakers only for configs that survived dedup
      foreach (var config in uniqueConfigs)
      {
        config.Activate();
      }

      _version++;
      _pending = HostCollectionSnapshot.Build(uniqueConfigs, _logger, _version);
    }

    if (_pending == null)
    {
      _logger.LogWarning("[HOSTMGR] Activate() called with no pending snapshot or staged hosts");
      return;
    }

    _pending.Freeze();

    // Mark hosts from the old snapshot that are not in the new one for spin-down
    var oldSnapshot = _current;
    SpinDownRetiredHosts(oldSnapshot, _pending);

    var oldVersion = _current.Version;
    _current = _pending;
    _pending = null;

    _logger.LogInformation("[HOSTMGR] ✓ Snapshot activated (v{OldVersion} → v{NewVersion}, {Count} hosts)",
        oldVersion, _current.Version, _current.Hosts.Count);

    IteratorFactory.InvalidateCache();
  }

  /// <summary>
  /// Removes duplicate staged configs based on <see cref="HostConfig.FrozenHash"/>.
  /// Keeps the first occurrence of each unique configuration.
  /// Configs without a frozen hash are always kept (treated as unique).
  /// </summary>
  private List<HostConfig> DeduplicateStagedConfigs(List<HostConfig> configs)
  {
    var seen = new HashSet<string>(StringComparer.Ordinal);
    var unique = new List<HostConfig>(configs.Count);
    var dupCount = 0;

    foreach (var config in configs)
    {
      var hash = config.FrozenHash;
      if (hash == null || seen.Add(hash))
      {
        unique.Add(config);
      }
      else
      {
        dupCount++;
        _logger.LogWarning("[HOSTMGR] Duplicate host config skipped: {Host} (hash {Hash})",
            config.Host, hash);
      }
    }

    if (dupCount > 0)
    {
      _logger.LogInformation("[HOSTMGR] Dedup: {Original} staged → {Unique} unique ({Dups} duplicates removed)",
          configs.Count, unique.Count, dupCount);
    }

    return unique;
  }

  /// <summary>
  /// Returns <c>true</c> when the staged configs have the exact same set of
  /// frozen hashes as the current active snapshot (same count, same hashes).
  /// </summary>
  private bool MatchesCurrentSnapshot(List<HostConfig> stagedConfigs)
  {
    var currentHosts = _current.Hosts;
    if (stagedConfigs.Count != currentHosts.Count)
      return false;

    // Build a bag of current hashes (multiset comparison — order doesn't matter)
    var currentHashes = new Dictionary<string, int>(currentHosts.Count, StringComparer.Ordinal);
    foreach (var host in currentHosts)
    {
      var hash = host.Config.FrozenHash;
      if (hash == null) return false; // unhashed → can't compare, treat as changed
      currentHashes[hash] = currentHashes.GetValueOrDefault(hash) + 1;
    }

    foreach (var config in stagedConfigs)
    {
      var hash = config.FrozenHash;
      if (hash == null) return false;
      if (!currentHashes.TryGetValue(hash, out var count) || count == 0)
        return false;
      currentHashes[hash] = count - 1;
    }

    return true;
  }

  /// <summary>
  /// Marks hosts in the old snapshot that are absent from the new snapshot
  /// for graceful spin-down via <see cref="HostConfig.SpinDown"/>.
  /// Comparison uses <see cref="HostConfig.FrozenHash"/> so only config
  /// changes are detected — identity (Guid) differences are ignored.
  /// </summary>
  private void SpinDownRetiredHosts(HostCollectionSnapshot oldSnapshot, HostCollectionSnapshot newSnapshot)
  {
    if (oldSnapshot.Hosts.Count == 0) return;

    // Build the set of hashes in the new snapshot
    var newHashes = new HashSet<string>(newSnapshot.Hosts.Count, StringComparer.Ordinal);
    foreach (var host in newSnapshot.Hosts)
    {
      var hash = host.Config.FrozenHash;
      if (hash != null) newHashes.Add(hash);
    }

    foreach (var oldHost in oldSnapshot.Hosts)
    {
      var hash = oldHost.Config.FrozenHash;
      if (hash != null && newHashes.Contains(hash))
        continue;

      oldHost.Config.SpinDown();
      _logger.LogInformation("[HOSTMGR] Host spinning down: {Host} (removed from active snapshot)",
          oldHost.Config.Host);
    }
  }

  /// <inheritdoc />
  public BaseHostHealth AddHost(HostConfig config)
  {
    ArgumentNullException.ThrowIfNull(config, nameof(config));

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
    _current = HostCollectionSnapshot.BuildFromHosts(newHosts, _version, _logger);

    _logger.LogInformation("[CRUD] ✓ Host added: {Host} (v{Version}, total: {Count})",
        config.Host, _version, _current.Hosts.Count);

    IteratorFactory.InvalidateCache();
    return host;
  }

  /// <inheritdoc />
  public bool RemoveHost(Guid hostId)
  {
    var existing = _current.Hosts.FirstOrDefault(h => h.guid == hostId);
    if (existing == null)
    {
      _logger.LogWarning("[CRUD] Host not found for removal: {HostId}", hostId);
      return false;
    }

    var newHosts = _current.Hosts.Where(h => h.guid != hostId).ToList();
    _version++;
    _current = HostCollectionSnapshot.BuildFromHosts(newHosts, _version, _logger);

    _logger.LogInformation("[CRUD] ✓ Host removed: {Host} (v{Version}, total: {Count})",
        existing.Host, _version, _current.Hosts.Count);

    IteratorFactory.InvalidateCache();
    return true;
  }

  /// <inheritdoc />
  public bool UpdateHost(Guid hostId, Action<HostConfig> mutate)
  {
    ArgumentNullException.ThrowIfNull(mutate, nameof(mutate));

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
        new List<BaseHostHealth>(_current.Hosts), _version, _logger);

    _logger.LogInformation("[CRUD] ✓ Host updated: {Host} (v{Version})",
        existing.Host, _version);

    IteratorFactory.InvalidateCache();
    return true;
  }
}
