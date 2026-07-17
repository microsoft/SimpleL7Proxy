# SimpleL7Proxy — Glossary

| | |
|---|---|
| **Version** | 1.1 |
| **Last Updated** | 2026-07-16 |
| **Owner** | Platform Engineering |
| **Review Cycle** | Updated with each feature release |

## Summary

This glossary defines every named concept, setting, and runtime behavior used across SimpleL7Proxy documentation. Each entry is grouped by the domain it belongs to — matching the ten domains in the [Table of Contents](TABLE_OF_CONTENTS.md) and the [machine-readable taxonomy](../taxonomy/concepts.json) — and links to the authoritative reference document where that concept is fully specified.

**Who this is for:** anyone reading, writing, or reviewing SimpleL7Proxy documentation or configuration files.

## TL;DR

- Terms are grouped by domain, matching the [Table of Contents](TABLE_OF_CONTENTS.md) structure.
- Each entry links to the document where that concept is fully specified.
- Configuration setting names appear in `code` style; deprecated terms are marked explicitly.

## Scope & Applicability

| | |
|---|---|
| **In scope** | All named concepts, configuration settings, runtime behaviors, and HTTP headers documented in `SimpleL7Proxy/docs/`. |
| **Out of scope** | General Azure service terminology (App Configuration, Service Bus, Blob Storage) except where it directly intersects with proxy behavior. |
| **Dependencies** | [TABLE_OF_CONTENTS.md](TABLE_OF_CONTENTS.md) · [../taxonomy/concepts.json](../taxonomy/concepts.json) |

---

## Request Lifecycle

Concepts covering how a request moves from client ingress through the priority queue to worker dispatch.

| Term | Definition | See Also |
|------|-----------|----------|
| `DefaultPriority` | Fallback priority level assigned when the request carries no matching priority header value. | [ADVANCED_CONFIGURATION.md](ADVANCED_CONFIGURATION.md) |
| Priority Level | Integer assigned to every request. Lower integer = higher dispatch precedence in the queue. | [ADVANCED_CONFIGURATION.md](ADVANCED_CONFIGURATION.md) |
| Priority Queue | Sorted list ordered by priority level using binary-search insertion. Lower integers are dispatched first. | [ADVANCED_CONFIGURATION.md](ADVANCED_CONFIGURATION.md) |
| TTL (Time-to-Live) | Total wall-clock budget for a request covering queue wait and all retry attempts. Expiry returns 412 to the client. | [TIMEOUTS.md](TIMEOUTS.md) |
| `Workers` | Count of concurrent proxy worker threads. Cold setting — the default of 10 is for local testing only. | [ENVIRONMENT_VARIABLES.md](ENVIRONMENT_VARIABLES.md) |

---

## Backend Management

Concepts covering backend host configuration, health probing, and the selection pipeline.

| Term | Definition | See Also |
|------|-----------|----------|
| Active Pool | The set of backend hosts currently eligible to receive traffic, filtered by rolling success rate threshold. | [BACKEND_HOSTS.md](BACKEND_HOSTS.md) |
| Connection String Format | Preferred per-host configuration using a semicolon-delimited `key=value` string (e.g., `host=…;probe=…;path=…`). Supports all modern options. | [BACKEND_HOSTS.md](BACKEND_HOSTS.md) |
| Direct Mode | Backend mode where the host is always treated as healthy and no probe is ever sent. Use for serverless or on-demand backends. | [BACKEND_HOSTS.md](BACKEND_HOSTS.md) |
| Health Poller | Background loop that probes each configured host at `PollInterval` ms and tracks rolling success rate and average latency. | [BACKEND_HOSTS.md](BACKEND_HOSTS.md) |
| `IterationMode` | Controls retry breadth. `SinglePass` tries each host at most once. `MultiPass` cycles up to `MaxAttempts` total. | [LOAD_BALANCING.md](LOAD_BALANCING.md) |
| Load Balance Mode | Determines host ordering within the candidate set: `roundrobin` (even), `latency` (fastest first), or `random`. | [LOAD_BALANCING.md](LOAD_BALANCING.md) |
| Path Filter | Stage 1 of backend selection. Specific-path hosts are checked first; catch-all hosts receive requests that match no specific path. | [LOAD_BALANCING.md](LOAD_BALANCING.md) |
| Shared Iterator | A single load-balance iterator shared across all concurrent requests to the same path, enabling strict round-robin fairness. | [LOAD_BALANCING.md](LOAD_BALANCING.md) |
| Success Rate | Rolling percentage of successful probe responses for a host. Hosts that fall below `SuccessRate` leave the active pool until they recover. | [BACKEND_HOSTS.md](BACKEND_HOSTS.md) |

---

## Reliability

Concepts covering circuit breaking, retry, requeue, and the timeout model.

| Term | Definition | See Also |
|------|-----------|----------|
| `AcceptableStatusCodes` | HTTP status codes from backends that are forwarded directly to the client and not counted as circuit-breaker failures. | [CIRCUIT_BREAKER.md](CIRCUIT_BREAKER.md) |
| Auto-Recovery | The circuit breaker closes automatically once all failures age out of the sliding window. No manual action is required. | [CIRCUIT_BREAKER.md](CIRCUIT_BREAKER.md) |
| Circuit Breaker | Per-host failure counter with a sliding time window. Opens when failures reach `CBErrorThreshold`; skips that host in the selection pipeline. | [CIRCUIT_BREAKER.md](CIRCUIT_BREAKER.md) |
| Progressive Delay | Artificial per-request delay (100 – 500 ms) added as a host's failure count approaches the open threshold, slowing traffic before the circuit trips. | [CIRCUIT_BREAKER.md](CIRCUIT_BREAKER.md) |
| Requeue | Returning a request to the priority queue after host attempts are exhausted and at least one backend returned 429 with `S7PREQUEUE: true`, using the shortest eligible delay. | [LOAD_BALANCING.md](LOAD_BALANCING.md) |
| `Timeout` | Per-host-attempt window in milliseconds. Resets on each retry. Effective limit per attempt = `min(remaining TTL, Timeout)`. | [TIMEOUTS.md](TIMEOUTS.md) |

> [!NOTE]
> **Circuit breaker vs. active pool:** A host leaves the active pool when its *probe success rate* drops below `SuccessRate`. A circuit breaker opens when *live request failures* exceed `CBErrorThreshold`. Both mechanisms can remove a host independently.

---

## Request Governance

Concepts covering the validation pipeline, user profiles, and priority mapping.

| Term | Definition | See Also |
|------|-----------|----------|
| App ID Allowlist | File or URL returning permitted Entra Application IDs. Enforced at step 2 of the validation pipeline, after inbound auth. | [REQUEST_VALIDATION.md](REQUEST_VALIDATION.md) |
| Priority Mapping | Maps an incoming request header value to an internal priority integer and allocates dedicated worker threads to that tier. | [ADVANCED_CONFIGURATION.md](ADVANCED_CONFIGURATION.md) |
| Priority Workers | `PriorityLevel:WorkerCount` pairs that reserve dedicated worker threads for each priority level, ensuring high-priority traffic always has capacity. | [ADVANCED_CONFIGURATION.md](ADVANCED_CONFIGURATION.md) |
| User Profile | Per-user JSON object loaded periodically from a URL or file. Drives priority assignment, async configuration, custom header injection, and throttling. | [USER_PROFILES.md](USER_PROFILES.md) |

> [!TIP]
> User profiles reload on a configurable interval (default 1 hour) without a proxy restart. Suspend a user by adding their ID to the suspended-users list — it takes effect on the next reload cycle.

---

## Async Mode

Concepts covering long-running request handling decoupled from the HTTP connection.

| Term | Definition | See Also |
|------|-----------|----------|
| `AsyncTimeout` | Maximum backend processing time in milliseconds once a request is in async mode (default 30 minutes). | [TIMEOUTS.md](TIMEOUTS.md) |
| `AsyncTriggerTimeout` | Milliseconds elapsed since enqueue before the proxy releases the client with a 202 response and continues in the background. | [TIMEOUTS.md](TIMEOUTS.md) |
| `AsyncTTLSecs` | Request TTL in seconds applied when async processing starts, replacing the synchronous request expiration. | [AsyncOperation.md](AsyncOperation.md) |
| Blob Lifecycle Policy | External Azure Storage lifecycle management rule used to delete result blobs. The proxy does not configure or enforce deletion. | [StorageBlobConfig.md](StorageBlobConfig.md) |

> [!WARNING]
> Async mode requires three simultaneous opt-ins: the proxy-wide `AsyncModeEnabled` flag, an `async-config` block in the user profile, and the `S7PAsyncMode` header on the individual request. All three MUST be present for async upgrade to occur.

---

## Observability

Concepts covering telemetry events, sinks, token tracking, and health endpoints.

| Term | Definition | See Also |
|------|-----------|----------|
| CompositeEventClient | Fan-out dispatcher that sends every serialized `ProxyEvent` to all registered telemetry sinks simultaneously. Custom sinks register by implementing `IEventClient + IHostedService`. | [OBSERVABILITY.md](OBSERVABILITY.md) |
| ProxyEvent | Per-request key/value dictionary capturing HTTP status, queue duration, processing duration, backend host, and token counts. | [OBSERVABILITY.md](OBSERVABILITY.md) |
| Sidecar Mode | Deployment pattern where a separate HealthProbe container on port 9000 handles Kubernetes probes. The proxy pushes its health state to the sidecar every second, isolating probe responses from proxy load. | [HEALTH_CHECKING.md](HEALTH_CHECKING.md) |
| Token Telemetry | Prompt and completion token counts extracted from SSE streams in flight by the `processor=OpenAI` stream handler. Logged per request without buffering the full response. | [OBSERVABILITY.md](OBSERVABILITY.md) |

---

## Configuration Management

Concepts covering how settings reach the proxy, when they take effect, and how they are organized.

| Term | Definition | See Also |
|------|-----------|----------|
| Cold Setting | Configuration value that takes effect only after a container restart. Examples: `Workers`, `AsyncModeEnabled`. | [CONFIGURATION_SETTINGS.md](CONFIGURATION_SETTINGS.md) |
| Composite Connection String | Semicolon-delimited `key=value` string encoding multiple related settings in a single environment variable (e.g., `Host1`, `AsyncBlobStorageConfig`). | [BACKEND_HOSTS.md](BACKEND_HOSTS.md) |
| Hidden Setting | Runtime-derived value never published to Azure App Configuration. Typically computed from a composite connection string at startup. | [CONFIGURATION_SETTINGS.md](CONFIGURATION_SETTINGS.md) |
| Sentinel | The `Warm:Sentinel` key in Azure App Configuration. Updating its value to anything new triggers hot-reload of all Warm settings across all running proxy instances. | [AZURE_APP_CONFIGURATION.md](AZURE_APP_CONFIGURATION.md) |
| Warm Setting | Configuration value hot-reloaded from Azure App Configuration within ~30 seconds when the Sentinel key changes. No container restart required. | [CONFIGURATION_SETTINGS.md](CONFIGURATION_SETTINGS.md) |

> [!TIP]
> To apply a batch of Warm setting changes atomically, update all values in Azure App Configuration first, then bump the Sentinel key once. All instances reload together.

---

## Authentication and Security

Concepts covering how the proxy authenticates to backends and restricts inbound callers.

| Term | Definition | See Also |
|------|-----------|----------|
| Keyless Auth | Using `usemi=true` in a host connection string to eliminate static API keys. The proxy acquires OAuth2 Bearer tokens from Managed Identity at runtime. | [AI_FOUNDRY_INTEGRATION.md](AI_FOUNDRY_INTEGRATION.md) |
| Managed Identity | Azure-managed credential attached to the container. Used for keyless authentication to backends, App Configuration, Event Hubs, Blob Storage, and Service Bus — no secrets stored. | [BACKEND_HOSTS.md](BACKEND_HOSTS.md) |

---

## Protocol and Headers

Named HTTP signals that cross the client-proxy and proxy-backend boundaries.

| Term | Direction | Definition | See Also |
|------|-----------|-----------|----------|
| `S7PAsyncMode` | Client → proxy | Per-request opt-in header that enables async mode for that call. Default header name is configurable. | [AsyncOperation.md](AsyncOperation.md) |
| `S7PDEBUG` | Client → proxy | Set to `true` to enable per-request debug tracing in logs. | [RESPONSE_CODES.md](RESPONSE_CODES.md) |
| `S7PPriorityKey` | Client → proxy | Carries the caller's priority tier value. Mapped via `PriorityKeys` to an internal priority integer. | [ADVANCED_CONFIGURATION.md](ADVANCED_CONFIGURATION.md) |
| `S7PREQUEUE` | Backend → proxy | Response header a backend sets on a 429 reply to make the request eligible for delayed requeue after available host attempts are exhausted. | [RESPONSE_CODES.md](RESPONSE_CODES.md) |
| `S7PTimeout` | Client → proxy | Per-request override for the host-attempt timeout in milliseconds. | [TIMEOUTS.md](TIMEOUTS.md) |
| `S7PTTL` | Client → proxy | Per-request override for the total TTL budget in seconds. | [TIMEOUTS.md](TIMEOUTS.md) |

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 1.1 | 2026-07-16 | Aligned queue, validation, requeue, async TTL, lifecycle, and protocol definitions with the concept taxonomy. |
| 1.0 | 2026-05-21 | Initial gold-standard release. Reorganized by domain, added See Also links, added callouts, added protocol headers section. |