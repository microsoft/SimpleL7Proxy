# Backend Dynamic Management — Analysis & Proposal

## Executive Summary

This document presents a comprehensive analysis of the backend management architecture in SimpleL7Proxy (streaming-release-tr branch) and proposes a clean approach for dynamically updating the list of backends (add, update, delete) at runtime — including whether an API is the right solution.

---

## 1. Current Architecture Analysis

### 1.1 Backend Lifecycle (Today)

Backends are **statically configured at startup** via environment variables (`Host1`–`Host9`) and remain **immutable for the lifetime of the process**:

```
Startup Flow:
  Environment Variables (Host1, Host2, ...)
    → BackendHostConfigurationExtensions.RegisterBackends()
      → HostConfig objects created (one per host)
        → BackendOptions.Hosts list populated
          → HostHealthCollection categorizes: SpecificPathHosts vs CatchAllHosts
            → Backends service starts polling loop
              → FilterActiveHosts() runs every PollInterval
                → _activeHosts reference swapped atomically
```

**Key observation**: Once `BackendOptions.Hosts` is populated, no code path adds to or removes from this list.

### 1.2 Key Components & Their Mutability

| Component | Mutable at Runtime? | Thread Safety | Notes |
|-----------|---------------------|---------------|-------|
| `BackendOptions.Hosts` | ❌ No | N/A (set once) | `List<HostConfig>`, populated during startup |
| `HostHealthCollection.Hosts` | ❌ No | N/A (set once) | Copies from BackendOptions at construction |
| `HostHealthCollection.SpecificPathHosts` | ❌ No | N/A | Categorized at construction time |
| `HostHealthCollection.CatchAllHosts` | ❌ No | N/A | Categorized at construction time |
| `Backends._backendHosts` | ❌ No | Read-only after init | Reference to HostHealthCollection.Hosts |
| `Backends._activeHosts` | ✅ Yes | Atomic reference swap | Filtered subset, updated every poll cycle |
| `IteratorFactory` caches | ✅ Yes | `volatile` + `lock` | Invalidated when active hosts change |
| `SharedIteratorRegistry` | ✅ Yes | `ConcurrentDictionary` | Invalidated when hosts change |
| `CircuitBreaker` (per-host) | ✅ Yes | `ConcurrentQueue` + `Interlocked` | Tracks per-host failure state |
| `BackendTokenProvider` | ✅ Yes | `ConcurrentDictionary` | Per-audience token refresh |

### 1.3 Concurrency Model

The proxy operates with ~1000 concurrent `ProxyWorker` tasks, all reading from shared backend state:

- **Poller Thread (1)**: Updates health, filters active hosts, invalidates caches
- **ProxyWorker Threads (N)**: Read `_activeHosts`, create iterators, proxy requests
- **No write contention during reads**: Workers only read; poller only writes via atomic swap

This model is **well-suited for safe runtime updates** because the atomic reference-swap pattern used by `FilterActiveHosts()` can be extended to handle backend list changes.

### 1.4 What Already Changes at Runtime

The system already handles dynamic changes to the **active subset** of backends:

1. **Health-based filtering**: Unhealthy hosts are excluded from `_activeHosts`
2. **Latency updates**: Host ordering changes based on measured latency
3. **Circuit breaker state**: Hosts can be temporarily blocked
4. **Iterator cache invalidation**: `IteratorFactory.InvalidateCache()` is called when active hosts change
5. **Shared iterator invalidation**: `SharedIteratorRegistry.InvalidateAll()` refreshes ordering

**The gap**: These only filter/reorder the _existing_ host list — they never add or remove hosts from `_backendHosts`.

---

## 2. Requirements for Dynamic Backend Updates

### 2.1 Operations Needed

| Operation | Description | Complexity |
|-----------|-------------|------------|
| **Add** | Introduce a new backend at runtime | Medium — requires HostConfig creation, health collection update, poller integration |
| **Delete** | Remove a backend at runtime | Medium — requires safe removal while requests may be in-flight to that host |
| **Update** | Modify backend configuration (e.g., path, probe, mode) | High — effectively a delete + add, but must handle in-flight requests gracefully |
| **List** | View current backends and their status | Low — data already available via `Backends.GetHosts()` |

### 2.2 Safety Requirements

1. **Zero-downtime**: Changes must not disrupt in-flight requests
2. **Thread safety**: Must be safe with ~1000 concurrent ProxyWorker reads
3. **Consistency**: No request should see a partially-updated host list
4. **Health integration**: New hosts must be polled before being marked active
5. **Token integration**: OAuth hosts need token registration before use
6. **Circuit breaker**: New hosts need their own circuit breaker instance

---

## 3. Proposed Implementation Approaches

### 3.1 Approach A: Admin API on the Main HttpListener (Recommended)

**Concept**: Add administrative endpoints to the existing `HttpListener` in `server.cs`, intercepted before queue processing.

**Endpoints**:
```
GET    /admin/backends           → List all backends with health status
POST   /admin/backends           → Add a new backend
DELETE /admin/backends/{hostId}  → Remove a backend by GUID
PUT    /admin/backends/{hostId}  → Update a backend's configuration
```

**How it fits the architecture**:

The `Server.Run()` method already has a fast-path for probes (`Constants.probes`). An admin path check can be added similarly:

```
Request arrives at HttpListener
  ├── Is probe path? → Handle immediately (existing)
  ├── Is admin path? → Handle immediately (NEW)
  └── Otherwise → Enqueue for proxy workers (existing)
```

**Implementation sketch** (key changes needed):

1. **`IBackendService` interface** — Add methods:
   - `AddHost(HostConfig config)`
   - `RemoveHost(Guid hostId)`
   - `UpdateHost(Guid hostId, HostConfig newConfig)`

2. **`Backends` class** — Implement atomic list replacement:
   ```csharp
   public void AddHost(HostConfig config)
   {
       // Create health wrapper
       // Add to _backendHosts (new list, atomic swap)
       // Rebuild HostHealthCollection categories
       // Register with token provider if OAuth
       // Invalidate iterator cache
   }
   ```

3. **`HostHealthCollection`** — Add mutation support:
   - Make `Hosts`, `SpecificPathHosts`, `CatchAllHosts` rebuildable
   - Or replace HostHealthCollection entirely (atomic swap of a new instance)

4. **`Server.Run()`** — Add admin request routing before queue enqueue

**Pros**:
- Minimal infrastructure change (reuses existing HttpListener)
- Follows existing pattern (fast-path routing already proven)
- No new ports, no new dependencies
- Admin operations complete synchronously
- Can be protected by existing auth mechanisms (`ValidateAuthAppID`)

**Cons**:
- Admin and proxy traffic share the same listener
- Under extreme load, admin requests compete for listener acceptance
- HttpListener lacks built-in routing middleware (manual path parsing)

### 3.2 Approach B: Admin API on ProbeServer (Separate Port)

**Concept**: Extend the `ProbeServer` (currently manages health endpoints) to also serve admin API endpoints on a separate port.

Note: ProbeServer currently doesn't run its own HTTP listener — it only provides response methods called by the main server. This approach would require adding a dedicated listener.

**Pros**:
- Admin traffic isolated from proxy traffic
- Could use ASP.NET Core minimal API on the probe port for richer routing
- Kubernetes can expose admin port separately with different access controls

**Cons**:
- Requires adding a new HTTP listener or Kestrel host to ProbeServer
- More infrastructure change than Approach A
- ProbeServer is currently a lightweight timer-based service, not a request handler

### 3.3 Approach C: File/Config Watch (No API)

**Concept**: Watch a configuration file (or environment variables) for changes and reload backends automatically.

**Implementation**: Use `FileSystemWatcher` on a mounted ConfigMap file, or periodically re-read a configuration endpoint.

**Pros**:
- No API surface to secure
- Integrates with K8s ConfigMap updates (rolling update without pod restart)
- Simpler implementation

**Cons**:
- No immediate feedback on whether the update succeeded
- File-based updates are harder to validate
- Cannot query current state
- Slower feedback loop (polling interval + propagation delay)
- ConfigMap changes may still trigger pod restart depending on K8s config

### 3.4 Approach D: Hybrid (API + Config Watch)

Combine API for immediate operations with file watch for declarative configuration.

---

## 4. Recommendation: Approach A (Admin API on Main HttpListener)

### 4.1 Why an API Is Worth It

| Factor | Assessment |
|--------|------------|
| **Operational agility** | ✅ Add/remove backends in seconds without pod restart |
| **Observability** | ✅ Query current backend state, health, and latency via API |
| **Integration** | ✅ Automation scripts, CI/CD pipelines, and dashboards can call the API |
| **Safety** | ✅ API can validate configuration before applying |
| **Complexity** | ⚠️ Moderate — but the atomic-swap pattern is already proven in the codebase |
| **Security** | ✅ Can reuse existing `ValidateAuthAppID` mechanism |

### 4.2 Why the Main HttpListener (Not a Separate Port)

1. **Already proven**: The fast-path probe routing (`/health`, `/readiness`, etc.) works reliably under load
2. **Single listener**: No additional port configuration in deployments
3. **Auth reuse**: Admin endpoints can use the same auth validation pipeline
4. **Minimal change**: 3-4 files need modification vs. new service infrastructure

### 4.3 Detailed Design

#### Thread-Safe Backend List Mutation

The core challenge is safely mutating the backend list while ~1000 workers read it. The solution mirrors the existing `FilterActiveHosts()` pattern:

```
Current pattern (health filtering):
  new_list = _backendHosts.Where(healthy).ToList()
  _activeHosts = new_list          ← atomic reference swap
  InvalidateIteratorCache()

Proposed pattern (add/remove):
  new_hosts = new List(_backendHosts) { + newHost }     ← copy + modify
  _backendHosts = new_hosts                              ← atomic reference swap
  RebuildHostCategories(new_hosts)                       ← rebuild specific/catch-all
  FilterActiveHosts()                                    ← recompute active set
  InvalidateIteratorCache()                              ← force fresh iterators
```

**Key insight**: Because workers only hold a reference to the _list_ (not individual items), swapping the list reference is atomic and safe. Workers currently iterating the old list will complete naturally; new requests will pick up the new list.

#### API Request/Response Design

**List Backends**:
```
GET /admin/backends
Response 200:
{
  "backends": [
    {
      "id": "guid",
      "host": "https://api.example.com",
      "path": "/api/v1",
      "mode": "direct",
      "probe": "/health",
      "status": "active",
      "successRate": 0.98,
      "avgLatency": 45.2,
      "circuitBreakerState": "closed"
    }
  ],
  "activeCount": 3,
  "totalCount": 4,
  "loadBalanceMode": "latency"
}
```

**Add Backend**:
```
POST /admin/backends
Content-Type: application/json
{
  "host": "https://new-api.example.com",
  "probe": "/health",
  "mode": "apim",
  "path": "/api/v2",
  "useOAuth": false
}
Response 201: { "id": "new-guid", "status": "pending" }
```

**Remove Backend**:
```
DELETE /admin/backends/{guid}
Response 200: { "removed": true, "host": "https://old-api.example.com" }
```

**Update Backend**:
```
PUT /admin/backends/{guid}
Content-Type: application/json
{
  "probe": "/health-v2",
  "path": "/api/v3"
}
Response 200: { "id": "guid", "status": "updated" }
```

#### Files to Modify

| File | Changes |
|------|---------|
| `IBackendService.cs` | Add `AddHost()`, `RemoveHost()`, `UpdateHost()` to interface |
| `Backends.cs` | Implement thread-safe add/remove/update with atomic list swap |
| `IHostHealthCollection.cs` | Add `Rebuild()` method or make it replaceable |
| `HostHealthCollection.cs` | Support rebuilding categories from a new host list |
| `server.cs` | Add admin path routing in `Run()` loop (before queue enqueue) |
| `Constants.cs` | Add admin path constants |

#### Security Considerations

- Admin endpoints should require authentication (reuse `ValidateAuthAppID` or a separate admin token)
- Consider rate limiting admin operations
- Log all admin operations to the event pipeline for audit trail
- Optional: restrict admin endpoints to internal network (K8s network policy)

### 4.4 Migration Path

1. **Phase 1**: Add read-only `GET /admin/backends` endpoint (zero risk, immediate operational value)
2. **Phase 2**: Add `POST` (add) with atomic list swap pattern
3. **Phase 3**: Add `DELETE` (remove) with graceful drain of in-flight requests
4. **Phase 4**: Add `PUT` (update) as atomic delete + add

---

## 5. Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Backends added via API are lost on restart | Document this behavior; combine with ConfigMap for persistence |
| In-flight requests to removed backend fail | Removed hosts complete current requests; circuit breaker prevents new ones |
| API abuse adds too many backends | Validate host configuration; enforce maximum backend count |
| OAuth token registration for new hosts | `BackendTokenProvider.AddAudience()` already supports dynamic registration |
| HostConfig requires static `_serviceProvider` | Already initialized at startup; new HostConfig instances work fine |

---

## 6. Alternative: Why NOT a Separate Microservice

A separate admin microservice (or sidecar) was considered but rejected because:

1. **State locality**: Backend state lives in the proxy process; a separate service would need IPC
2. **Latency**: Admin operations should be immediate, not requiring cross-process communication
3. **Deployment complexity**: Additional container/service to deploy and manage
4. **Already has the pattern**: The existing probe fast-path proves in-process admin routing works

---

## 7. Conclusion

**Yes, implementing an Admin API is worth it.** The proxy's existing architecture — particularly the atomic reference-swap pattern used for health filtering and the fast-path probe routing — provides a natural foundation for runtime backend management. The recommended approach (Approach A: Admin API on the main HttpListener) delivers maximum operational value with minimal architectural change.

The implementation can be done incrementally, starting with a read-only endpoint and progressing to full CRUD operations, with each phase independently testable and deployable.
