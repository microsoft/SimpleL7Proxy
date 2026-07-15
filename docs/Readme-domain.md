# SimpleL7Proxy — Documentation by Domain

**SimpleL7Proxy** is a self-hosted, open-source Layer 7 proxy for Azure AI workloads. It sits between client applications and backend model endpoints and provides priority queuing, request governance, fault isolation, async request handling, load balancing, and per-request telemetry — all within operator-controlled infrastructure.

> **TL;DR**
> - Routes and governs AI workloads across shared backend pools
> - Prevents noisy-neighbour problems with priority queuing and per-user throttling
> - Isolates backend failures with circuit breakers and cross-host retry
> - Handles long-running AI requests with async mode (202 + result blob)
> - Emits per-request token telemetry for chargeback and cost attribution

---

## Who Should Read This

| Audience | Start here |
|----------|-----------|
| **CIO / CTO / CSO** | [What It Does & Why](#1-what-it-does--why) · [Security](#8-security) · [WAF Alignment](OVERVIEW.md#well-architected-framework-alignment) |
| **Engineering Manager** | [Architecture](#2-architecture--design) · [Deployment Architecture](#9-deployment-architecture) · [Proof-of-Concept Guides](#12-proof-of-concept-guides) |
| **Senior Developer / Architect** | [Architecture](#2-architecture--design) · [Configuration & Operations](#5-configuration--operations) · [Backend Management](#6-backend-management) · [Reliability](#7-reliability--resilience) |
| **Developer (first time)** | [Getting Started](#3-getting-started) · [Local Development](#3-getting-started) |
| **Operator / SRE** | [Observability](#10-observability) · [Troubleshooting](#11-troubleshooting) · [Health Checking](HEALTH_CHECKING.md) |

---

## Document Domains

Documents are organized by the ten functional domains defined in [`taxonomy/concepts.json`](../taxonomy/concepts.json). Each section below identifies the domain, its purpose, and the authoritative documents that cover it.

---

## 1. What It Does & Why

**Purpose:** Executive-level understanding of the proxy's value, objectives, and scope.

| Document | Description |
|----------|-------------|
| [OVERVIEW.md](OVERVIEW.md) | Authoritative overview: objectives, architecture, core concepts, workflows, constraints, and WAF alignment. **Start here.** |
| [Glossary.md](Glossary.md) | Definitions for every domain term used across all documentation. |
| [AI_FOUNDRY_INTEGRATION.md](AI_FOUNDRY_INTEGRATION.md) | How the proxy fits into Azure AI Foundry and Azure OpenAI deployments specifically. |

---

## 2. Architecture & Design

**Domain:** Request Lifecycle (`d01`) · All domains  
**Purpose:** How the proxy is structured internally and how a request moves through it end-to-end.

| Document | Description |
|----------|-------------|
| [OVERVIEW.md](OVERVIEW.md#high-level-architecture) | High-level component table and end-to-end workflows (sync, async, hot-reload). |
| [design.md](design.md) | Source-code-level walkthrough: classes, data flow, request lifecycle from `Server.cs` through `ProxyWorker.cs`. |
| [code/DISPOSAL_ARCHITECTURE.md](code/DISPOSAL_ARCHITECTURE.md) | Disposal and lifecycle management patterns within the .NET codebase. |
| [AsyncOperation.md](AsyncOperation.md) | Async engine design: how 202 upgrade, background processing, and result delivery work together. |

---

## 3. Getting Started

**Purpose:** Go from zero to a running proxy as fast as possible.

| Document | Description |
|----------|-------------|
| [QUICKSTART.md](QUICKSTART.md) | Deploy to Azure Container Apps or run locally in minutes. **Start here for first deployment.** |
| [BEGINNER_DEVELOPMENT.md](BEGINNER_DEVELOPMENT.md) | Local development setup: run from source, validate request paths, diagnose startup issues. |
| [ADVANCED_DEVELOPMENT.md](ADVANCED_DEVELOPMENT.md) | Performance tuning, worker sizing, and feature-specific development. |
| [DUMMY_BACKEND.md](DUMMY_BACKEND.md) | Mock backends for local testing without cloud deployments. |
| [SCENARIOS.md](SCENARIOS.md) | Copy-paste configurations for the most common deployment scenarios. |

---

## 4. Protocol & Headers

**Domain:** Protocol and Headers (`d10`)  
**Purpose:** The named HTTP signals that cross the client-proxy and proxy-backend boundaries.

| Document | Description |
|----------|-------------|
| [RESPONSE_CODES.md](RESPONSE_CODES.md) | All proxy-originated response codes (400, 412, 417, 429, 503), injected response headers (`x-Request-Queue-Duration`, `BackendHost`, etc.), and inbound request headers (`S7PTTL`, `S7PPriorityKey`, `S7PDEBUG`). |

**Key headers at a glance:**

| Header | Direction | Purpose |
|--------|-----------|---------|
| `S7PPriorityKey` | Client → proxy | Declare request priority tier |
| `S7PTTL` | Client → proxy | Override TTL for this request (seconds) |
| `S7PTimeout` | Client → proxy | Override per-attempt timeout (seconds) |
| `S7PAsyncMode` | Client → proxy | Request async upgrade for this request |
| `S7PDEBUG` | Client → proxy | Enable debug response headers |
| `S7PREQUEUE` | Backend → proxy | Signal the proxy to requeue a 429 |
| `x-Request-Queue-Duration` | Proxy → client | Time the request spent in the queue (ms) |
| `x-Request-Process-Duration` | Proxy → client | Time the proxy spent forwarding (ms) |
| `BackendHost` | Proxy → client | Which backend served the request |

---

## 5. Configuration & Operations

**Domain:** Configuration Management (`d07`)  
**Purpose:** How settings reach the proxy, when they take effect, and how to organize them.

| Document | Description |
|----------|-------------|
| [ENVIRONMENT_VARIABLES.md](ENVIRONMENT_VARIABLES.md) | Exhaustive reference for every environment variable. Includes minimum required configuration, async variables, and observability variables. |
| [CONFIGURATION_SETTINGS.md](CONFIGURATION_SETTINGS.md) | All settings organized by operational goal. Distinguishes Warm (live-reload) from Cold (restart required) settings. |
| [CONFIGURATION_CATEGORIES.md](CONFIGURATION_CATEGORIES.md) | Settings grouped by frequency of use: Essential / Common / Advanced. |
| [ADVANCED_CONFIGURATION.md](ADVANCED_CONFIGURATION.md) | Priority management, per-user throttling, and user governance settings. |
| [AZURE_APP_CONFIGURATION.md](AZURE_APP_CONFIGURATION.md) | Azure App Configuration setup, RBAC, and the Sentinel hot-reload pattern. |

> **Warm vs Cold:** Most runtime settings update live via Azure App Configuration (Warm). Foundational settings such as worker count require a container restart (Cold).

---

## 6. Backend Management

**Domain:** Backend Management (`d02`)  
**Purpose:** How the proxy discovers, probes, and selects backend hosts.

| Document | Description |
|----------|-------------|
| [BACKEND_HOSTS.md](BACKEND_HOSTS.md) | Backend host configuration (Host1–Host9): connection string format, path-based routing, Direct mode, IP override, health polling, Managed Identity, OAuth2, strip-prefix. |
| [LOAD_BALANCING.md](LOAD_BALANCING.md) | Load balance modes (roundrobin / latency / random), backend selection pipeline, shared iterators, retry-across-backends. |
| [AI_FOUNDRY_INTEGRATION.md](AI_FOUNDRY_INTEGRATION.md) | Azure AI Foundry and Azure OpenAI–specific configuration: keyless auth, `processor=OpenAI` for token telemetry, multi-region failover patterns. |

---

## 7. Reliability & Resilience

**Domain:** Reliability (`d03`)  
**Purpose:** Mechanisms that prevent failures from propagating to clients.

| Document | Description |
|----------|-------------|
| [CIRCUIT_BREAKER.md](CIRCUIT_BREAKER.md) | CLOSED / OPEN states, sliding window, progressive delay (50%–90% threshold), auto-recovery, global blocked check (all circuits OPEN → 503). |
| [TIMEOUTS.md](TIMEOUTS.md) | Full timeout model: TTL (total budget), per-host attempt timeout, per-request override headers, async timeout variants. |
| [HEALTH_CHECKING.md](HEALTH_CHECKING.md) | `/liveness`, `/readiness`, `/startup` endpoints; sidecar mode for isolated health probing. |

> **How they work together:** TTL bounds the total retry budget. The circuit breaker skips broken hosts without consuming retries. Retry logic advances to the next healthy host within the remaining TTL.

---

## 8. Security

**Domain:** Authentication and Security (`d08`)  
**Purpose:** How the proxy authenticates to backends and restricts inbound callers.

| Document | Description |
|----------|-------------|
| [SECURITY.md](SECURITY.md) | Microsoft security policy and responsible disclosure process. |
| [REQUEST_VALIDATION.md](REQUEST_VALIDATION.md) | Five-step validation pipeline: App ID allowlist → header stripping → user profile load → required headers → header value allowlist. |
| [USER_PROFILES.md](USER_PROFILES.md) | Per-user JSON profiles: structure, fields, refresh interval, suspension, `async-config` block. |
| [BACKEND_HOSTS.md](BACKEND_HOSTS.md#managed-identity) | Managed Identity (`usemi=true`) and OAuth2 ****** attachment for backend authentication. |
| [POC-Secure-the-proxy.md](POC-Secure-the-proxy.md) | Runnable POC: securing inbound access to the proxy. |
| [POC-security-the-apim.md](POC-security-the-apim.md) | Runnable POC: securing the APIM layer in front of the proxy. |

> **Security model:** Caller identity is validated before any request enters the queue. Headers that must not reach backends are stripped at the first pipeline step. Per-user profiles control priority, async access, and header allowlists.

---

## 9. Deployment Architecture

**Domain:** Deployment Architecture (`d09`)  
**Purpose:** How the proxy is packaged and run on Azure.

| Document | Description |
|----------|-------------|
| [CONTAINER_DEPLOYMENT.md](CONTAINER_DEPLOYMENT.md) | Azure Container Apps deployment: ACA ingress targets port 8000, backend host configuration. |
| [SIDECAR_DEPLOYMENT.md](SIDECAR_DEPLOYMENT.md) | Sidecar pattern: proxy container + HealthProbe container sharing a pod. Build and deploy scripts. |
| [AI_FOUNDRY_INTEGRATION.md](AI_FOUNDRY_INTEGRATION.md) | Azure AI Foundry / OpenAI integration patterns and multi-region topology. |
| [../deployment/README.md](../deployment/README.md) | Deployment automation scripts and parameters reference. |
| [../deployment/DAY2_OPERATIONS.md](../deployment/DAY2_OPERATIONS.md) | Post-deployment operational runbook. |
| [../APIM-Policy/readme.md](../APIM-Policy/readme.md) | APIM policy integration and reference policy. |

---

## 10. Observability

**Domain:** Observability (`d06`)  
**Purpose:** How the proxy exposes telemetry about its own operation.

| Document | Description |
|----------|-------------|
| [OBSERVABILITY.md](OBSERVABILITY.md) | ProxyEvent model, telemetry fan-out architecture, Application Insights sink, Event Hubs sink, local file sink, token telemetry from SSE streams, custom event logger extensibility. |
| [BACKEND_LOG_REFERENCE.md](BACKEND_LOG_REFERENCE.md) | Reference for every activity log entry emitted by the APIM priority-with-retry policy (`backendLog` header). |

---

## 11. Async Mode

**Domain:** Async Mode (`d05`)  
**Purpose:** Long-running request handling that decouples client wait from backend processing.

| Document | Description |
|----------|-------------|
| [AsyncOperation.md](AsyncOperation.md) | Three-level opt-in model, 202 upgrade trigger, Azure Service Bus lifecycle events, result blob URI in response body. |
| [StorageBlobConfig.md](StorageBlobConfig.md) | Blob storage retention and lifecycle policy configuration for async results. |
| [TIMEOUTS.md](TIMEOUTS.md#async-requests) | `AsyncTriggerTimeout`, `AsyncTimeout`, `AsyncTTLSecs` — how async timeouts interact. |

> **Three-level opt-in:** Proxy-wide flag + user profile `async-config` block + per-request `S7PAsyncMode` header. All three must be present.

---

## 12. Proof-of-Concept Guides

**Purpose:** Runnable, end-to-end demonstrations of specific proxy capabilities. Each guide can be completed in under 30 minutes with observable outcomes.

| Scenario | Document | Key capability demonstrated |
|----------|----------|-----------------------------|
| Azure OpenAI failover across regions | [POC-OpenAI-Failover.md](POC-OpenAI-Failover.md) | Backend failover with APIM retry |
| Failover configuration | [POC-Failover-configuration.md](POC-Failover-configuration.md) | Circuit breaker + backend selection |
| Priority-based routing | [POC-Priority-configuration.md](POC-Priority-configuration.md) | Priority queue and `acceptablePriorities` |
| Securing the proxy | [POC-Secure-the-proxy.md](POC-Secure-the-proxy.md) | App ID validation and header stripping |
| Securing APIM | [POC-security-the-apim.md](POC-security-the-apim.md) | APIM policy security hardening |
| Chargeback and token tracking | [POC-Chargeback.md](POC-Chargeback.md) | Token telemetry and per-user attribution |

---

## 13. Request Governance

**Domain:** Request Governance (`d04`)  
**Purpose:** Validation and priority rules applied before a request enters the queue.

| Document | Description |
|----------|-------------|
| [REQUEST_VALIDATION.md](REQUEST_VALIDATION.md) | Validation pipeline execution order, all five steps, scenario-based examples, response codes for each rejection. |
| [USER_PROFILES.md](USER_PROFILES.md) | Per-user configuration: priority assignment, header allowlists, async access, suspension. |
| [ADVANCED_CONFIGURATION.md](ADVANCED_CONFIGURATION.md) | Priority key/value mapping, per-user throttle (`UserPriorityThreshold`), worker partition by priority tier. |

---

## 14. Troubleshooting

**Purpose:** Symptom-first diagnosis guides. Start at the TOC, find your symptom, follow the link.

| Document | Description |
|----------|-------------|
| [TroubleshootTOC.md](TroubleshootTOC.md) | Master troubleshooting index. **Start here.** |
| [troubleshooting/requests-429.md](troubleshooting/requests-429.md) | 429 Too Many Requests: queue full vs backend throttle |
| [troubleshooting/requests-412.md](troubleshooting/requests-412.md) | 412 Precondition Failed: TTL expired in queue |
| [troubleshooting/requests-503.md](troubleshooting/requests-503.md) | 503 Service Unavailable: no active backends |
| [troubleshooting/requests-400-invalid-ttl.md](troubleshooting/requests-400-invalid-ttl.md) | 400 Bad Request: malformed TTL header |
| [troubleshooting/circuit-breaker.md](troubleshooting/circuit-breaker.md) | Circuit breaker opened unexpectedly |
| [troubleshooting/health-probes.md](troubleshooting/health-probes.md) | Liveness / readiness probe failures |
| [troubleshooting/backend-hosts.md](troubleshooting/backend-hosts.md) | Backend host configuration problems |
| [troubleshooting/async-requests.md](troubleshooting/async-requests.md) | Async requests not behaving as expected |
| [troubleshooting/async-202-never-issued.md](troubleshooting/async-202-never-issued.md) | Expected async 202 but always getting sync |
| [troubleshooting/event-hub.md](troubleshooting/event-hub.md) | Event Hub telemetry not flowing |
| [troubleshooting/app-configuration.md](troubleshooting/app-configuration.md) | Azure App Configuration not loading settings |

---

## 15. Reference

**Purpose:** Exhaustive reference material for operators and integrators.

| Document | Description |
|----------|-------------|
| [ENVIRONMENT_VARIABLES.md](ENVIRONMENT_VARIABLES.md) | All environment variables with defaults, units, reload type, and minimum required set. |
| [RESPONSE_CODES.md](RESPONSE_CODES.md) | All response codes the proxy can originate or pass through; all injected headers. |
| [Glossary.md](Glossary.md) | Canonical definitions for every domain term. |
| [TABLE_OF_CONTENTS.md](TABLE_OF_CONTENTS.md) | Machine-friendly full index linking every concept to its authoritative document. |
| [BACKEND_LOG_REFERENCE.md](BACKEND_LOG_REFERENCE.md) | APIM `backendLog` activity log entry reference. |
| [../taxonomy/concepts.json](../taxonomy/concepts.json) | Machine-readable concept graph: IDs, relationships, settings cross-references, and response code mappings. |

---

## Document–Domain Mapping

The table below maps every document to its primary taxonomy domain (`d01`–`d10`).

| Document | Primary Domain | Secondary Domain(s) |
|----------|---------------|---------------------|
| OVERVIEW.md | All | — |
| design.md | d01 Request Lifecycle | — |
| code/DISPOSAL_ARCHITECTURE.md | d01 Request Lifecycle | — |
| RESPONSE_CODES.md | d10 Protocol and Headers | d01 |
| TIMEOUTS.md | d03 Reliability | d01, d05 |
| BACKEND_HOSTS.md | d02 Backend Management | d08 |
| LOAD_BALANCING.md | d02 Backend Management | d03 |
| CIRCUIT_BREAKER.md | d03 Reliability | d02 |
| HEALTH_CHECKING.md | d03 Reliability | d09 |
| REQUEST_VALIDATION.md | d04 Request Governance | d08 |
| USER_PROFILES.md | d04 Request Governance | d08 |
| ADVANCED_CONFIGURATION.md | d04 Request Governance | d01 |
| AsyncOperation.md | d05 Async Mode | — |
| StorageBlobConfig.md | d05 Async Mode | — |
| OBSERVABILITY.md | d06 Observability | — |
| BACKEND_LOG_REFERENCE.md | d06 Observability | — |
| ENVIRONMENT_VARIABLES.md | d07 Configuration Management | All |
| CONFIGURATION_SETTINGS.md | d07 Configuration Management | — |
| CONFIGURATION_CATEGORIES.md | d07 Configuration Management | — |
| AZURE_APP_CONFIGURATION.md | d07 Configuration Management | — |
| SECURITY.md | d08 Authentication and Security | — |
| AI_FOUNDRY_INTEGRATION.md | d02 Backend Management | d08, d09 |
| CONTAINER_DEPLOYMENT.md | d09 Deployment Architecture | — |
| SIDECAR_DEPLOYMENT.md | d09 Deployment Architecture | d03 |
| QUICKSTART.md | Getting Started | — |
| BEGINNER_DEVELOPMENT.md | Getting Started | — |
| ADVANCED_DEVELOPMENT.md | Getting Started | d01, d07 |
| SCENARIOS.md | Getting Started | d07 |
| DUMMY_BACKEND.md | Getting Started | — |
| Glossary.md | Reference | All |
| TABLE_OF_CONTENTS.md | Reference | All |
| TroubleshootTOC.md | Troubleshooting | All |
| troubleshooting/*.md | Troubleshooting | Various |
| POC-OpenAI-Failover.md | Proof-of-Concept | d02, d03 |
| POC-Failover-configuration.md | Proof-of-Concept | d02, d03 |
| POC-Priority-configuration.md | Proof-of-Concept | d04 |
| POC-Secure-the-proxy.md | Proof-of-Concept | d08 |
| POC-security-the-apim.md | Proof-of-Concept | d08 |
| POC-Chargeback.md | Proof-of-Concept | d06 |
| BRANCH_CHANGES_vs_feature_async.md | ⚠ Internal only | — |
| OVERVIEW copy.md | ⚠ Duplicate | — |
| troubleshooting/TROUBLESHOOTING_TODO.md | ⚠ Internal backlog | — |
