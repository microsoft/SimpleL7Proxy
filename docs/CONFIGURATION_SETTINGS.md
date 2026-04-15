# Configuration Settings

All proxy settings live in `ProxyConfig.cs` and are sourced from environment variables, Azure App Configuration, or both.

> **TL;DR**
> - **Warm** settings are hot-reloaded from Azure App Configuration (~30 s) — no restart needed.
> - **Cold** settings are published to App Configuration but take effect only after a restart.
> - **Hidden** settings are never published — they are runtime-derived, parsed from composite strings, or infrastructure-only.

---

> **Units used in this doc:** timeout/interval values in **milliseconds** unless the property name ends in `Secs` or `Minutes` (those are seconds/minutes).

## How Settings Are Loaded

```
Startup
  │
  ├── Read ALL settings (Warm + Cold + Hidden) from env vars / App Configuration
  │
  └── Every AZURE_APPCONFIG_REFRESH_INTERVAL_SECONDS
        └── Re-apply Warm settings only (sentinel-triggered)
```

**Changing a Cold setting requires a restart; changing a Warm setting only requires bumping `Sentinel`.**

---

## Warm Settings — hot-reloaded, no restart needed

### Async

| Env Var / Config Name | Property | Default | Description |
|----------------------|----------|---------|-------------|
| `AsyncClientRequestHeader` | `AsyncClientRequestHeader` | `AsyncMode` | Request header that enables async mode |
| `AsyncClientConfigFieldName` | `AsyncClientConfigFieldName` | `async-config` | JSON field in async client config |
| `AsyncTimeout` | `AsyncTimeout` | `1800000` ms (30 min) | Max backend processing time in async mode |
| `AsyncTTLSecs` | `AsyncTTLSecs` | `86400` s (24 h) | Async result blob retention |
| `AsyncTriggerTimeout` | `AsyncTriggerTimeout` | `10000` ms (10 s) | Wait before converting a queued request to async |

### Circuit Breaker

| Env Var / Config Name | Property | Default | Description |
|----------------------|----------|---------|-------------|
| `CBErrorThreshold` | `CircuitBreakerErrorThreshold` | `50` | Error % that opens the circuit |
| `CBTimeslice` | `CircuitBreakerTimeslice` | `60` s | Rolling window for error rate calculation |

### Health Probe

| Env Var / Config Name | Property | Default | Description |
|----------------------|----------|---------|-------------|
| `HealthProbeSidecar` | `HealthProbeSidecar` | `Enabled=false;url=http://localhost:9000` | Sidecar health probe config string |

### Load Balancing

| Env Var / Config Name | Property | Default | Description |
|----------------------|----------|---------|-------------|
| `LoadBalanceMode` | `LoadBalanceMode` | `latency` | `roundrobin`, `latency`, or `random` |
| `IterationMode` | `IterationMode` | `SinglePass` | `SinglePass` or `MultiPass` |

### Logging

| Env Var / Config Name | Property | Default | Description |
|----------------------|----------|---------|-------------|
| `LogToConsole` | `LogToConsole` | `["*"]` | Event categories written to console |
| `LogToEvents` | `LogToEvents` | `["async","backend","probe",...]` | Event categories written to event store |
| `LogToAI` | `LogToAI` | `[""]` | Event categories sent to Application Insights |
| `LogHeaders` | `LogHeaders` | `[]` | Specific headers to log |
| `LogAllRequestHeaders` | `LogAllRequestHeaders` | `false` | Log all inbound headers |
| `LogAllRequestHeadersExcept` | `LogAllRequestHeadersExcept` | `["Authorization"]` | Headers excluded from full request logging |
| `LogAllResponseHeaders` | `LogAllResponseHeaders` | `false` | Log all outbound headers |
| `LogAllResponseHeadersExcept` | `LogAllResponseHeadersExcept` | `["Api-Key"]` | Headers excluded from full response logging |

### Request Processing

| Env Var / Config Name | Property | Default | Description |
|----------------------|----------|---------|-------------|
| `DefaultPriority` | `DefaultPriority` | `2` | Priority assigned when no priority header present |
| `DefaultTTLSecs` | `DefaultTTLSecs` | `300` s | Request TTL when no `S7PTTL` header present |
| `GreedyUserThreshold` | `UserPriorityThreshold` | `0.1` | Fraction of queue a single user may occupy |
| `PriorityKeys` | `PriorityKeys` | `["12345","234"]` | Known priority key values |
| `PriorityValues` | `PriorityValues` | `[1,3]` | Priority level assigned per key |
| `DefaultTimeout` | `Timeout` | `1200000` ms (20 min) | Per-host request timeout |
| `MaxAttempts` | `MaxAttempts` | `10` | Max backend attempts per request |
| `S7PTimeout` *(header name)* | `TimeoutHeader` | `S7PTimeout` | Header clients use to override per-request timeout |
| `S7PTTL` *(header name)* | `TTLHeader` | `S7PTTL` | Header clients use to override per-request TTL |
| `S7PPriorityKey` *(header name)* | `PriorityKeyHeader` | `S7PPriorityKey` | Header clients use to set priority |
| `UniqueUserHeaders` | `UniqueUserHeaders` | `["X-UserID"]` | Headers that identify a unique user |
| `RequiredHeaders` | `RequiredHeaders` | `[]` | Headers that must be present or request is rejected |
| `DisallowedHeaders` | `DisallowedHeaders` | `[]` | Headers that must not be present |
| `StripRequestHeaders` | `StripRequestHeaders` | `[]` | Headers stripped before forwarding to backend |
| `DependancyHeaders` | `DependancyHeaders` | `["Backend-Host","Status",...]` | Headers copied from backend response into event log |
| `ValidateHeaders` | `ValidateHeaders` | `{}` | Header name→expected value validation map |

### Response

| Env Var / Config Name | Property | Default | Description |
|----------------------|----------|---------|-------------|
| `AcceptableStatusCodes` | `AcceptableStatusCodes` | `[200,202,400,401,403,404,408,410,412,417]` | Status codes returned to client without retry |
| `StripResponseHeaders` | `StripResponseHeaders` | `[]` | Headers stripped from backend response |

### User Profiles & Auth Validation

| Env Var / Config Name | Property | Default | Description |
|----------------------|----------|---------|-------------|
| `UserConfigUrl` | `UserConfigUrl` | `""` | URL for user profile config (file: or http:) |
| `SuspendedUserConfigUrl` | `SuspendedUserConfigUrl` | `""` | URL for suspended user list |
| `UserIDFieldName` | `UserIDFieldName` | `userId` | JSON field used as user identifier |
| `UserProfileHeader` | `UserProfileHeader` | `X-UserProfile` | Header injected with user profile data |
| `UseProfiles` | `UseProfiles` | `false` | Enable user profile enrichment |
| `UserConfigRequired` | `UserConfigRequired` | `false` | Reject requests when user config unavailable |
| `ValidateAppIDEnabled` | `ValidateAuthAppID` | `false` | Enable app ID validation |
| `ValidateAuthAppIDUrl` | `ValidateAuthAppIDUrl` | `""` | URL for app ID allowlist |
| `ValidateAuthAppFieldName` | `ValidateAuthAppFieldName` | `authAppID` | JSON field name for app ID |
| `ValidateAuthAppIDHeader` | `ValidateAuthAppIDHeader` | `X-MS-CLIENT-PRINCIPAL-ID` | Header containing app ID to validate |

### Sentinel

| Env Var / Config Name | Property | Default | Description |
|----------------------|----------|---------|-------------|
| `Sentinel` | `Sentinel` | `""` | Update this value to trigger a Warm settings refresh |

---

## Cold Settings — restart required

### Async

| Env Var / Config Name | Property | Default | Description |
|----------------------|----------|---------|-------------|
| `AsyncModeEnabled` | `AsyncModeEnabled` | `false` | Enable asynchronous request processing |
| `AsyncBlobStorageConfig` | `AsyncBlobStorageConfig` | `uri=...,mi=true` | Blob storage composite config string |
| `AsyncSBConfig` | `AsyncSBConfig` | `cs=...,ns=...,q=requeststatus,mi=false` | Service Bus composite config string |
| `AsyncBlobWorkerCount` | `AsyncBlobWorkerCount` | `2` | Worker threads for blob upload |
| `StorageDbEnabled` | `StorageDbEnabled` | `false` | Enable blob result storage |
| `StorageDbContainerName` | `StorageDbContainerName` | `Requests` | Blob container name for async results |

### Security

| Env Var / Config Name | Property | Default | Description |
|----------------------|----------|---------|-------------|
| `IgnoreSSLCert` | `IgnoreSSLCert` | `false` | Skip TLS verification (dev only) |
| `UseOAuth` | `UseOAuth` | `false` | Enable OAuth token validation |
| `OAuthAudience` | `OAuthAudience` | `""` | Expected OAuth audience |

### Server

| Env Var / Config Name | Property | Default | Description |
|----------------------|----------|---------|-------------|
| `Port` | `Port` | `80` | Proxy listen port |
| `Workers` | `Workers` | `10` | Concurrent worker count |
| `MaxQueueLength` | `MaxQueueLength` | `1000` | Max queued requests before returning 429 |
| `PollInterval` | `PollInterval` | `15000` ms | Backend health poll interval |
| `PollTimeout` | `PollTimeout` | `3000` ms | Backend health probe timeout |
| `SuccessRate` | `SuccessRate` | `80` % | Min success rate to keep circuit closed |
| `TERMINATION_GRACE_PERIOD_SECONDS` | `TerminationGracePeriodSeconds` | `30` s | Drain window on shutdown |
| `GC2InternalSecs` | `GC2InternalSecs` | `300` s | GC2 internal cleanup interval |
| `EVENTHUB_MAX_UNDRAINED_EVENTS` | `MaxUndrainedEvents` | `100` | Max buffered events before blocking |
| `SharedIteratorTTLSeconds` | `SharedIteratorTTLSeconds` | `60` s | TTL for an unused shared iterator |
| `SharedIteratorCleanupIntervalSeconds` | `SharedIteratorCleanupIntervalSeconds` | `30` s | Shared iterator cleanup frequency |

### Logging

| Env Var / Config Name | Property | Default | Description |
|----------------------|----------|---------|-------------|
| `APPINSIGHTS_CONNECTIONSTRING` | `AppInsightsConnectionString` | `""` | Application Insights connection string |
| `EVENT_LOGGERS` | `EventLoggers` | `file` | Comma-separated list of event sinks |
| `EVENT_HEADERS` | `EventHeaders` | `SimpleL7Proxy.Events.CommonEventHeaders` | Event data class name |
| `LOGFILE_NAME` | `LogFileName` | `eventslog.json` | Event log file path |
| `LOGDATETIME` | `LogDateTime` | `false` | Prefix log entries with timestamp |
| `ReuseEvents` | `ReuseEvents` | `false` | Reuse event objects across requests |

### Event Hub

| Env Var / Config Name | Property | Default | Description |
|----------------------|----------|---------|-------------|
| `EVENTHUB_CONNECTIONSTRING` | `EventHubConnectionString` | `""` | Event Hub connection string |
| `EVENTHUB_NAME` | `EventHubName` | `""` | Event Hub name |
| `EVENTHUB_NAMESPACE` | `EventHubNamespace` | `""` | Event Hub namespace |
| `EVENTHUB_STARTUP_SECONDS` | `EventHubStartupSeconds` | `10` s | Delay before Event Hub starts sending |
| `EVENTHUB_MAX_RECONNECT_ATTEMPTS` | `EventHubMaxReconnectAttempts` | `5` | Max reconnect attempts on failure |

### User Profiles

| Env Var / Config Name | Property | Default | Description |
|----------------------|----------|---------|-------------|
| `RefreshIntervalSecs` | `UserConfigRefreshIntervalSecs` | `3600` s (1 h) | How often user config is reloaded |
| `SoftDeleteTTLMinutes` | `UserSoftDeleteTTLMinutes` | `360` min (6 h) | TTL for soft-deleted user records |

---

## Hidden Settings — not published to App Configuration

> [!NOTE]
> Hidden settings are set via environment variables only. They are never written to or read from Azure App Configuration.

### Azure App Configuration

| Env Var | Property | Default | Description |
|---------|----------|---------|-------------|
| `AZURE_APPCONFIG_ENDPOINT` | `AppConfigEndpoint` | — | App Configuration endpoint (Managed Identity auth) |
| `AZURE_APPCONFIG_CONNECTION_STRING` | `AppConfigConnectionString` | — | App Configuration connection string (dev fallback) |
| `AZURE_APPCONFIG_LABEL` | `AppConfigLabel` | — | Label filter for settings |
| `AZURE_APPCONFIG_REFRESH_INTERVAL_SECONDS` | `AppConfigRefreshIntervalSeconds` | `30` s | Sentinel poll interval |

### Security

| Env Var | Property | Default | Description |
|---------|----------|---------|-------------|
| `UseOAuthGov` | `UseOAuthGov` | `false` | Use Azure Government OAuth endpoint |

### Async — parsed from `AsyncBlobStorageConfig`

| Property | Default | Description |
|----------|---------|-------------|
| `AsyncBlobStorageConnectionString` | `example-connection-string` | Parsed blob storage connection string |
| `AsyncBlobStorageUseMI` | `true` | Use Managed Identity for blob storage |
| `AsyncBlobStorageAccountUri` | `https://mystorageaccount.blob.core.windows.net` | Blob storage account URI |

### Async — parsed from `AsyncSBConfig`

| Property | Default | Description |
|----------|---------|-------------|
| `AsyncSBConnectionString` | `example-sb-connection-string` | Parsed Service Bus connection string |
| `AsyncSBQueue` | `requeststatus` | Service Bus queue name |
| `AsyncSBUseMI` | `false` | Use Managed Identity for Service Bus |
| `AsyncSBNamespace` | `example-namespace` | Service Bus namespace |

### Logging

| Env Var | Property | Default | Description |
|---------|----------|---------|-------------|
| `LOG_LEVEL` | `LogLevel` | `Information` | Minimum log level |
| `LOGTOFILE` | `LogToFile` | `false` | Write logs to file |

### Transport / Keep-Alive

| Env Var | Property | Default | Description |
|---------|----------|---------|-------------|
| `KeepAliveInitialDelaySecs` | `KeepAliveInitialDelaySecs` | `60` s | Delay before first keep-alive probe |
| `KeepAlivePingIntervalSecs` | `KeepAlivePingIntervalSecs` | `60` s | Interval between keep-alive pings |
| `KeepAliveIdleTimeoutSecs` | `KeepAliveIdleTimeoutSecs` | `1200` s | Idle connection timeout |
| `EnableMultipleHttp2Connections` | `EnableMultipleHttp2Connections` | `false` | Allow multiple HTTP/2 connections per host |
| `MultiConnLifetimeSecs` | `MultiConnLifetimeSecs` | `3600` s | Max lifetime of a pooled connection |
| `MultiConnIdleTimeoutSecs` | `MultiConnIdleTimeoutSecs` | `300` s | Idle timeout for pooled connections |
| `MultiConnMaxConns` | `MultiConnMaxConns` | `4000` | Max connections in the pool |

### Metadata (populated by Azure Container Apps runtime)

| Env Var | Property | Default | Description |
|---------|----------|---------|-------------|
| `CONTAINER_APP_NAME` | `ContainerApp` | `ContainerAppName` | Container App name injected by ACA |
| `Hostname` | `HostName` | `""` | Host name |
| `RequestIDPrefix` | `IDStr` | `S7P` | Prefix for generated request IDs |
| `CONTAINER_APP_REPLICA_NAME` | `ReplicaName` | `""` | Replica name injected by ACA |
| `CONTAINER_APP_REVISION` | `Revision` | `revisionID` | Revision name injected by ACA |

---

## Runtime-Derived Properties

These are never set via config — the proxy computes them at startup from other settings.

| Property | Description |
|----------|-------------|
| `HealthProbeSidecarEnabled` | Parsed from `HealthProbeSidecar` |
| `HealthProbeSidecarUrl` | Parsed from `HealthProbeSidecar` |
| `Hosts` | Populated from `Host1`…`HostN` environment variables |
| `PriorityWorkers` | Worker allocation map derived from `PriorityValues` |
| `TrackWorkers` | Internal worker tracking flag |
| `UseSharedIterators` | Whether to share iterator state across concurrent requests |

---

## Related Documentation

- [AZURE_APP_CONFIGURATION.md](AZURE_APP_CONFIGURATION.md) — Setting up hot-reload with App Configuration
- [DEVELOPMENT.md](DEVELOPMENT.md) — Local dev setup and minimal required config
- [TIMEOUTS.md](TIMEOUTS.md) — How TTL, Timeout, and AsyncTimeout interact
- [LOAD_BALANCING.md](LOAD_BALANCING.md) — LoadBalanceMode, IterationMode, and retry settings
