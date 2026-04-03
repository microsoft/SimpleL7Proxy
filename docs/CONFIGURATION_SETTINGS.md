# BackendOptions Settings - Organized by Restart Requirement

## Legend

| Tag | Description |
|-----|-------------|
| **[WARM]** | Hot-reloadable (read per-request or periodically refreshed) |
| **[DRAIN]** | Requires draining all workers (stop accepting, wait for in-flight to complete) |
| **[COLD]** | Requires cold restart (read once at startup, configures DI/infrastructure) |
| **[PARTIAL]** | Mixed - some settings WARM, others COLD/DRAIN |

---

## [WARM] Settings - Can be changed without restart

### Async (per-request settings)

| Setting | Property Name |
|---------|---------------|
| Timeout | `AsyncTimeout` |
| TTLSecs | `AsyncTTLSecs` |
| TriggerTimeout | `AsyncTriggerTimeout` |
| ClientRequestHeader | `AsyncClientRequestHeader` |
| ClientConfigFieldName | `AsyncClientConfigFieldName` |

### Logging - Read per-request or per-event

| Setting | Property Name |
|---------|---------------|
| LogToConsole | `LogToConsole` |
| LogToEvents | `LogToEvents` |
| LogToAI | `LogToAI` |
| Probes | `LogProbes` |
| Headers | `LogHeaders` |
| AllRequestHeaders | `LogAllRequestHeaders` |
| AllRequestHeadersExcept | `LogAllRequestHeadersExcept` |
| AllResponseHeaders | `LogAllResponseHeaders` |
| AllResponseHeadersExcept | `LogAllResponseHeadersExcept` |

### Request - Read per-request

| Setting | Property Name |
|---------|---------------|
| MaxAttempts | `MaxAttempts` |
| TimeoutHeader | `TimeoutHeader` |
| TTLHeader | `TTLHeader` |
| DefaultTTLSecs | `DefaultTTLSecs` |
| RequiredHeaders | `RequiredHeaders` |
| StripHeaders | `StripRequestHeaders` |
| DisallowedHeaders | `DisallowedHeaders` |
| DependencyHeaders | `DependancyHeaders` |

### Response - Read per-response

| Setting | Property Name |
|---------|---------------|
| StripHeaders | `StripResponseHeaders` |

### StatusCodes - Read per-response

| Setting | Property Name |
|---------|---------------|
| Acceptable | `AcceptableStatusCodes` |

### Validation - Read per-request

| Setting | Property Name |
|---------|---------------|
| Headers | `ValidateHeaders` |
| AuthAppID.Enabled | `ValidateAuthAppID` |
| AuthAppID.Url | `ValidateAuthAppIDUrl` |
| AuthAppID.FieldName | `ValidateAuthAppFieldName` |
| AuthAppID.Header | `ValidateAuthAppIDHeader` |

### Server (metadata only)

| Setting | Property Name |
|---------|---------------|
| IDStr | `IDStr` |
| ContainerApp | `ContainerApp` |
| Revision | `Revision` |

---

## [DRAIN] Settings - Require stopping all workers before restart

> ⚠️ These settings affect shared state, external connections, or would cause inconsistency during rolling update. Drain all in-flight requests before changing.

### Async - Switching modes or connections with in-flight requests causes data loss

| Setting | Property Name | Reason |
|---------|---------------|--------|
| Enabled | `AsyncModeEnabled` | Mode switch with in-flight requests |
| BlobStorage.ConnectionString | `AsyncBlobStorageConnectionString` | Connection change with pending writes |
| BlobStorage.UseMI | `AsyncBlobStorageUseMI` | Auth change with pending writes |
| BlobStorage.AccountUri | `AsyncBlobStorageAccountUri` | Connection change with pending writes |
| ServiceBus.ConnectionString | `AsyncSBConnectionString` | Connection change with pending messages |
| ServiceBus.Queue | `AsyncSBQueue` | Queue change with pending messages |
| ServiceBus.UseMI | `AsyncSBUseMI` | Auth change with pending messages |
| ServiceBus.Namespace | `AsyncSBNamespace` | Namespace change with pending messages |

### Hosts - Changing backends with in-flight requests causes routing errors

| Setting | Property Name | Reason |
|---------|---------------|--------|
| Hosts | `Hosts` | Backend routing changes |

### LoadBalancing - Changing strategy mid-flight causes uneven distribution

| Setting | Property Name | Reason |
|---------|---------------|--------|
| Mode | `LoadBalanceMode` | Strategy change mid-flight |
| IterationMode | `IterationMode` | Iterator behavior change |
| UseSharedIterators | `UseSharedIterators` | State inconsistency with active iterators |

### OAuth - Changing auth mid-flight causes 401s on in-flight requests

| Setting | Property Name | Reason |
|---------|---------------|--------|
| Enabled | `UseOAuth` | Auth change mid-flight |
| UseGov | `UseOAuthGov` | Endpoint change mid-flight |
| Audience | `OAuthAudience` | Token audience change |

### Server - Infrastructure changes with active queue

| Setting | Property Name | Reason |
|---------|---------------|--------|
| Port | `Port` | Listener stop required |
| Workers | `Workers` | Worker count with active queue |
| MaxQueueLength | `MaxQueueLength` | Queue resize with pending requests |

### Storage - Storage changes with pending writes = data loss

| Setting | Property Name | Reason |
|---------|---------------|--------|
| DbEnabled | `StorageDbEnabled` | Toggling with pending writes |
| DbContainerName | `StorageDbContainerName` | Container change with pending writes |

---

## [COLD] Settings - Require restart but can use rolling update

### Async

| Setting | Property Name |
|---------|---------------|
| BlobStorageConfig | `AsyncBlobStorageConfig` |
| SBConfig | `AsyncSBConfig` |
| BlobWorkerCount | `AsyncBlobWorkerCount` |

### CircuitBreaker - Configured at startup

| Setting | Property Name |
|---------|---------------|
| ErrorThreshold | `CircuitBreakerErrorThreshold` |
| Timeslice | `CircuitBreakerTimeslice` |

### HealthProbe - Timer and sidecar client created at startup

| Setting | Property Name |
|---------|---------------|
| Sidecar | `HealthProbeSidecar` |
| SidecarEnabled | `HealthProbeSidecarEnabled` |
| SidecarUrl | `HealthProbeSidecarUrl` |

### Hosts

| Setting | Property Name |
|---------|---------------|
| HostName | `HostName` |

### LoadBalancing.SharedIterator

| Setting | Property Name |
|---------|---------------|
| TTLSeconds | `SharedIteratorTTLSeconds` |
| CleanupIntervalSeconds | `SharedIteratorCleanupIntervalSeconds` |

### Polling - Poller timer configured at startup

| Setting | Property Name |
|---------|---------------|
| Interval | `PollInterval` |
| Timeout | `PollTimeout` |
| SuccessRate | `SuccessRate` |

### Request

| Setting | Property Name |
|---------|---------------|
| Timeout | `Timeout` (HttpClient timeout) |

### Security

| Setting | Property Name |
|---------|---------------|
| IgnoreSSLCert | `IgnoreSSLCert` |

### Server

| Setting | Property Name |
|---------|---------------|
| MaxEvents | `MaxEvents` |
| TerminationGracePeriodSeconds | `TerminationGracePeriodSeconds` |
| TrackWorkers | `TrackWorkers` |

### Logging - Configured at startup

| Setting | Property Name |
|---------|---------------|
| Level | `LogLevel` |
| EventLoggers | `EventLoggers` |
| EventHeaders | `EventHeaders` |
| LogFileName | `LogFileName` |
| LogDateTime | `LogDateTime` |

### EventHub - Configured at startup

| Setting | Property Name |
|---------|---------------|
| ConnectionString | `EventHubConnectionString` |
| Name | `EventHubName` |
| Namespace | `EventHubNamespace` |
| StartupSeconds | `EventHubStartupSeconds` |
| MaxReconnectAttempts | `EventHubMaxReconnectAttempts` |
| MaxUndrainedEvents | `MaxUndrainedEvents` |

---

## [PARTIAL] Settings - Mixed restart requirements

### Priority

| Setting | Property Name | Requirement |
|---------|---------------|-------------|
| Default | `DefaultPriority` | [WARM] |
| KeyHeader | `PriorityKeyHeader` | [WARM] |
| Keys | `PriorityKeys` | [WARM] |
| Values | `PriorityValues` | [WARM] |
| Workers | `PriorityWorkers` | [DRAIN] |

### User

| Setting | Property Name | Requirement |
|---------|---------------|-------------|
| IDFieldName | `UserIDFieldName` | [WARM] |
| ProfileHeader | `UserProfileHeader` | [WARM] |
| ConfigUrl | `UserConfigUrl` | [WARM] |
| PriorityThreshold | `UserPriorityThreshold` | [WARM] |
| UniqueHeaders | `UniqueUserHeaders` | [WARM] |
| SuspendedConfigUrl | `SuspendedUserConfigUrl` | [WARM] |
| UseProfiles | `UseProfiles` | [COLD] |
| UserConfigRequired | `UserConfigRequired` | [COLD] |
| UserConfigRefreshIntervalSecs | `UserConfigRefreshIntervalSecs` | [COLD] |
| UserSoftDeleteTTLMinutes | `UserSoftDeleteTTLMinutes` | [COLD] |
