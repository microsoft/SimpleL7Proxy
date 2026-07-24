# Configuration Examples

| Attribute | Value |
|-----------|-------|
| **Version** | 1.1 |
| **Last Updated** | 2026-05-21 |
| **Owner** | SimpleL7Proxy maintainers |
| **Review Cycle** | Quarterly |

## Summary

This document provides canonical, copy-paste-ready configuration blocks for common SimpleL7Proxy deployment scenarios. All examples use the connection string format for `Host1`–`Host9`. Operators MUST NOT use the legacy per-variable format (`Probe_path1`, `IP1`) in new deployments.

> **TL;DR**
> - Every scenario REQUIRES `Port` and at least one `Host1` connection string.
> - Probe paths MUST be embedded in the connection string (`host=…;probe=/health`), not as separate variables.
> - All timeout and interval values are in **milliseconds** unless the variable name ends in `Secs`.

> [!NOTE]
> For environment variable definitions and defaults, see [ENVIRONMENT_VARIABLES.md](../reference/environment-variables.md). For Warm/Cold/Hidden reload classification, see [CONFIGURATION_SETTINGS.md](../reference/configuration.md).

---

## Scope & Applicability

**In scope:** Copy-paste configuration blocks for the most common operational scenarios.
**Out of scope:** Infrastructure provisioning (see [CONTAINER_DEPLOYMENT.md](deploy-container-apps.md)); App Configuration seeding (see [AZURE_APP_CONFIGURATION.md](configure-app-configuration.md)).
**Dependencies:** [BACKEND_HOSTS.md](../reference/backend-hosts.md), [CONFIGURATION_SETTINGS.md](../reference/configuration.md).

---

## Scenario 1: Minimal Local Setup

**Purpose:** Start the proxy with the fewest possible settings.

> **Rule: Every deployment MUST set `Port` and at least one `Host1` connection string. All other settings use documented defaults.**

```bash
Port=8000
Host1="host=https://localhost:3000;probe=/health"
```

This starts the proxy on port 8000. The health poller checks `/health` every 15 s (default `PollInterval`). Requests time out after 20 min (default `Timeout`). Latency-based load balancing is active by default.

> [!TIP]
> For a mock backend, see [DUMMY_BACKEND.md](llm-simulator.md). The included null server at `test/nullserver/Python/streamserver.py` requires no external dependencies.

---

## Scenario 2: Multiple Backends with Circuit Breaker Tuning

**Purpose:** Deploy with three health-probed backends and tune the circuit breaker for faster failover.

> **Rule: `CBErrorThreshold` and `CBTimeslice` MUST be tuned together — a low threshold combined with a long window opens the circuit on normal transient errors.**

```bash
Host1="host=https://primary.example.com;probe=/health"
Host2="host=https://secondary.example.com;probe=/health"
Host3="host=https://tertiary.example.com;probe=/health"
PollInterval=5000
CBErrorThreshold=20
CBTimeslice=30
SuccessRate=95
Workers=20
MaxQueueLength=500
```

> [!NOTE]
> `PollInterval` is in milliseconds. `CBTimeslice` is in seconds. `SuccessRate` is a percentage (0–100).

> [!TIP]
> If a host opens the circuit on transient errors, add its non-failure status codes to `AcceptableStatusCodes`. See [CIRCUIT_BREAKER.md](../reference/circuit-breaker.md).

---

## Scenario 3: Request Validation and Security

**Purpose:** Reject requests missing mandatory headers and block Entra application IDs not in the allowlist before they consume queue capacity.

> **Rule: The `ValidateAuthAppID` check is validation step 2, after optional inbound key/OAuth validation. Unknown app IDs return `403` before the request enters the queue.**

```bash
DisallowedHeaders=X-Forwarded-For,X-Real-IP
RequiredHeaders=Authorization,X-Correlation-ID
ValidateAuthAppID=true
ValidateAuthAppIDUrl=file:auth.json
LogAllRequestHeaders=false
LogAllRequestHeadersExcept=Authorization,X-API-Key,Cookie
```

> [!WARNING]
> Setting `LogAllRequestHeaders=true` in production will include the `Authorization` header in logs unless it appears in `LogAllRequestHeadersExcept`. The default exclusion list covers `Authorization`.

> [!TIP]
> For the full validation execution order and wildcard path matching rules, see [REQUEST_VALIDATION.md](configure-security.md).

---

## Scenario 4: High-Throughput Production

**Purpose:** Maximize concurrent request throughput.

> **Rule: `Workers` MUST be increased in proportion to `MaxQueueLength` — a large queue with insufficient workers increases average latency without improving throughput.**

```bash
Workers=50
MaxQueueLength=2000
EnableMultipleHttp2Connections=true
MultiConnMaxConns=8000
KeepAliveIdleTimeoutSecs=1800
PollInterval=10000
Timeout=300000
```

> [!NOTE]
> `Timeout` is in milliseconds. `KeepAliveIdleTimeoutSecs` is in seconds. Set `Timeout` to match your backend SLA, not to an arbitrarily large value.

> [!TIP]
> For preset configurations for common workloads (slow backends, fast failure detection), see [ADVANCED_DEVELOPMENT.md](../contributing/development.md).

---

## Scenario 5: Async Processing for Long-Running Requests

**Purpose:** Enable async mode so the proxy returns `202 Accepted` with result URIs instead of blocking the client for the duration of a long backend call.

> **Rule: `AsyncModeEnabled=true` alone is insufficient. `AsyncBlobStorageConfig` and `AsyncSBConfig` MUST also be set, and each client MUST have an async-enabled user profile with `enabled=true` in the `async-config` field.**

```bash
AsyncModeEnabled=true
AsyncBlobStorageConfig=uri=https://<storageaccount>.blob.core.windows.net,mi=true
AsyncSBConfig=ns=<namespace>.servicebus.windows.net,q=requeststatus,mi=true
AsyncTimeout=1800000
AsyncTriggerTimeout=10000
APPINSIGHTS_CONNECTIONSTRING=<your-connection-string>
```

> [!NOTE]
> `AsyncTimeout` and `AsyncTriggerTimeout` are in milliseconds. `AsyncTTLSecs` (default 86400 s) resets the request TTL when async processing starts. Configure blob deletion separately with an Azure Storage lifecycle policy.

> [!TIP]
> For the full async configuration reference, including the `async-config` user profile field format, see [AsyncOperation.md](../concepts/async-processing.md).

---

## Scenario 6: Multi-Region Failover

**Purpose:** Distribute traffic across three regions and fail over automatically when a region becomes unhealthy.

> **Rule: Each backend MUST include a `probe` path in its connection string. Without probing, the proxy cannot detect regional failures and will continue routing requests to an unhealthy backend.**

```bash
Host1="host=https://us-east.example.com;probe=/health"
Host2="host=https://us-west.example.com;probe=/health"
Host3="host=https://eu-west.example.com;probe=/health"
LoadBalanceMode=latency
DnsRefreshTimeout=60000
PollInterval=8000
CBErrorThreshold=10
CBTimeslice=30
```

> [!TIP]
> For path-based routing (e.g., `/chat` to one region and `/embeddings` to another), use the `path=` key in each connection string. See [BACKEND_HOSTS.md](../reference/backend-hosts.md#path-based-routing).

---

## Scenario 7: Azure Container Apps Production

**Purpose:** Standard settings for a production Container Apps deployment with Application Insights.

> **Rule: `Port` MUST be `443` in Container Apps — ACA ingress terminates TLS and forwards plain HTTP to this port inside the container.**

```bash
CONTAINER_APP_NAME=simplel7proxy
Port=443
Workers=15
MaxQueueLength=500
APPINSIGHTS_CONNECTIONSTRING=<your-connection-string>
LogToConsole=*
EVENT_LOGGERS=eventhub
EVENTHUB_NAMESPACE=<your-namespace>.servicebus.windows.net
EVENTHUB_NAME=<your-eventhub>
```

> [!NOTE]
> `CONTAINER_APP_NAME`, `CONTAINER_APP_REPLICA_NAME`, and `CONTAINER_APP_REVISION` are injected automatically by the ACA environment and do not need to be set manually.

---

## Scenario 8: Local Development

**Purpose:** Minimal development configuration with verbose logging and SSL bypass for self-signed certificates.

> **Rule: `IgnoreSSLCert=true` MUST NOT be used in production — it disables TLS certificate validation entirely.**

```bash
Port=8080
Host1="host=http://localhost:3000;probe=/health"
Host2="host=http://localhost:5000;probe=/health"
LogAllRequestHeaders=true
LogAllResponseHeaders=true
LogProbes=true
LOGFILE_NAME=dev-events.json
Workers=5
MaxQueueLength=10
IgnoreSSLCert=true
```

> [!TIP]
> For local setup and IDE integration, see [Run SimpleL7Proxy Locally](../getting-started/local.md) and [Development](../contributing/development.md).

---

## Validation & Compliance

| Scenario | Verification | Expected Result |
|----------|-------------|-----------------|
| Proxy starts | `curl http://localhost:{Port}/health` | `200 OK` |
| Backend probed | Proxy startup log | Backend URL in active host list |
| Circuit breaker opens | Send `CBErrorThreshold` failures within `CBTimeslice` s | `503`; host excluded from rotation |
| Async mode | Send request with `S7PAsyncMode: true` header after `AsyncTriggerTimeout` | `202 Accepted` with blob URIs in body |
| Multi-region failover | Stop one backend host | Traffic routes to remaining hosts within `PollInterval` ms |
| Request rejected (app ID) | Send request with app ID absent from `auth.json` | `403 Forbidden` |
| Request rejected (header) | Send request missing a `RequiredHeaders` entry | `417 Expectation Failed` |

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 1.1 | 2026-05-21 | Converted from flat config dump to scenario-based structure; removed legacy `Probe_path1` format; added Rule: callouts, [!TIP]/[!WARNING]/[!NOTE], cross-links, Validation & Compliance, Version History | SimpleL7Proxy maintainers |
| 1.0 | — | Initial version | SimpleL7Proxy maintainers |
