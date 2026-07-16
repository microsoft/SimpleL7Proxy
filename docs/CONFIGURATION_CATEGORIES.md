# Configuration Settings — By Frequency of Use

| Attribute | Value |
|-----------|-------|
| **Version** | 1.1 |
| **Last Updated** | 2026-05-21 |
| **Owner** | SimpleL7Proxy maintainers |
| **Review Cycle** | Quarterly |

## Summary

This document categorizes every SimpleL7Proxy configuration setting into three tiers — **Essential**, **Common**, and **Advanced** — to guide operators on which settings to address first and documentation authors on what each doc MUST cover. All Essential settings MUST be configured before any Common or Advanced settings are tuned.

> **TL;DR**
> - **Essential:** MUST be set in every deployment — the proxy will not function correctly without them.
> - **Common:** MUST be configured for typical production deployments; govern reliability and observability.
> - **Advanced:** MUST only be set when a specific capability (async, multi-tenancy, advanced auth) is explicitly required.

> [!NOTE]
> For the complete reference with Warm/Cold/Hidden reload classification and exact defaults, see [CONFIGURATION_SETTINGS.md](CONFIGURATION_SETTINGS.md). For all environment variable definitions, see [ENVIRONMENT_VARIABLES.md](ENVIRONMENT_VARIABLES.md).

---

## ESSENTIAL — Core settings required in every deployment

**These settings must be configured for the proxy to function. Most deployments set all of them.**

### Backends
| Env Var | Property | Default | Purpose |
|---------|----------|---------|---------|
| `Host1` | (Backend URL) | — | **Required.** First backend URL. Format: `protocol://host[:port][;probe=/path]` |
| `Host2`–`Host9` | (Backend URL) | — | Optional additional backends (up to 9 total) |

### Server
| Env Var | Property | Mode | Default | Purpose |
|---------|----------|------|---------|---------|
| `Port` | `Port` | Cold | `80` | Proxy listen port |
| `Workers` | `Workers` | Cold | `10` | Concurrent worker count; tune for your backend throughput |
| `MaxQueueLength` | `MaxQueueLength` | Cold | `1000` | Max queued requests before returning 429 |

### Request Handling
| Env Var | Property | Mode | Default | Purpose |
|---------|----------|------|---------|---------|
| `Timeout` | `Timeout` | Warm | `1200000` ms (20 min) | Per-host request timeout; adjust for your SLAs |
| `MaxAttempts` | `MaxAttempts` | Warm | `10` | Max retries per request |
| `DefaultPriority` | `DefaultPriority` | Warm | `2` | Base priority for requests without priority header |

---

## COMMON — Standard configuration for typical deployments

**These settings fine-tune behavior for typical use cases. Set them when initial deployment is running.**

### Request Processing
| Env Var | Property | Mode | Default | Purpose |
|---------|----------|------|---------|---------|
| `DefaultTTLSecs` | `DefaultTTLSecs` | Warm | `300` s | Request TTL; how long before queue entry expires |
| `AcceptableStatusCodes` | `AcceptableStatusCodes` | Warm | `[200,202,401,403,404,408,410,412,417,400]` | Status codes returned without retry |
| `UniqueUserHeaders` | `UniqueUserHeaders` | Warm | `["X-UserID"]` | Headers that identify a unique user for queue tracking |

### Load Balancing
| Env Var | Property | Mode | Default | Purpose |
|---------|----------|------|---------|---------|
| `LoadBalanceMode` | `LoadBalanceMode` | Warm | `latency` | `roundrobin`, `latency`, or `random` |

### Circuit Breaker
| Env Var | Property | Mode | Default | Purpose |
|---------|----------|------|---------|---------|
| `CBErrorThreshold` | `CBErrorThreshold` | Warm | `50` | Number of failures within the window that opens the circuit |
| `CBTimeslice` | `CBTimeslice` | Warm | `60` s | Sliding window width — failures older than this are discarded |
| `SuccessRate` | `SuccessRate` | Cold | `80` % | Minimum probe success rate for a host to remain in the active pool |

### Health Checking
| Env Var | Property | Mode | Default | Purpose |
|---------|----------|------|---------|---------|
| `PollInterval` | `PollInterval` | Cold | `15000` ms | Backend health check frequency |
| `PollTimeout` | `PollTimeout` | Cold | `3000` ms | Health check timeout |

### Logging (Basic)
| Env Var | Property | Mode | Default | Purpose |
|---------|----------|------|---------|---------|
| `LogAllRequestHeaders` | `LogAllRequestHeaders` | Warm | `false` | Log all inbound headers (for debugging) |
| `LogAllResponseHeaders` | `LogAllResponseHeaders` | Warm | `false` | Log all outbound headers (for debugging) |
| `LogToConsole` | `LogToConsole` | Warm | `["*","-custom"]` | Event categories written to stdout |
| `LogToEvents` | `LogToEvents` | Warm | `["async","backend","probe",...]` | Event categories written to event store |

### User Profiles (if using)
| Env Var | Property | Mode | Default | Purpose |
|---------|----------|------|---------|---------|
| `UseProfiles` | `UseProfiles` | Warm | `false` | Enable user profile enrichment |
| `UserConfigUrl` | `UserConfigUrl` | Warm | `""` | URL to user config (file: or http:) |
| `UserProfileHeader` | `UserProfileHeader` | Warm | `X-UserProfile` | Header to inject with profile data |

### Security (Basic)
| Env Var | Property | Mode | Default | Purpose |
|---------|----------|------|---------|---------|
| `IgnoreSSLCert` | `IgnoreSSLCert` | Cold | `false` | Skip TLS verification (dev/test only) |

### Async (if enabling basic async)
| Env Var | Property | Mode | Default | Purpose |
|---------|----------|------|---------|---------|
| `AsyncModeEnabled` | `AsyncModeEnabled` | Cold | `false` | Enable asynchronous request processing |

---

## ADVANCED — Specialized settings for specific scenarios

**These settings address async pipelines, multi-tenancy, advanced auth, performance tuning, or high-scale deployments. Set only when needed.**

### Async (Advanced)
| Env Var | Property | Mode | Default | Purpose |
|---------|----------|------|---------|---------|
| `AsyncBlobStorageConfig` | `AsyncBlobStorageConfig` | Cold | `""` | Blob storage connection string + auth method |
| `AsyncSBConfig` | `AsyncSBConfig` | Cold | `""` | Service Bus connection string + queue name |
| `AsyncBlobWorkerCount` | `AsyncBlobWorkerCount` | Cold | `2` | Worker threads for blob uploads |
| `AsyncTimeout` | `AsyncTimeout` | Warm | `1800000` ms (30 min) | Max backend processing time in async mode |
| `AsyncTTLSecs` | `AsyncTTLSecs` | Warm | `86400` s (24 h) | Request TTL applied when async processing starts |
| `AsyncTriggerTimeout` | `AsyncTriggerTimeout` | Warm | `10000` ms | Delay before queued request converts to async |
| `AsyncClientRequestHeader` | `AsyncClientRequestHeader` | Warm | `S7PAsyncMode` | Header clients use to enable async mode |
| `AsyncClientConfigFieldName` | `AsyncClientConfigFieldName` | Warm | `async-config` | JSON field in async client config |
| `AsyncClassNames` | `AsyncClassNames` | Cold | `""` | Comma-separated class names allowed in async requests |

### Auth / App ID Validation (if needed)
| Env Var | Property | Mode | Default | Purpose |
|---------|----------|------|---------|---------|
| `ValidateAuthAppID` | `ValidateAuthAppID` | Warm | `false` | Enable app ID validation |
| `ValidateAuthAppIDUrl` | `ValidateAuthAppIDUrl` | Warm | `""` | URL to app ID allowlist (file: or http:) |
| `ValidateAuthAppIDHeader` | `ValidateAuthAppIDHeader` | Warm | `X-MS-CLIENT-PRINCIPAL-ID` | Header containing app ID |
| `ValidateAuthAppFieldName` | `ValidateAuthAppFieldName` | Warm | `authAppID` | JSON field name in allowlist |

### User Profiles (Advanced)
| Env Var | Property | Mode | Default | Purpose |
|---------|----------|------|---------|---------|
| `UserConfigRequired` | `UserConfigRequired` | Warm | `false` | Reject requests when user config unavailable |
| `SuspendedUserConfigUrl` | `SuspendedUserConfigUrl` | Warm | `""` | URL to suspended user list (file: or http:) |
| `UserIDFieldName` | `UserIDFieldName` | Warm | `userId` | JSON field used as user identifier |
| `UserConfigRefreshIntervalSecs` | `UserConfigRefreshIntervalSecs` | Cold | `3600` s | User config reload frequency |
| `UserSoftDeleteTTLMinutes` | `UserSoftDeleteTTLMinutes` | Cold | `360` min | Soft-deleted user record TTL |

### Request Headers (Advanced)
| Env Var | Property | Mode | Default | Purpose |
|---------|----------|------|---------|---------|
| `RequiredHeaders` | `RequiredHeaders` | Warm | `[]` | Headers that must be present or request rejected |
| `DisallowedHeaders` | `DisallowedHeaders` | Warm | `[]` | Headers that must not be present |
| `StripRequestHeaders` | `StripRequestHeaders` | Warm | `[]` | Headers stripped before forwarding to backend |
| `StripResponseHeaders` | `StripResponseHeaders` | Warm | `[]` | Headers stripped from backend response |
| `ValidateHeaders` | `ValidateHeaders` | Warm | `{}` | Header name → expected value validation map |
| `LogHeaders` | `LogHeaders` | Warm | `[]` | Specific headers to log (if not using LogAll*) |
| `LogAllRequestHeadersExcept` | `LogAllRequestHeadersExcept` | Warm | `["Authorization"]` | Headers excluded from full request logging |
| `LogAllResponseHeadersExcept` | `LogAllResponseHeadersExcept` | Warm | `["Api-Key"]` | Headers excluded from full response logging |
| `DependancyHeaders` | `DependancyHeaders` | Warm | `["Backend-Host",...]` | Headers copied from response into event log |

### Priority Management (Advanced)
| Env Var | Property | Mode | Default | Purpose |
|---------|----------|------|---------|---------|
| `PriorityKeys` | `PriorityKeys` | Warm | `["high","medium","low"]` | Known priority key values |
| `PriorityValues` | `PriorityValues` | Warm | `[1,2,3]` | Priority levels assigned per key |
| `PriorityKeyHeader` | `PriorityKeyHeader` | Warm | `S7PPriorityKey` | Header clients use to pass priority key |
| `UserPriorityThreshold` | `UserPriorityThreshold` | Warm | `0.1` | Max fraction of queue a single user may occupy |

### Health Probe (Advanced)
| Env Var | Property | Mode | Default | Purpose |
|---------|----------|------|---------|---------|
| `HealthProbeSidecar` | `HealthProbeSidecar` | Warm | `Enabled=false;url=http://localhost:9000` | Sidecar health probe config |

### Load Balancing (Advanced)
| Env Var | Property | Mode | Default | Purpose |
|---------|----------|------|---------|---------|
| `IterationMode` | `IterationMode` | Warm | `SinglePass` | `SinglePass` or `MultiPass` retry mode |

### OAuth / Security (Advanced)
| Env Var | Property | Mode | Default | Purpose |
|---------|----------|------|---------|---------|
| `OAuthAudience` | `OAuthAudience` | Cold | `""` | Legacy global OAuth audience. Prefer `audience=` in `HostN` connection strings. |
| `ValidateAuthConfig` | `ValidateAuthConfig` | Warm | `enabled=false, mode=none, header=S7P-KEY` | Enable inbound key or OAuth validation and set the request header |
| `ValidateAuthKey1` | `ValidateAuthKey1` | Warm | `""` | First allowed inbound key value |
| `ValidateAuthKey2` | `ValidateAuthKey2` | Warm | `""` | Second allowed inbound key value |

Per-host auth should be configured in `HostN` connection strings using `useoauth` / `usemi`, `audience`, `api-key`, and `api-key-header`.

### Logging (Advanced)
| Env Var | Property | Mode | Default | Purpose |
|---------|----------|------|---------|---------|
| `LogToAI` | `LogToAI` | Warm | `["*"]` | Event categories sent to Application Insights |
| `APPINSIGHTS_CONNECTIONSTRING` | `AppInsightsConnectionString` | Cold | `""` | Application Insights connection string |
| `EVENTHUB_CONNECTIONSTRING` | `EventHubConnectionString` | Cold | `""` | Event Hub connection string |
| `EVENTHUB_NAME` | `EventHubName` | Cold | `""` | Event Hub name |
| `EVENTHUB_NAMESPACE` | `EventHubNamespace` | Cold | `""` | Event Hub namespace |
| `EVENTHUB_STARTUP_SECONDS` | `EventHubStartupSeconds` | Cold | `10` s | Delay before Event Hub starts sending |
| `EVENTHUB_MAX_RECONNECT_ATTEMPTS` | `EventHubMaxReconnectAttempts` | Cold | `5` | Max reconnect attempts on failure |
| `EVENTHUB_MAX_UNDRAINED_EVENTS` | `EVENTHUB_MAX_UNDRAINED_EVENTS` | Cold | `10000` | Max buffered events before blocking |
| `EVENT_LOGGERS` | `EVENT_LOGGERS` | Cold | `file` | Comma-separated list of event sinks (`file`, `eventhub`, `none`, or custom type) |
| `LOGFILE_NAME` | `LogFileName` | Cold | `eventslog.json` | Event log file path |
| `LOGDATETIME` | `LogDateTime` | Cold | `false` | Prefix log entries with timestamp |
| `LOG_LEVEL` | `LogLevel` | Hidden | `Information` | Log level (Debug, Information, Warning, Error) |
| `EVENT_HEADERS` | `EventHeaders` | Cold | `SimpleL7Proxy.Events.CommonEventHeaders` | Event data class name (for custom telemetry) |
| `ReuseEvents` | `ReuseEvents` | Cold | `false` | Reuse event objects across requests (performance optimization) |

### Server / Performance Tuning (Advanced)
| Env Var | Property | Mode | Default | Purpose |
|---------|----------|------|---------|---------|
| `GC2InternalSecs` | `GC2InternalSecs` | Cold | `300` s | Garbage collection internal cleanup interval |
| `StreamFlushInterval` | `StreamFlushInterval` | Cold | `250` ms | Interval used by StreamFlusher to flush active response streams |
| `SharedIteratorTTLSeconds` | `SharedIteratorTTLSeconds` | Cold | `60` s | TTL for an unused shared iterator |
| `SharedIteratorCleanupIntervalSeconds` | `SharedIteratorCleanupIntervalSeconds` | Cold | `30` s | Shared iterator cleanup frequency |
| `TERMINATION_GRACE_PERIOD_SECONDS` | `TerminationGracePeriodSeconds` | Cold | `30` s | Graceful shutdown drain window |

---

## Mapping: Where Each Category Appears in Docs

| Document | SHALL Discuss | Notes |
|----------|---|---------|
| [BEGINNER_DEVELOPMENT.md](BEGINNER_DEVELOPMENT.md) | Essential | Local setup uses basic config |
| [CONTAINER_DEPLOYMENT.md](CONTAINER_DEPLOYMENT.md) | Essential + Common | Initial deployment checklist |
| [AZURE_APP_CONFIGURATION.md](AZURE_APP_CONFIGURATION.md) | Essential + Common | Seed script outputs both |
| [CONFIGURATION_SETTINGS.md](CONFIGURATION_SETTINGS.md) | All three (with labels) | Complete reference — the authoritative source |
| [ADVANCED_CONFIGURATION.md](ADVANCED_CONFIGURATION.md) | Advanced only | Deep-dive: priority mapping, header validation, user throttling |
| [TroubleshootTOC.md](TroubleshootTOC.md) | Common + Advanced (context-dependent) | Circuit-breaker thresholds (Common); sliding window settings (Advanced) |
| [HEALTH_CHECKING.md](HEALTH_CHECKING.md) | Common (+ Advanced if sidecar) | `PollInterval`/`PollTimeout` are Common; sidecar config is Advanced |

---

## Validation & Compliance

| Check | Method | Expected Result |
|-------|--------|-----------------|
| Essential settings present | Inspect running container env vars | `Port`, `Host1`, `Workers`, `MaxQueueLength`, `Timeout`, `MaxAttempts`, `DefaultPriority` are set |
| Common settings documented | Review deployment runbook | All Common settings in the table above appear with tuned values |
| Advanced settings gated | Review pull request checklist | No Advanced setting is added without a documented justification |

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 1.1 | 2026-05-21 | Added metadata, TL;DR, Summary; removed internal meta-comment; fixed `BEGINNERDEVELOPMENT.md` → `BEGINNER_DEVELOPMENT.md`; added hyperlinks to all documents in mapping table; added Validation & Compliance and Version History sections | SimpleL7Proxy maintainers |
| 1.0 | — | Initial version | SimpleL7Proxy maintainers |

---

## Quick Decision Tree

**Q: What settings should I change first in a new deployment?**
→ Configure **Essential** group. Your deployment won't work without them.

**Q: The proxy is working but I want to fine-tune it.**
→ Adjust **Common** group. These directly impact throughput, latency, and reliability.

**Q: I need async requests / multi-tenancy / advanced auth.**
→ Configure **Advanced** group. Read ADVANCED_CONFIGURATION.md first.

**Q: Which settings appear in the portal (App Configuration)?**
→ All **Warm** and **Cold** settings (regardless of frequency group).
→ **Hidden** settings are env-var only.
