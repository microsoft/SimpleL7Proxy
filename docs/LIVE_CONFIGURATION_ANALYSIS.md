# Live Configuration Update Analysis

## Overview

This document provides a comprehensive analysis of all configuration parameters in SimpleL7Proxy,
classifying each by its live-update capability and identifying the mechanism changes required to
enable runtime updates where they are not currently supported.

## Architecture Summary

### Configuration Loading Pipeline

```
Environment Variables
        │
        ▼
LoadBackendOptions()          ← Creates a single BackendOptions object
        │
        ▼
AddBackendHostConfiguration()
        │
        ├── services.AddSingleton(backendOptions)        ← Original object (Object A)
        │
        └── services.Configure<BackendOptions>(opt =>    ← Property-copied clone (Object B)
              copy all properties from A to B)
```

### Injection Patterns in Use

| Pattern | How It Works | Live-Update Capable? | Consumers |
|---------|-------------|---------------------|-----------|
| `BackendOptions` (direct) | Injects Object A (original singleton) | Only if A's properties are mutated in-place | `UserProfile`, `ProxyWorker` (via ProxyWorkerCollection) |
| `IOptions<BackendOptions>` | Returns Object B (property-copied clone) via `.Value` | ❌ No — `.Value` returns a frozen singleton | 20+ services (see list below) |
| `IOptionsMonitor<BackendOptions>` | Returns `.CurrentValue` which can react to changes | ⚠️ Theoretically yes, but no reload source is configured | `BlobWriterFactory`, `ServiceBusFactory` |

### Critical Finding

**No configuration reload source exists.** The application does not use `IConfigurationRoot.Reload()`,
file watchers on `appsettings.json`, or any other mechanism to push updated values into the
`IOptions`/`IOptionsMonitor` pipeline after startup. Therefore:

- `IOptions<BackendOptions>.Value` → Always returns the same object (Object B), never updated
- `IOptionsMonitor<BackendOptions>.CurrentValue` → Also never updated (no change notifications fire)
- Direct `BackendOptions` singleton → Object A, also never updated (except `AsyncModeEnabled` in one edge case)

**Result:** Despite the existing `CONFIGURATION_SETTINGS.md` labeling some settings as `[WARM]`,
**no parameter is truly hot-reloadable today** without code changes.

---

## Parameter Classification

The parameters below are classified into three categories based on what would be required to make
them live-updatable:

### Category 1: Easy to Make Live-Updatable

These parameters are read from `_options` on each request or operation. If the options object were
updated in-place (or if consumers were changed to use `IOptionsMonitor<T>.CurrentValue`), these
would immediately take effect on the next request without any structural changes.

**What's needed:** A configuration reload mechanism (e.g., admin API endpoint, file watcher, or
`IOptionsMonitor` with a custom `IOptionsChangeTokenSource`) plus either:
- (a) Mutating the singleton BackendOptions properties in-place (simplest for Object A consumers), or
- (b) Switching consumers from `IOptions<T>` to `IOptionsMonitor<T>` (proper .NET pattern)

#### Logging Parameters
| Parameter | Property | Read Location | Notes |
|-----------|----------|--------------|-------|
| LogConsole | `LogConsole` | EventDataBuilder, per-event | Simple boolean check |
| LogConsoleEvent | `LogConsoleEvent` | EventDataBuilder, per-event | Simple boolean check |
| LogPoller | `LogPoller` | Backends, per-poll-cycle | Simple boolean check |
| LogProbes | `LogProbes` | ProbeServer, per-probe | Simple boolean check |
| LogHeaders | `LogHeaders` | EventDataBuilder, per-event | List comparison |
| LogAllRequestHeaders | `LogAllRequestHeaders` | EventDataBuilder, per-event | Simple boolean check |
| LogAllRequestHeadersExcept | `LogAllRequestHeadersExcept` | EventDataBuilder, per-event | List filtering |
| LogAllResponseHeaders | `LogAllResponseHeaders` | EventDataBuilder, per-event | Simple boolean check |
| LogAllResponseHeadersExcept | `LogAllResponseHeadersExcept` | EventDataBuilder, per-event | List filtering |

#### Request Processing Parameters
| Parameter | Property | Read Location | Notes |
|-----------|----------|--------------|-------|
| MaxAttempts | `MaxAttempts` | ProxyWorker, per-request | Retry count cap |
| TimeoutHeader | `TimeoutHeader` | ProxyWorker, cached at worker construction but used per-request | Header name lookup |
| TTLHeader | `TTLHeader` | RequestData/AsyncFeeder, per-request | Header name lookup |
| DefaultTTLSecs | `DefaultTTLSecs` | RequestData/AsyncFeeder, per-request | Default fallback |
| RequiredHeaders | `RequiredHeaders` | Server, per-request validation | List comparison |
| DisallowedHeaders | `DisallowedHeaders` | Server, per-request validation | List comparison |
| StripRequestHeaders | `StripRequestHeaders` | ProxyWorker, per-request | Header removal |
| StripResponseHeaders | `StripResponseHeaders` | ProxyWorker, per-request | Header removal |
| DependancyHeaders | `DependancyHeaders` | ProxyWorker, cached at worker construction | Array reference |
| AcceptableStatusCodes | `AcceptableStatusCodes` | CircuitBreaker, per-response check | ⚠️ Also used in CB (see Category 3) |

#### Priority Parameters (per-request reads)
| Parameter | Property | Read Location | Notes |
|-----------|----------|--------------|-------|
| DefaultPriority | `DefaultPriority` | Server, per-request | Simple integer |
| PriorityKeyHeader | `PriorityKeyHeader` | Server, per-request | Header name |
| PriorityKeys | `PriorityKeys` | Server, per-request | List lookup |
| PriorityValues | `PriorityValues` | Server, per-request | List lookup |

#### User Parameters (per-request reads)
| Parameter | Property | Read Location | Notes |
|-----------|----------|--------------|-------|
| UserIDFieldName | `UserIDFieldName` | UserProfile constructor (cached) | Used for header lookup |
| UserProfileHeader | `UserProfileHeader` | ProxyWorker, per-request | Header name |
| UserPriorityThreshold | `UserPriorityThreshold` | UserPriority constructor (cached) | Float threshold |
| UniqueUserHeaders | `UniqueUserHeaders` | Server, per-request | Header list |
| UserConfigUrl | `UserConfigUrl` | UserProfile background task, per-refresh | URL read on each cycle |
| SuspendedUserConfigUrl | `SuspendedUserConfigUrl` | UserProfile background task, per-refresh | URL read on each cycle |

#### Async Per-Request Parameters
| Parameter | Property | Read Location | Notes |
|-----------|----------|--------------|-------|
| AsyncTimeout | `AsyncTimeout` | AsyncWorker, per-request | Timeout value |
| AsyncTTLSecs | `AsyncTTLSecs` | AsyncFeeder, per-request | TTL value |
| AsyncTriggerTimeout | `AsyncTriggerTimeout` | ProxyWorker, per-request | Threshold comparison |
| AsyncClientRequestHeader | `AsyncClientRequestHeader` | RequestLifecycleManager, per-request | Header name |
| AsyncClientConfigFieldName | `AsyncClientConfigFieldName` | ProxyWorker, per-request | Config field name |

#### Validation Parameters
| Parameter | Property | Read Location | Notes |
|-----------|----------|--------------|-------|
| ValidateHeaders | `ValidateHeaders` | Server, per-request | Dict lookup |
| ValidateAuthAppID | `ValidateAuthAppID` | Server, per-request | Boolean gate |
| ValidateAuthAppIDUrl | `ValidateAuthAppIDUrl` | UserProfile background refresh | URL for auth config |
| ValidateAuthAppFieldName | `ValidateAuthAppFieldName` | Server, per-request | Field name |
| ValidateAuthAppIDHeader | `ValidateAuthAppIDHeader` | Server, per-request | Header name |

#### Server Metadata (informational only)
| Parameter | Property | Read Location | Notes |
|-----------|----------|--------------|-------|
| IDStr | `IDStr` | Multiple, per-request | String prefix |
| ContainerApp | `ContainerApp` | EventDataBuilder, per-event | Metadata string |
| Revision | `Revision` | EventDataBuilder, per-event | Metadata string |

---

### Category 2: Requires Draining In-Flight Requests

These parameters affect shared state or external connections. Changing them while requests are
in-flight could cause data loss, routing errors, or inconsistent behavior. The recommended approach
is to:
1. Stop accepting new requests (drain)
2. Wait for in-flight requests to complete
3. Apply the configuration change
4. Resume accepting requests

#### Load Balancing
| Parameter | Property | Reason | Reinitialization Required |
|-----------|----------|--------|--------------------------|
| LoadBalanceMode | `LoadBalanceMode` | Changes host selection algorithm mid-flight | Recreate backend iterators |
| IterationMode | `IterationMode` | Changes iteration strategy for active requests | Recreate backend iterators |
| UseSharedIterators | `UseSharedIterators` | Switching between shared/isolated iterators with active state | Recreate SharedIteratorRegistry |

#### Backend Hosts
| Parameter | Property | Reason | Reinitialization Required |
|-----------|----------|--------|--------------------------|
| Hosts | `Hosts` (list) | Changing backends during active routing | Rebuild Backends host list, restart health probes |

#### OAuth
| Parameter | Property | Reason | Reinitialization Required |
|-----------|----------|--------|--------------------------|
| UseOAuth | `UseOAuth` | Toggling auth mid-flight causes 401s | Reinitialize BackendTokenProvider |
| UseOAuthGov | `UseOAuthGov` | Endpoint switch requires new credentials | Reinitialize BackendTokenProvider |
| OAuthAudience | `OAuthAudience` | Token audience change invalidates cached tokens | Flush token cache |

#### Async Infrastructure Connections
| Parameter | Property | Reason | Reinitialization Required |
|-----------|----------|--------|--------------------------|
| AsyncModeEnabled | `AsyncModeEnabled` | Mode switch with pending async operations | Drain async queues, stop/start ServiceBus processor |
| AsyncBlobStorageConnectionString | `AsyncBlobStorageConnectionString` | Connection change with pending blob writes | Drain BlobWriteQueue, recreate BlobServiceClient |
| AsyncBlobStorageUseMI | `AsyncBlobStorageUseMI` | Auth method switch with pending writes | Drain BlobWriteQueue, recreate BlobServiceClient |
| AsyncBlobStorageAccountUri | `AsyncBlobStorageAccountUri` | Endpoint change with pending writes | Drain BlobWriteQueue, recreate BlobServiceClient |
| AsyncSBConnectionString | `AsyncSBConnectionString` | Connection change with pending messages | Drain ServiceBus senders, recreate ServiceBusClient |
| AsyncSBQueue | `AsyncSBQueue` | Queue change with pending messages | Stop processor, recreate on new queue |
| AsyncSBUseMI | `AsyncSBUseMI` | Auth method change | Recreate ServiceBusClient |
| AsyncSBNamespace | `AsyncSBNamespace` | Namespace change | Recreate ServiceBusClient |

#### Queue & Workers
| Parameter | Property | Reason | Reinitialization Required |
|-----------|----------|--------|--------------------------|
| Workers | `Workers` | Fixed task count created at startup | Stop worker tasks, create new ProxyWorkerCollection |
| PriorityWorkers | `PriorityWorkers` | Worker-to-priority mapping fixed at startup | Same as Workers |
| MaxQueueLength | `MaxQueueLength` | Queue boundary check; resizing with pending items is unsafe | Drain queue, recreate ConcurrentPriQueue |
| Port | `Port` | HttpListener bound to port at startup | Stop HttpListener, rebind |

#### Storage
| Parameter | Property | Reason | Reinitialization Required |
|-----------|----------|--------|--------------------------|
| StorageDbEnabled | `StorageDbEnabled` | Toggling with pending writes | Drain storage queue |
| StorageDbContainerName | `StorageDbContainerName` | Container switch with pending writes | Drain storage queue |

---

### Category 3: Requires Full Restart (Cold Start)

These parameters configure infrastructure that is created once at application startup and cannot be
changed without destroying and recreating the entire component.

#### HTTP Client & Connection Pool
| Parameter | Property | Why Restart Required |
|-----------|----------|---------------------|
| EnableMultipleHttp2Connections | `EnableMultipleHttp2Connections` | `SocketsHttpHandler` configured at startup; connection pool is immutable |
| MultiConnLifetimeSecs | `MultiConnLifetimeSecs` | `PooledConnectionLifetime` set on handler |
| MultiConnIdleTimeoutSecs | `MultiConnIdleTimeoutSecs` | `PooledConnectionIdleTimeout` set on handler |
| MultiConnMaxConns | `MultiConnMaxConns` | `MaxConnectionsPerServer` set on handler |
| KeepAliveInitialDelaySecs | `KeepAliveInitialDelaySecs` | TCP socket options set per-connection in `ConnectCallback` |
| KeepAlivePingIntervalSecs | `KeepAlivePingIntervalSecs` | TCP socket options set per-connection |
| KeepAliveIdleTimeoutSecs | `KeepAliveIdleTimeoutSecs` | `PooledConnectionIdleTimeout` set on handler |
| IgnoreSSLCert | `IgnoreSSLCert` | `SslOptions.RemoteCertificateValidationCallback` set on handler |
| DnsRefreshTimeout | `DnsRefreshTimeout` | Affects DNS lookup caching, configured at process level |
| Timeout | `Timeout` | `HttpClient.Timeout` set once at startup |

#### Circuit Breaker
| Parameter | Property | Why Restart Required |
|-----------|----------|---------------------|
| CircuitBreakerErrorThreshold | `CircuitBreakerErrorThreshold` | Stored in readonly fields per `CircuitBreaker` instance (one per host) |
| CircuitBreakerTimeslice | `CircuitBreakerTimeslice` | Stored in readonly field, drives sampling window size |

#### Health Probing
| Parameter | Property | Why Restart Required |
|-----------|----------|---------------------|
| PollInterval | `PollInterval` | Timer period set at ProbeServer construction |
| PollTimeout | `PollTimeout` | Timeout value cached at construction |
| SuccessRate | `SuccessRate` | Threshold cached in HostHealth at construction |
| HealthProbeSidecar | `HealthProbeSidecar` | Sidecar configuration parsed at startup |
| HealthProbeSidecarEnabled | `HealthProbeSidecarEnabled` | Feature flag read at startup |
| HealthProbeSidecarUrl | `HealthProbeSidecarUrl` | URL cached at startup |

#### Async Worker Infrastructure
| Parameter | Property | Why Restart Required |
|-----------|----------|---------------------|
| AsyncBlobWorkerCount | `AsyncBlobWorkerCount` | Channel<T> worker count fixed at BlobWriteQueue construction; channels cannot be added/removed |

#### Shared Iterator Infrastructure
| Parameter | Property | Why Restart Required |
|-----------|----------|---------------------|
| SharedIteratorTTLSeconds | `SharedIteratorTTLSeconds` | Stored as `TimeSpan` field in SharedIteratorRegistry |
| SharedIteratorCleanupIntervalSeconds | `SharedIteratorCleanupIntervalSeconds` | Timer period set at construction |

#### Server Lifecycle
| Parameter | Property | Why Restart Required |
|-----------|----------|---------------------|
| TerminationGracePeriodSeconds | `TerminationGracePeriodSeconds` | Read once in CoordinatedShutdownService |
| TrackWorkers | `TrackWorkers` | Read at HealthCheckService construction |

#### User Profile Infrastructure
| Parameter | Property | Why Restart Required |
|-----------|----------|---------------------|
| UseProfiles | `UseProfiles` | Feature gate checked at startup to register services |
| UserConfigRequired | `UserConfigRequired` | Strict mode flag read at startup |
| UserConfigRefreshIntervalSecs | `UserConfigRefreshIntervalSecs` | ⚠️ Actually read each cycle in UserProfile loop — could be WARM if mutable |
| UserSoftDeleteTTLMinutes | `UserSoftDeleteTTLMinutes` | Cached as `TimeSpan` in UserProfile constructor |

#### Miscellaneous
| Parameter | Property | Why Restart Required |
|-----------|----------|---------------------|
| HostName | `HostName` | Used as response header value, cached at multiple construction points |
| RequestIDPrefix | (via IDStr) | Used in request ID generation |

---

## Proposals for Enabling Live Configuration Updates

### Proposal 1: In-Place Singleton Mutation (Minimal Change)

**Approach:** Add an admin API endpoint (e.g., `POST /admin/config`) that deserializes updated
configuration and mutates the singleton `BackendOptions` object's properties in-place.

**Pros:**
- Minimal code changes — all consumers already hold a reference to the same object (Object A)
- Category 1 parameters immediately become live-updatable
- No DI registration changes needed

**Cons:**
- Not thread-safe for collection properties (lists, dictionaries) without additional synchronization
- `IOptions<BackendOptions>` consumers receive Object B (the copy), NOT Object A — they would NOT
  see changes
- Violates .NET Options pattern conventions
- No change notification mechanism

**Scope of changes:**
- Add admin controller/endpoint for config updates
- Switch ~20 `IOptions<BackendOptions>` consumers to inject `BackendOptions` directly
  (or apply Proposal 2)
- Add `volatile` or `Interlocked` access for frequently-read properties
- Use immutable snapshot patterns for collection properties (swap entire list reference atomically)

### Proposal 2: IOptionsMonitor with Custom Change Source (Proper .NET Pattern)

**Approach:** Implement `IOptionsChangeTokenSource<BackendOptions>` backed by an admin endpoint,
file watcher, or external configuration store (e.g., Azure App Configuration).

**Pros:**
- Follows .NET best practices for configuration reload
- `IOptionsMonitor<T>.CurrentValue` always returns the latest values
- Change notifications allow components to react (e.g., rebuild iterators)
- Already used by `BlobWriterFactory` and `ServiceBusFactory`

**Cons:**
- Requires changing ~20+ constructor signatures from `IOptions<T>` to `IOptionsMonitor<T>`
- Each property read changes from `_options.X` to `_optionsMonitor.CurrentValue.X`
  (minor performance overhead from indirection)
- Category 2 and 3 parameters still require reinitialization logic

**Scope of changes:**
- Implement custom `IOptionsChangeTokenSource<BackendOptions>` (e.g., `AdminConfigChangeTokenSource`)
- Change all `IOptions<BackendOptions>` injections to `IOptionsMonitor<BackendOptions>`
- Replace `_options = options.Value` with `_optionsMonitor = optionsMonitor`
- Replace `_options.X` reads with `_optionsMonitor.CurrentValue.X`
- Register `OnChange` callbacks for Category 2 parameters that need reinitialization

### Proposal 3: Hybrid Approach (Recommended)

**Approach:** Combine Proposals 1 and 2 strategically:

1. **For Category 1 (Easy) parameters:** Switch consumers to `IOptionsMonitor<T>` and implement a
   simple config reload endpoint. These parameters take effect immediately.

2. **For Category 2 (Drain) parameters:** Register `IOptionsMonitor.OnChange` callbacks that:
   - Set a "draining" flag to stop accepting new requests
   - Wait for in-flight request count to reach zero
   - Apply the infrastructure change (recreate connections, iterators, etc.)
   - Resume accepting requests

3. **For Category 3 (Cold) parameters:** Document these as requiring container restart. Consider
   using a rolling update strategy (e.g., Kubernetes rolling deployment) where a new container with
   updated configuration replaces the old one.

**Implementation Priority:**

| Phase | Parameters | Effort | Impact |
|-------|-----------|--------|--------|
| Phase 1 | Logging flags (LogConsole, LogHeaders, etc.) | Low | High — immediate debugging capability |
| Phase 2 | Request processing (MaxAttempts, StripHeaders, TTL) | Low | Medium — tune behavior without restart |
| Phase 3 | Priority configuration | Low | Medium — adjust fairness without restart |
| Phase 4 | User profile URLs | Already works | Already refreshes on timer |
| Phase 5 | Load balancing mode, backend hosts | Medium | High — requires drain logic |
| Phase 6 | OAuth, async connections | High | Medium — requires connection management |
| Phase 7 | HTTP client, circuit breaker | Very High | Low — rolling restart is acceptable |

---

## Appendix A: DI Registration Details

### Object A vs Object B

```csharp
// In BackendHostConfigurationExtensions.AddBackendHostConfiguration():

// Object A: The original BackendOptions instance
services.AddSingleton(backendOptions);

// Object B: A COPY created by Configure<T>
services.Configure<BackendOptions>(opt => {
    foreach (var prop in typeof(BackendOptions).GetProperties()) {
        if (prop.CanWrite && prop.CanRead)
            prop.SetValue(opt, prop.GetValue(backendOptions));
    }
});
```

**Consumers of Object A** (direct `BackendOptions` injection):
- `UserProfile` — receives Object A, reads `UserConfigUrl`, `SuspendedUserConfigUrl` on each timer cycle
- `ProxyWorker` — receives Object A via ProxyWorkerCollection (which caches `.Value` = Object B)

**Wait — correction:** `ProxyWorkerCollection` injects `IOptions<BackendOptions>` and caches `.Value` (Object B),
then passes Object B to `ProxyWorker`. So ProxyWorker actually uses **Object B**.

**Consumers of Object B** (via `IOptions<BackendOptions>.Value`):
- All 20+ services listed in the IOptions column above

**Consumers of IOptionsMonitor** (could receive updated values if reload source existed):
- `BlobWriterFactory`
- `ServiceBusFactory`

### Special Case: UserProfile

`UserProfile` is the only service that receives Object A directly AND runs a background refresh loop.
Because it reads `_options.UserConfigUrl` and `_options.SuspendedUserConfigUrl` on each refresh cycle,
if Object A's properties were mutated in-place, the next refresh would pick up the new URLs automatically.
This is the closest thing to a live-updatable parameter in the current codebase.

### Special Case: AsyncModeEnabled Runtime Mutation

In `AsyncWorkerFactory.cs`, if blob writer initialization fails:
```csharp
_backendOptions.AsyncModeEnabled = false;
```
This mutates Object B (received via `IOptions<BackendOptions>.Value`). However, since Object B is shared
by reference among all `IOptions<BackendOptions>` consumers, this change IS visible to all of them.
This is the only runtime mutation that occurs today.

---

## Appendix B: Complete Parameter Index

| # | Property | Env Variable | Category | Current Injection |
|---|----------|-------------|----------|-------------------|
| 1 | `AcceptableStatusCodes` | `AcceptableStatusCodes` | 1 (Easy) / 3 (CB) | IOptions |
| 2 | `AsyncBlobStorageConnectionString` | `AsyncBlobStorageConnectionString` | 2 (Drain) | IOptions |
| 3 | `AsyncBlobStorageUseMI` | `AsyncBlobStorageUseMI` | 2 (Drain) | IOptionsMonitor |
| 4 | `AsyncBlobStorageAccountUri` | `AsyncBlobStorageAccountUri` | 2 (Drain) | IOptionsMonitor |
| 5 | `AsyncBlobWorkerCount` | `AsyncBlobWorkerCount` | 3 (Cold) | IOptions |
| 6 | `AsyncClientRequestHeader` | `AsyncClientRequestHeader` | 1 (Easy) | IOptions |
| 7 | `AsyncClientConfigFieldName` | `AsyncClientConfigFieldName` | 1 (Easy) | IOptions |
| 8 | `AsyncModeEnabled` | `AsyncModeEnabled` | 2 (Drain) | IOptions + IOptionsMonitor |
| 9 | `AsyncSBConnectionString` | `AsyncSBConnectionString` | 2 (Drain) | IOptionsMonitor |
| 10 | `AsyncSBQueue` | `AsyncSBQueue` | 2 (Drain) | IOptionsMonitor |
| 11 | `AsyncSBUseMI` | `AsyncSBUseMI` | 2 (Drain) | IOptionsMonitor |
| 12 | `AsyncSBNamespace` | `AsyncSBNamespace` | 2 (Drain) | IOptionsMonitor |
| 13 | `AsyncTimeout` | `AsyncTimeout` | 1 (Easy) | IOptions |
| 14 | `AsyncTTLSecs` | `AsyncTTLSecs` | 1 (Easy) | IOptions |
| 15 | `AsyncTriggerTimeout` | `AsyncTriggerTimeout` | 1 (Easy) | IOptions |
| 16 | `CircuitBreakerErrorThreshold` | `CBErrorThreshold` | 3 (Cold) | IOptions |
| 17 | `CircuitBreakerTimeslice` | `CBTimeslice` | 3 (Cold) | IOptions |
| 18 | `Client` | (programmatic) | 3 (Cold) | IOptions |
| 19 | `ContainerApp` | `CONTAINER_APP_NAME` | 1 (Easy) | IOptions |
| 20 | `DefaultPriority` | `DefaultPriority` | 1 (Easy) | IOptions |
| 21 | `DefaultTTLSecs` | `DefaultTTLSecs` | 1 (Easy) | IOptions |
| 22 | `DependancyHeaders` | `DependancyHeaders` | 1 (Easy) | IOptions |
| 23 | `DisallowedHeaders` | `DisallowedHeaders` | 1 (Easy) | IOptions |
| 24 | `EnableMultipleHttp2Connections` | `EnableMultipleHttp2Connections` | 3 (Cold) | N/A (startup) |
| 25 | `HealthProbeSidecar` | `HealthProbeSidecar` | 3 (Cold) | IOptions |
| 26 | `HealthProbeSidecarEnabled` | (parsed) | 3 (Cold) | IOptions |
| 27 | `HealthProbeSidecarUrl` | (parsed) | 3 (Cold) | IOptions |
| 28 | `HostName` | `HostName` | 3 (Cold) | IOptions |
| 29 | `Hosts` | `Host1..Host9` | 2 (Drain) | IOptions |
| 30 | `IDStr` | `RequestIDPrefix` | 1 (Easy) | IOptions |
| 31 | `IgnoreSSLCert` | `IgnoreSSLCert` | 3 (Cold) | N/A (startup) |
| 32 | `IterationMode` | `IterationMode` | 2 (Drain) | IOptions |
| 33 | `KeepAliveIdleTimeoutSecs` | `KeepAliveIdleTimeoutSecs` | 3 (Cold) | N/A (startup) |
| 34 | `KeepAliveInitialDelaySecs` | `KeepAliveInitialDelaySecs` | 3 (Cold) | N/A (startup) |
| 35 | `KeepAlivePingIntervalSecs` | `KeepAlivePingIntervalSecs` | 3 (Cold) | N/A (startup) |
| 36 | `LoadBalanceMode` | `LoadBalanceMode` | 2 (Drain) | IOptions |
| 37 | `LogAllRequestHeaders` | `LogAllRequestHeaders` | 1 (Easy) | IOptions |
| 38 | `LogAllRequestHeadersExcept` | `LogAllRequestHeadersExcept` | 1 (Easy) | IOptions |
| 39 | `LogAllResponseHeaders` | `LogAllResponseHeaders` | 1 (Easy) | IOptions |
| 40 | `LogAllResponseHeadersExcept` | `LogAllResponseHeadersExcept` | 1 (Easy) | IOptions |
| 41 | `LogConsole` | `LogConsole` | 1 (Easy) | IOptions |
| 42 | `LogConsoleEvent` | `LogConsoleEvent` | 1 (Easy) | IOptions |
| 43 | `LogHeaders` | `LogHeaders` | 1 (Easy) | IOptions |
| 44 | `LogPoller` | `LogPoller` | 1 (Easy) | IOptions |
| 45 | `LogProbes` | `LogProbes` | 1 (Easy) | IOptions |
| 46 | `MaxAttempts` | `MaxAttempts` | 1 (Easy) | IOptions |
| 47 | `MaxQueueLength` | `MaxQueueLength` | 2 (Drain) | IOptions |
| 48 | `MultiConnIdleTimeoutSecs` | `MultiConnIdleTimeoutSecs` | 3 (Cold) | N/A (startup) |
| 49 | `MultiConnLifetimeSecs` | `MultiConnLifetimeSecs` | 3 (Cold) | N/A (startup) |
| 50 | `MultiConnMaxConns` | `MultiConnMaxConns` | 3 (Cold) | N/A (startup) |
| 51 | `OAuthAudience` | `OAuthAudience` | 2 (Drain) | IOptions |
| 52 | `PollInterval` | `PollInterval` | 3 (Cold) | IOptions |
| 53 | `PollTimeout` | `PollTimeout` | 3 (Cold) | IOptions |
| 54 | `Port` | `Port` | 2 (Drain) | IOptions |
| 55 | `PriorityKeyHeader` | `PriorityKeyHeader` | 1 (Easy) | IOptions |
| 56 | `PriorityKeys` | `PriorityKeys` | 1 (Easy) | IOptions |
| 57 | `PriorityValues` | `PriorityValues` | 1 (Easy) | IOptions |
| 58 | `PriorityWorkers` | `PriorityWorkers` | 2 (Drain) | IOptions |
| 59 | `RequiredHeaders` | `RequiredHeaders` | 1 (Easy) | IOptions |
| 60 | `Revision` | `CONTAINER_APP_REVISION` | 1 (Easy) | IOptions |
| 61 | `SharedIteratorCleanupIntervalSeconds` | `SharedIteratorCleanupIntervalSeconds` | 3 (Cold) | IOptions |
| 62 | `SharedIteratorTTLSeconds` | `SharedIteratorTTLSeconds` | 3 (Cold) | IOptions |
| 63 | `StorageDbContainerName` | `StorageDbContainerName` | 2 (Drain) | IOptions |
| 64 | `StorageDbEnabled` | `StorageDbEnabled` | 2 (Drain) | IOptions |
| 65 | `StripRequestHeaders` | `StripRequestHeaders` | 1 (Easy) | IOptions |
| 66 | `StripResponseHeaders` | `StripResponseHeaders` | 1 (Easy) | IOptions |
| 67 | `SuccessRate` | `SuccessRate` | 3 (Cold) | IOptions |
| 68 | `SuspendedUserConfigUrl` | `SuspendedUserConfigUrl` | 1 (Easy) | Direct singleton |
| 69 | `TerminationGracePeriodSeconds` | `TERMINATION_GRACE_PERIOD_SECONDS` | 3 (Cold) | IOptions |
| 70 | `Timeout` | `Timeout` | 3 (Cold) | IOptions |
| 71 | `TrackWorkers` | `TrackWorkers` | 3 (Cold) | IOptions |
| 72 | `TTLHeader` | `TTLHeader` | 1 (Easy) | IOptions |
| 73 | `UniqueUserHeaders` | `UniqueUserHeaders` | 1 (Easy) | IOptions |
| 74 | `UseOAuth` | `UseOAuth` | 2 (Drain) | IOptions |
| 75 | `UseOAuthGov` | `UseOAuthGov` | 2 (Drain) | IOptions |
| 76 | `UseProfiles` | `UseProfiles` | 3 (Cold) | IOptions |
| 77 | `UseSharedIterators` | `UseSharedIterators` | 2 (Drain) | IOptions |
| 78 | `UserConfigRefreshIntervalSecs` | `UserConfigRefreshIntervalSecs` | 1 (Easy)* | Direct singleton |
| 79 | `UserConfigRequired` | `UserConfigRequired` | 3 (Cold) | Direct singleton |
| 80 | `UserConfigUrl` | `UserConfigUrl` | 1 (Easy) | Direct singleton |
| 81 | `UserIDFieldName` | `UserIDFieldName` | 1 (Easy) | Direct singleton |
| 82 | `UserPriorityThreshold` | `UserPriorityThreshold` | 1 (Easy) | IOptions |
| 83 | `UserProfileHeader` | `UserProfileHeader` | 1 (Easy) | IOptions |
| 84 | `UserSoftDeleteTTLMinutes` | `UserSoftDeleteTTLMinutes` | 3 (Cold) | Direct singleton |
| 85 | `ValidateAuthAppFieldName` | `ValidateAuthAppFieldName` | 1 (Easy) | IOptions |
| 86 | `ValidateAuthAppID` | `ValidateAuthAppID` | 1 (Easy) | IOptions |
| 87 | `ValidateAuthAppIDHeader` | `ValidateAuthAppIDHeader` | 1 (Easy) | IOptions |
| 88 | `ValidateAuthAppIDUrl` | `ValidateAuthAppIDUrl` | 1 (Easy) | Direct singleton |

**Summary Count:**
- **Category 1 (Easy to make live):** ~45 parameters
- **Category 2 (Requires drain):** ~20 parameters
- **Category 3 (Requires restart):** ~23 parameters
