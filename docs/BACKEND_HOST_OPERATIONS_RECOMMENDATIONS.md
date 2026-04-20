# Backend Hosts: Add, Modify, Soft-Delete — Focused Recommendations

## Purpose

This document focuses specifically on **what it takes to add, modify, and soft-delete backend hosts** in SimpleL7Proxy. It maps every place a host is referenced, identifies what must change for each operation, and calls out the considerations and gotchas to address before writing code.

---

## 1. How a Host Lives in the System Today

A single backend host exists as a chain of objects across 6 layers. Understanding this chain is essential because every mutation operation must update **all layers atomically** to avoid inconsistency.

```
Layer 1: HostConfig                   (immutable config parsed from connection string)
Layer 2: BaseHostHealth               (wraps HostConfig + latency/success tracking + circuit breaker)
           ├─ ProbeableHostHealth     (has health probe polling)
           └─ NonProbeableHostHealth  (always-healthy, DirectMode)
Layer 3: HostHealthCollection         (categorizes hosts into SpecificPathHosts vs CatchAllHosts)
Layer 4: Backends._backendHosts       (master list — reference to HostHealthCollection.Hosts)
Layer 5: Backends._activeHosts        (filtered subset — rebuilt every PollInterval)
Layer 6: IteratorFactory caches +     (volatile caches built from _activeHosts, 
         SharedIteratorRegistry        invalidated on host list change)
```

**Each host also has per-host sidecar state:**
- `CircuitBreaker` — created via DI (`ICircuitBreaker`, transient), stored as a private field (`_circuitBreaker`) inside `HostConfig`
- `BackendTokenProvider` audience registration — if `UseOAuth` is set, audience is registered for token refresh
- `BaseHostHealth._latencies` / `_pxLatency` / `_errors` — runtime performance tracking

### Current Host Identity

Each host has **two identity values**:
- `HostConfig.Guid` — assigned at `HostConfig` construction (`Guid.NewGuid()`)
- `BaseHostHealth.guid` — assigned at `BaseHostHealth` construction (`Guid.NewGuid()`)

These are **independent GUIDs** — there is no single canonical host ID today. This matters for modify and delete operations.

---

## 2. Operation: Add a Host

### What Must Happen

1. **Create `HostConfig`** from a connection string (existing `HostConfig` constructor handles parsing)
   - Requires `HostConfig.Initialize()` to have been called (it has, at startup)
   - Creates a `CircuitBreaker` from the DI container (`_serviceProvider.GetService<ICircuitBreaker>()`)
   - Computes `Protocol`, `Port`, `ProbeUrl`, path-matching cache fields

2. **Create `BaseHostHealth` wrapper** — choose `ProbeableHostHealth` or `NonProbeableHostHealth` based on `DirectMode`/`ProbePath` (same logic as `HostHealthCollection` constructor)

3. **Register OAuth audience** — if `UseOAuth && !string.IsNullOrEmpty(Audience)`, call `HostConfig.RegisterWithTokenProvider()` which calls `_tokenProvider.AddAudience(audience)`. This already supports dynamic registration.

4. **Rebuild the host lists atomically** — this is the critical step:
   - Build new `_backendHosts` list (old list + new host)
   - Rebuild `SpecificPathHosts` and `CatchAllHosts` categorization
   - Swap `_backendHosts` reference atomically
   - Call `FilterActiveHosts()` to recompute `_activeHosts`
   - Call `InvalidateIteratorCache()` to force fresh iterators

### Considerations

| Concern | Detail |
|---------|--------|
| **Thread safety** | The poller thread iterates `_backendHosts` in `UpdateHostStatus()`. Adding a host while the poller is mid-iteration could cause issues if you mutate the list in-place. **Must use copy-and-swap, never in-place mutation.** |
| **New host won't be probed immediately** | The poller runs on `PollInterval` (configurable, often seconds). A newly added host will be in `_backendHosts` but not in `_activeHosts` until the next poll cycle marks it healthy. This is actually **correct behavior** — new hosts should prove health before receiving traffic. |
| **`BackendOptions.Hosts` is a separate list** | `Backends._backendHosts` is a reference to `_backendHostCollection.Hosts`, not `BackendOptions.Hosts`. The `BackendOptions.Hosts` list (in the options pattern) is used only at startup. Adding a host at runtime should update `_backendHostCollection`, not `BackendOptions`. |
| **Circuit breaker global counters** | `CircuitBreaker._totalCircuitBreakersCount` is a static `int` incremented in the constructor. Adding a host creates a new `CircuitBreaker`, which correctly increments this counter. No special handling needed. |
| **HostConfig static dependencies** | `HostConfig._tokenProvider`, `_logger`, `_serviceProvider` are static and initialized once at startup. New `HostConfig` instances will use these same statics. This works, but is fragile. |
| **Max host limit** | Consider enforcing a maximum number of hosts to prevent unbounded growth. |

---

## 3. Operation: Modify a Host

### What "Modify" Means

Modifying a host's configuration (e.g., changing its `probe` path, `path` routing, `mode`, `audience`) is **effectively a delete + add** because:

- `HostConfig` fields are derived from `ParsedConfig` at construction time
- Path-matching cache fields (`_isCatchAllPath`, `_normalizedPartialPath`, `_isWildcardPath`, `_wildcardPrefix`) are `readonly` and computed in the constructor
- `ProbeUrl` is computed at construction time from `Protocol`, `Hostname`, `Port`, `ProbePath`
- Changing from probeable to non-probeable (or vice versa) requires a different `BaseHostHealth` subclass

**Recommendation**: Implement modify as **remove old host + add new host** in a single atomic swap. This is simpler and avoids mutation of readonly fields.

### Considerations

| Concern | Detail |
|---------|--------|
| **Preserving host identity** | If you delete+add, the host gets a new GUID. To maintain consistent identity across modifications (important for circuit breaker logs, event telemetry, external tooling), unify the two GUIDs (`HostConfig.Guid` and `BaseHostHealth.guid`) into a single canonical ID on `BaseHostHealth`, and copy the old host's GUID to the new instance during modify. Add a constructor overload or property setter on `BaseHostHealth` that accepts an existing GUID. |
| **Preserving health history** | A delete+add loses the old host's latency history, success rate history, and circuit breaker state. For a probe path change, this may be desirable (new probe = fresh data). For a path routing change, you might want to preserve perf history. |
| **In-flight requests** | Requests currently being proxied to the old host configuration will complete with the old config. This is safe — they hold a reference to the old `BaseHostHealth` object. The next request will pick up the new config via the iterator. |
| **OAuth audience change** | If the audience changes, the old audience's token refresh task remains running (no cleanup in `BackendTokenProvider`). The new audience gets registered. This is a minor leak but not harmful — the old task will keep refreshing an unused token. Consider adding `RemoveAudience()` to `BackendTokenProvider`. |

### What Should Be Modifiable vs. Not

| Property | Modifiable? | Reason |
|----------|-------------|--------|
| `probe` path | ✅ Yes | Changes which URL is polled for health |
| `path` (routing) | ✅ Yes | Changes which requests route to this host |
| `mode` (direct/apim) | ⚠️ Caution | Changes subclass (Probeable vs NonProbeable), use delete+add |
| `host` URL | ⚠️ Caution | Fundamental identity change — effectively a different backend |
| `useOAuth` / `audience` | ✅ Yes | Needs token provider registration/cleanup |
| `processor` | ✅ Yes | Changes stream processing behavior |
| `ipaddress` | ✅ Yes | DNS override change |
| `usesRetryAfter` | ✅ Yes | Behavioral flag |

---

## 4. Operation: Soft-Delete a Host

### What "Soft-Delete" Means

A soft-deleted host should:
1. **Not receive new traffic** — excluded from `_activeHosts` and iterators
2. **Allow in-flight requests to complete** — existing references remain valid
3. **Continue to exist in the system** — visible in status/diagnostics as "disabled"
4. **Be restorable** — can be re-enabled without recreating the host

This is different from a hard delete, where the host is completely removed from all lists.

### Recommended Implementation: `IsEnabled` Flag on `BaseHostHealth`

The cleanest approach is to add an `IsEnabled` flag to `BaseHostHealth`:

```
BaseHostHealth
  + private volatile bool _isEnabled = true;
  + public bool IsEnabled 
  + { 
  +     get => _isEnabled; 
  +     set => _isEnabled = value; 
  + }
```

Then modify `FilterActiveHosts()` in `Backends.cs` to include the flag:

```
Current:  _backendHosts.Where(h => h.SuccessRate() >= _successRate)
Proposed: _backendHosts.Where(h => h.IsEnabled && h.SuccessRate() >= _successRate)
```

**Why this is clean:**
- Single point of change in the filtering logic
- No new lists to manage
- Disabled hosts remain in `_backendHosts` for visibility
- The poller can optionally skip probing disabled hosts (saves network calls)
- Re-enabling is just `host.IsEnabled = true` + next poll cycle picks it up

### Considerations

| Concern | Detail |
|---------|--------|
| **Poller behavior for disabled hosts** | Should disabled hosts still be probed? **Option A**: Skip probing (saves network calls). **Option B**: Continue probing (keeps health data fresh for seamless re-enable). **Recommend Option B** — continue probing disabled hosts. This ensures that when a host is re-enabled, it has current health data and can immediately receive traffic without waiting for probe warmup. The cost is negligible (one extra HTTP probe per poll cycle per disabled host). |
| **Display in status** | `DisplayHostStatus()` iterates `_backendHosts` — disabled hosts should show as "Disabled" rather than "Good" or "Errors". |
| **Circuit breaker** | A disabled host's circuit breaker should be reset when re-enabled, so old failure data doesn't prevent immediate traffic. |
| **Thread safety of `IsEnabled`** | `IsEnabled` is a `bool` read by the poller thread and written by admin operations. Although `bool` reads/writes are individually atomic on x86/.NET, the CPU can reorder instructions and cache values, meaning changes may not be immediately visible across threads. **`volatile` is required** (not optional) to ensure proper memory barriers so the poller thread sees the updated value promptly. |
| **Persistence** | Soft-deleted state is in-memory only. A pod restart reverts to the original env var configuration. This is acceptable for the current architecture but should be documented. |

### Alternative: Separate Disabled List

Instead of a flag, maintain a separate `HashSet<Guid> _disabledHostIds`. This avoids modifying `BaseHostHealth` but adds complexity to `FilterActiveHosts()`.

**Recommendation**: The `IsEnabled` flag is simpler and more natural. Use it.

---

## 5. Downstream Propagation — Where Hosts Are Consumed

When any host mutation happens (add/modify/soft-delete), these downstream consumers must see the updated state. Here's exactly what needs to happen:

| Consumer | How It Gets Hosts | What Propagates Changes |
|----------|-------------------|------------------------|
| `Backends.Run()` (poller) | Iterates `_backendHosts` | Reference swap of `_backendHosts` |
| `Backends.FilterActiveHosts()` | Filters from `_backendHosts` | Reference swap (same as above) |
| `Backends._activeHosts` | Set by `FilterActiveHosts()` | Automatic — recomputed each poll cycle |
| `IteratorFactory` caches | `_cachedActiveHosts`, `_cachedSpecificPathHosts`, `_cachedCatchAllHosts` | `InvalidateCache()` clears all; rebuilt on next request |
| `SharedIteratorRegistry` | Keyed by path, holds `SharedHostIterator` instances | `InvalidateAll()` clears all; rebuilt on next request |
| `ProxyWorker` | Calls `_backends.GetActiveHosts()` each request | Automatic — gets latest `_activeHosts` reference |
| `HealthCheckService` | Calls `_backends.GetHosts()` for status display | Automatic — gets latest `_backendHosts` reference |
| `HostHealthCollection` | Holds `Hosts`, `SpecificPathHosts`, `CatchAllHosts` lists | **Must be rebuilt** on add/remove — this is the key mutation point |

### The `HostHealthCollection` Problem

`HostHealthCollection` is the **hardest piece to update**:
- It's a singleton registered in DI
- Its lists are set `private set` — populated only in the constructor
- `Backends._backendHosts` is a direct reference to `HostHealthCollection.Hosts`
- `Backends.GetSpecificPathHosts()` delegates to `_backendHostCollection.SpecificPathHosts`
- `Backends.GetCatchAllHosts()` delegates to `_backendHostCollection.CatchAllHosts`

**Options:**
1. **Add mutation methods to `IHostHealthCollection`** — e.g., `AddHost(BaseHostHealth)`, `RemoveHost(Guid)`, `RebuildCategories()`. This keeps the singleton but makes it mutable.
2. **Bypass `HostHealthCollection`** — have `Backends` maintain its own `_specificPathHosts` and `_catchAllHosts` lists, rebuilt on mutation. This decouples from the singleton.
3. **Replace `HostHealthCollection` entirely** — atomic swap a new `HostHealthCollection` instance. This requires updating the DI registration or using a wrapper.

**Recommendation**: Option 2 — have `Backends` own the categorized lists internally. This is the least disruptive. `HostHealthCollection` remains the startup initializer, but `Backends` takes over for runtime categorization.

---

## 6. HostConfig Construction Dependencies

Creating a new `HostConfig` at runtime requires these static dependencies to be initialized:

| Dependency | Initialized Where | Available at Runtime? |
|------------|--------------------|-----------------------|
| `HostConfig._serviceProvider` | `HostConfig.Initialize()` in `Program.Main()` | ✅ Yes — set once, never cleared |
| `HostConfig._tokenProvider` | `HostConfig.Initialize()` in `Program.Main()` | ✅ Yes — same as above |
| `HostConfig._logger` | `HostConfig.Initialize()` in `Program.Main()` | ✅ Yes — same as above |
| `ICircuitBreaker` (from DI) | Registered as `Transient` in DI | ✅ Yes — new instance per `GetService<ICircuitBreaker>()` call |

**Conclusion**: Runtime `HostConfig` creation works today with no changes. The static initialization pattern, while not ideal, does support dynamic host creation.

### Connection String Format (Input for Add/Modify)

The existing `HostConfig` constructor accepts the same connection string format used in env vars:

```
host=https://api.example.com;probe=/health;mode=apim;path=/api/v1;useoauth=true;audience=api://my-app
```

This means the add/modify API can accept the same format, reusing all existing parsing logic in `HostConfig.TryParseConfig()`.

---

## 7. Concurrency Specifics

### Thread Access Patterns

```
Poller Thread (1):
  - READS _backendHosts (iterates for health checks)
  - WRITES _activeHosts (atomic reference swap)
  - READS/WRITES currentHostStatus dictionary
  - CALLS InvalidateIteratorCache()

ProxyWorker Threads (~1000):
  - READS _activeHosts (via GetActiveHosts())
  - READS SpecificPathHosts/CatchAllHosts (via GetSpecificPathHosts()/GetCatchAllHosts())
  - READS individual BaseHostHealth properties (latency, success rate, circuit breaker)

Admin Operation (new — single-threaded):
  - WRITES _backendHosts (atomic reference swap)
  - WRITES SpecificPathHosts/CatchAllHosts (rebuild + swap)
  - CALLS InvalidateIteratorCache()
  - CALLS FilterActiveHosts()
```

### Race Condition Analysis

| Scenario | Risk | Mitigation |
|----------|------|------------|
| Admin adds host while poller is iterating `_backendHosts` | Poller holds reference to old list; safe — old list remains valid | Copy-and-swap pattern |
| Admin adds host while ProxyWorker creates iterator | Worker gets either old or new host list depending on timing | Acceptable — next request gets updated list |
| Admin soft-deletes host while request is mid-proxy to that host | Request completes normally — it holds a reference to the `BaseHostHealth` object | No issue — object remains in memory |
| Admin modifies host while poller is mid-probe to that host | Old probe URL is used for current probe; next cycle uses new | Acceptable — one stale probe is harmless |
| Two concurrent admin operations | Both do copy-and-swap, second overwrites first | **Must serialize admin operations** — use a simple `lock` or `SemaphoreSlim` |

### Key Recommendation: Serialize Mutations

All add/modify/soft-delete operations should be serialized (e.g., `lock(_hostMutationLock)`). This prevents two concurrent admin operations from racing on the copy-and-swap. The lock scope is tiny (list copy + reference swap), so it won't impact proxy throughput.

---

## 8. What About `BackendOptions.Hosts`?

`BackendOptions.Hosts` is the original list populated by `RegisterBackends()` from env vars. After startup, it serves two purposes:
1. `HostHealthCollection` constructor reads it to build categorized lists
2. It's never read again

**Recommendation**: Do **not** update `BackendOptions.Hosts` on runtime mutations. It represents the startup configuration. Runtime state lives in `Backends._backendHosts`. This separation is actually useful — it tells you "what did the env vars say?" vs. "what is the runtime state?"

---

## 9. Summary of Recommendations

### For Add:
1. Accept the same connection string format as `Host1` env vars
2. Create `HostConfig` → `BaseHostHealth` (choose subclass based on mode/probe)
3. Register OAuth audience if needed
4. Copy-and-swap `_backendHosts`, rebuild categorized lists, invalidate caches
5. Let the poller naturally discover and health-check the new host

### For Modify:
1. Implement as atomic **remove old + add new** in a single swap
2. **Unify host identity**: Before implementing modify, consolidate the two independent GUIDs (`HostConfig.Guid` and `BaseHostHealth.guid`) into a single canonical host ID owned by `BaseHostHealth`. When performing a modify-as-delete+add, explicitly copy the old `BaseHostHealth.guid` to the new instance so that circuit breaker logs, event telemetry, and any external references remain consistent. This means adding a `BaseHostHealth` constructor overload or setter that accepts an existing GUID.
3. Consider preserving or resetting health/latency history per property changed
4. Clean up old OAuth audience if it changed

### For Soft-Delete:
1. Add `volatile bool IsEnabled` field to `BaseHostHealth` (volatile is required for cross-thread visibility)
2. Modify `FilterActiveHosts()` to check `IsEnabled`
3. Continue probing disabled hosts (keeps health data fresh for seamless re-enable)
4. Display disabled state in `DisplayHostStatus()` 
5. Allow re-enable by setting `IsEnabled = true`

### Cross-Cutting:
1. Serialize all mutation operations with a lock
2. Do not mutate `BackendOptions.Hosts` — it's the startup config snapshot
3. Bypass `HostHealthCollection` for runtime categorization — have `Backends` own its own specific/catch-all lists
4. Reuse existing `HostConfig` constructor and `InvalidateIteratorCache()` patterns
5. All mutations are in-memory only — document that pod restart reverts to env var config
6. Consider a `BackendTokenProvider.RemoveAudience()` method for modify/delete cleanup

### To Proceed — Suggested Order:
1. **Start with soft-delete** — it's the simplest (single flag + filter change) and immediately useful for operations
2. **Then add** — builds on the copy-and-swap pattern already used by `FilterActiveHosts()`
3. **Then modify** — implemented as delete+add once both primitives exist
4. **Then expose via API** — add `/admin/backends` endpoints using the fast-path pattern in `Server.Run()`
