# Host Swap Update Plan

## Goal

Define the simplest way to atomically swap the entire set of backend hosts at runtime without impacting the 1000+ concurrent ProxyWorker tasks.

---

## 1. Why "Swap All" Is the Simplest Operation

Previous proposals explored incremental add/modify/soft-delete. For a full host replacement, swapping the entire list at once is **simpler and safer** than incremental mutations because:

- **One atomic operation** instead of coordinating multiple add/remove steps
- **No partial states** — workers never see a mix of old and new hosts
- **Leverages the existing pattern** — `FilterActiveHosts()` already does an atomic reference swap of `_activeHosts` every poll cycle; a full host swap extends this same proven pattern to `_backendHosts`

---

## 2. Current Reference Chain (What Must Be Swapped)

When a ProxyWorker needs to send a request to a backend, it traverses this chain:

```
ProxyWorker calls IteratorFactory.CreateSinglePassIterator(_backends, ...)
  → IteratorFactory calls _backends.GetSpecificPathHosts()
  → IteratorFactory calls _backends.GetCatchAllHosts()
    → Backends.GetSpecificPathHosts() → _backendHostCollection.SpecificPathHosts
    → Backends.GetCatchAllHosts()     → _backendHostCollection.CatchAllHosts
  → IteratorFactory filters by path, creates iterator over matching hosts
  → ProxyWorker iterates the hosts
```

Separately, the poller thread in `Backends.Run()` iterates:

```
Poller iterates _backendHosts (for health probing)
  → FilterActiveHosts() filters to _activeHosts (atomic reference swap)
  → InvalidateIteratorCache() clears IteratorFactory + SharedIteratorRegistry
```

**Key insight**: Workers never cache host references beyond a single request. They call `GetSpecificPathHosts()`/`GetCatchAllHosts()` fresh each time. This means a swap only needs to update the references these methods return, and every subsequent request automatically uses the new hosts.

---

## 3. The Exact Fields That Need Swapping

For a full host swap to take effect across the entire system, these fields must be updated:

| Field | Owner | Current Access | Swap Strategy |
|-------|-------|---------------|---------------|
| `_backendHosts` | `Backends` | Poller iterates; `GetHosts()` returns it | Atomic reference swap to new list |
| `_activeHosts` | `Backends` | Workers read via `GetActiveHosts()` | Rebuilt by `FilterActiveHosts()` after swap |
| `SpecificPathHosts` | `IHostHealthCollection` | `GetSpecificPathHosts()` delegates | Must be updated — see Section 4 |
| `CatchAllHosts` | `IHostHealthCollection` | `GetCatchAllHosts()` delegates | Must be updated — see Section 4 |
| `IteratorFactory` caches | `IteratorFactory` (static) | Workers read on each request | `InvalidateCache()` — already exists |
| `SharedIteratorRegistry` | `SharedIteratorRegistry` | Shared iterators by path | `InvalidateAll()` — already exists |
| `currentHostStatus` | `Backends` (private dict) | Poller tracks host status | Must be cleared/rebuilt for new hosts |

---

## 4. The `HostHealthCollection` Bottleneck

The current design has a structural problem for swapping:

```csharp
// Backends.cs constructor
_backendHostCollection = backendHostCollection;   // IHostHealthCollection singleton
_backendHosts = backendHostCollection.Hosts;       // reference to collection's list

// Backends.cs methods  
GetSpecificPathHosts() => _backendHostCollection.SpecificPathHosts;
GetCatchAllHosts()     => _backendHostCollection.CatchAllHosts;
```

`HostHealthCollection` is a DI singleton whose lists are `private set` and only populated in the constructor. There is no way to update them at runtime.

### Recommended Change: Internalize Categorized Lists in `Backends`

Instead of delegating to `HostHealthCollection`, have `Backends` own its own categorized lists:

```
Suggested new fields in Backends:
  private List<BaseHostHealth> _specificPathHosts;
  private List<BaseHostHealth> _catchAllHosts;

Suggested change to methods:
  GetSpecificPathHosts() => _specificPathHosts;   // was: _backendHostCollection.SpecificPathHosts
  GetCatchAllHosts()     => _catchAllHosts;        // was: _backendHostCollection.CatchAllHosts
```

**This is the only structural change needed.** `HostHealthCollection` remains unchanged as the startup initializer. `Backends` takes ownership of the runtime-mutable state.

---

## 5. The Swap Method — Suggested Pseudocode

Add a single new method to `Backends` (and `IBackendService`):

```
SwapHosts(List<HostConfig> newHostConfigs):

  1. Build new BaseHostHealth objects:
     For each HostConfig in newHostConfigs:
       - Create ProbeableHostHealth or NonProbeableHostHealth (same logic as HostHealthCollection constructor)
       - Register OAuth audience if needed (HostConfig.RegisterWithTokenProvider())

  2. Categorize new hosts (same logic as HostHealthCollection constructor):
     newCatchAllHosts     = hosts where string.IsNullOrEmpty(PartialPath?.Trim())
                            or PartialPath?.Trim() == "/"
                            or PartialPath?.Trim() == "/*"
     newSpecificPathHosts = all remaining hosts

  3. Atomic swap (all under a single lock):
     lock(_hostSwapLock):
       _backendHosts      = newHosts
       _specificPathHosts  = newSpecificPathHosts
       _catchAllHosts      = newCatchAllHosts
       currentHostStatus.Clear()

  4. Propagate (outside lock):
     FilterActiveHosts()       → rebuilds _activeHosts from new _backendHosts
     InvalidateIteratorCache() → clears IteratorFactory + SharedIteratorRegistry
```

### Why This Is Safe for 1000+ Workers

- **Workers hold no references between requests.** Each request calls `GetSpecificPathHosts()`/`GetCatchAllHosts()` fresh. A worker currently mid-request holds references to old `BaseHostHealth` objects — those objects remain valid in memory until the request completes (GC won't collect them while referenced).

- **The poller may be mid-iteration.** If the poller is iterating the old `_backendHosts` when the swap happens, it will finish its current cycle with the old list. On the next cycle, it picks up the new `_backendHosts` reference. This is safe because the swap is a reference reassignment, not an in-place mutation.

- **Iterators are invalidated.** `InvalidateCache()` and `InvalidateAll()` clear all cached iterators. The next worker to create an iterator gets one built from the new host lists.

- **No worker-visible lock contention.** The lock in step 3 is held only for the duration of 3 reference assignments + a dictionary clear — microseconds at most, even with many hosts. Workers don't acquire this lock; they read the new references via normal field access.

---

## 6. What Happens to In-Flight Requests During Swap

| Scenario | What Happens | Impact |
|----------|-------------|--------|
| Worker is mid-proxy to old host | Request completes normally — holds `BaseHostHealth` reference | ✅ No impact |
| Worker is creating iterator at swap moment | Gets either old or new host list | ✅ Acceptable — next request uses new |
| Worker reads `GetActiveHosts()` at swap moment | Gets either old or new filtered list | ✅ Acceptable — `_activeHosts` is rebuilt right after swap |
| Poller is mid-iteration at swap moment | Finishes current cycle with old list, next cycle uses new | ✅ No impact |
| New host gets probed for the first time | `NonProbeableHostHealth` is immediately active; `ProbeableHostHealth` waits for first successful probe | ✅ Correct behavior |

---

## 7. Files to Modify

| File | Change | Risk |
|------|--------|------|
| **`IBackendService.cs`** | Add `SwapHosts(List<HostConfig> newHosts)` to interface | Low — additive |
| **`Backends.cs`** | Add `_specificPathHosts`/`_catchAllHosts` fields; change `GetSpecificPathHosts()`/`GetCatchAllHosts()` to use them; implement `SwapHosts()` method; add `_hostSwapLock` | Medium — main logic change |
| **`IHostHealthCollection.cs`** | No change needed | None |
| **`HostHealthCollection.cs`** | No change needed | None |
| **`IteratorFactory.cs`** | No change needed (already has `InvalidateCache()`) | None |
| **`SharedIteratorRegistry.cs`** | No change needed (already has `InvalidateAll()`) | None |

**Total: 2 files modified, 0 new files.**

---

## 8. How to Trigger the Swap

The swap method needs to be invoked. Options (ordered by simplicity):

### Option A: Admin Endpoint on Main HttpListener (Recommended)

The `Server.Run()` loop already has a fast-path for probe requests (`/health`, `/readiness`, etc.). Add a similar fast-path for an admin swap endpoint:

```
POST /admin/swap-hosts
Content-Type: application/json
Body: [
  "host=https://api1.example.com;probe=/health;path=/api",
  "host=https://api2.example.com;mode=direct;path=/embeddings"
]
```

This is the same connection-string format already used in `Host1`..`Host9` environment variables, so the existing `HostConfig` constructor handles all parsing.

### Option B: File-Based Trigger

Watch a mounted config file (e.g., K8s ConfigMap) for changes. On change, read new host definitions and call `SwapHosts()`. Simpler but no immediate feedback.

### Option C: Environment Variable Re-Read

Periodically re-read `Host1`..`Host9` env vars. Works if env vars are updated externally (e.g., via K8s), but env vars typically require a pod restart to change.

**Recommendation: Option A** — it matches the existing probe fast-path pattern and provides immediate feedback.

---

## 9. Input Format

Reuse the existing connection-string format that `HostConfig` already parses. No new config format needed:

```
host=https://backend.example.com;probe=/health;mode=apim;path=/api;useoauth=true;audience=api://my-app
```

For the swap endpoint, accept a JSON array of these connection strings. The `HostConfig` constructor handles all validation and parsing, including:
- URL normalization
- Protocol/port extraction  
- Path matching cache precomputation
- Circuit breaker creation via DI
- OAuth audience registration

---

## 10. Things to Consider

### 10.1 New Hosts Start with No Health History

After a swap, probed hosts (`ProbeableHostHealth`) start with a success rate of `1.0` (benefit of the doubt) and won't have real health data until the first poll cycle. This means:
- New probed hosts will be immediately included in `_activeHosts` on first `FilterActiveHosts()` call
- They will appear healthy by default and receive traffic right away
- If a new host is actually unhealthy, it will be filtered out after the first poll cycle

**This is acceptable** — it matches the existing startup behavior.

### 10.2 OAuth Token Warm-Up

If new hosts require OAuth tokens, `RegisterWithTokenProvider()` triggers `BackendTokenProvider.AddAudience()` which starts a background refresh task. Tokens may not be ready immediately. Consider:
- Calling `WaitForStartup()` equivalent after swap for OAuth hosts
- Or accepting that the first few requests may fail until tokens are ready (circuit breaker will handle this)

### 10.3 Old Host Cleanup

After a swap, old `BaseHostHealth` objects become unreferenced. They will be GC'd once all in-flight requests complete. No explicit cleanup needed. The only consideration:
- Old OAuth audiences keep their background refresh tasks running in `BackendTokenProvider`. These are harmless (refresh unused tokens) but could be cleaned up by adding a `RemoveAudience()` method.
- Old `CircuitBreaker` static counters (`_totalCircuitBreakersCount`) will not decrement. Consider adding cleanup logic or ignoring (counts are informational only).

### 10.4 Validation Before Swap

The swap method should validate new hosts before applying:
- At least one host must be provided (don't swap to empty)
- Each connection string must parse successfully
- Optional: reject swap if no new hosts are reachable (pre-probe)

### 10.5 Logging and Telemetry

Log the swap event with:
- Previous host count and list
- New host count and list
- Timestamp
- Caller info (if triggered via API)

This provides an audit trail for troubleshooting.

### 10.6 Thread Safety Guarantees

The proposed swap involves **three** fields (`_backendHosts`, `_specificPathHosts`, `_catchAllHosts`). A worker could theoretically read the new `_specificPathHosts` but the old `_catchAllHosts` in a tiny window between individual reference assignments.

**Recommended solution: Immutable snapshot object.** Wrap the three lists in a single immutable record/class and swap the entire snapshot reference atomically:

```
record HostSnapshot(
    List<BaseHostHealth> AllHosts,
    List<BaseHostHealth> SpecificPathHosts,
    List<BaseHostHealth> CatchAllHosts
);

// In Backends:
private volatile HostSnapshot _hostSnapshot;

GetSpecificPathHosts() => _hostSnapshot.SpecificPathHosts;
GetCatchAllHosts()     => _hostSnapshot.CatchAllHosts;
```

This guarantees that a worker always reads a consistent set of all three lists from the same swap generation — no possibility of mixing old and new. A single `volatile` reference swap of the snapshot object provides both atomicity and memory visibility, eliminating the need for any lock on the read path.

The swap method then becomes:
```
var newSnapshot = new HostSnapshot(newHosts, newSpecificPathHosts, newCatchAllHosts);
_hostSnapshot = newSnapshot;  // single atomic reference assignment
```

**Fallback alternative**: If the snapshot pattern feels over-engineered, using a `lock` for the write side (step 3 in section 5) combined with `volatile` on each of the three fields is also safe in practice, since the window between field updates is extremely small and the worst case is one request using a mix of old/new lists for a single request cycle.

---

## 11. Summary

The simplest way to swap backend hosts at runtime:

1. **Add two private fields** to `Backends`: `_specificPathHosts` and `_catchAllHosts` (initialized from `HostHealthCollection` at startup)
2. **Change `GetSpecificPathHosts()`/`GetCatchAllHosts()`** to return these fields instead of delegating to `HostHealthCollection`
3. **Add `SwapHosts()` method** that builds new objects, atomically swaps all three list references under a lock, clears status, then rebuilds active hosts and invalidates caches
4. **Expose via `/admin/swap-hosts` endpoint** using the existing probe fast-path pattern in `Server.Run()`

**Files changed: 2** (`IBackendService.cs`, `Backends.cs`) plus the trigger mechanism.  
**Worker impact: None** — workers acquire no locks and pick up new hosts on next request.  
**Pattern: Proven** — mirrors the existing `FilterActiveHosts()` atomic reference swap.
