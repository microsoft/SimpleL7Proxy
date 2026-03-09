namespace SimpleL7Proxy.Backend;

/// <summary>
/// Manages the authoritative collection of backend hosts.
/// Exposes an immutable snapshot for lock-free reads and thread-safe CRUD for mutations.
/// </summary>
public interface IHostHealthCollection
{
  /// <summary>
  /// Current immutable snapshot. Grab once per operation — no locking needed.
  /// Old snapshots stay alive until in-flight workers finish, then GC reclaims them.
  /// </summary>
  HostCollectionSnapshot Current { get; }

  /// <summary>
  /// Stages a single <see cref="HostConfig"/> into the pending list.
  /// Does NOT activate it — call <see cref="Activate"/> after all hosts are staged.
  /// </summary>
  void StageHost(HostConfig config);

  /// <summary>
  /// Builds a pending snapshot from a list of HostConfigs.
  /// Does NOT activate it — call Activate() to swap it in.
  /// </summary>
  void LoadFromConfig(IEnumerable<HostConfig> hostConfigs);

  /// <summary>
  /// Atomically swaps the pending snapshot in as Current.
  /// </summary>
  void Activate();

  /// <summary>
  /// Creates a BaseHostHealth from a HostConfig, adds it to the collection,
  /// rebuilds the snapshot, and invalidates iterator caches.
  /// Returns the created host.
  /// </summary>
  BaseHostHealth AddHost(HostConfig config);

  /// <summary>
  /// Removes a host by its unique GUID.
  /// Returns true if found and removed.
  /// </summary>
  bool RemoveHost(Guid hostId);

  /// <summary>
  /// Applies a mutation to an existing host's config, then re-categorizes.
  /// Returns true if found and updated.
  /// </summary>
  bool UpdateHost(Guid hostId, Action<HostConfig> mutate);
}
