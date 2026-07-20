# SimpleL7Proxy — Documentation Table of Contents

All documentation is organized by conceptual domain. Each entry links to the authoritative document for that concept.

> **Machine-readable taxonomy:** `SimpleL7Proxy/taxonomy/concepts.json` contains the full concept graph with IDs, relationships, settings cross-references, and response code mappings used to generate and validate documentation.

---

## Request Lifecycle

The end-to-end path a request travels from client to backend and back.

| Concept | Document |
|---------|----------|
| Ingress, listener port, worker count | [ENVIRONMENT_VARIABLES.md](ENVIRONMENT_VARIABLES.md) |
| Priority queue, priority levels, default priority | [ADVANCED_CONFIGURATION.md](ADVANCED_CONFIGURATION.md#priority-management) |
| TTL (Time-to-Live) and per-request overrides | [TIMEOUTS.md](TIMEOUTS.md) |
| Worker dispatch, concurrent thread count | [ENVIRONMENT_VARIABLES.md](ENVIRONMENT_VARIABLES.md) |
| Response headers injected by the proxy | [RESPONSE_CODES.md](RESPONSE_CODES.md) |
| All proxy-originated and pass-through response codes | [RESPONSE_CODES.md](RESPONSE_CODES.md) |
| Request and response header reference (S7P* headers) | [RESPONSE_CODES.md](RESPONSE_CODES.md) |

---

## Backend Management

How the proxy discovers, probes, and selects backend hosts.

| Concept | Document |
|---------|----------|
| Backend host configuration (Host1–Host9) | [BACKEND_HOSTS.md](BACKEND_HOSTS.md) |
| Connection string format (all keys) | [BACKEND_HOSTS.md](BACKEND_HOSTS.md) |
| Legacy per-variable format (deprecated) | [BACKEND_HOSTS.md](BACKEND_HOSTS.md) |
| Path-based routing, catch-all hosts, strip prefix | [BACKEND_HOSTS.md](BACKEND_HOSTS.md#path-based-routing) |
| Direct mode (serverless / no probing) | [BACKEND_HOSTS.md](BACKEND_HOSTS.md#direct-mode) |
| Stream processor (`processor=OpenAI`) | [AI_FOUNDRY_INTEGRATION.md](AI_FOUNDRY_INTEGRATION.md) |
| IP override, DNS bypass | [BACKEND_HOSTS.md](BACKEND_HOSTS.md) |
| Health polling, success rate, active pool | [BACKEND_HOSTS.md](BACKEND_HOSTS.md#health-polling) |
| Load balance modes (roundrobin / latency / random) | [LOAD_BALANCING.md](LOAD_BALANCING.md) |
| Backend selection pipeline (path → order → circuit gate) | [LOAD_BALANCING.md](LOAD_BALANCING.md) |
| Shared iterators | [LOAD_BALANCING.md](LOAD_BALANCING.md) |
| Iteration mode, max attempts | [LOAD_BALANCING.md](LOAD_BALANCING.md) |

---

## Reliability

Mechanisms that prevent failures from propagating to clients.

| Concept | Document |
|---------|----------|
| Circuit breaker: CLOSED / OPEN states | [CIRCUIT_BREAKER.md](CIRCUIT_BREAKER.md) |
| Sliding window, auto-recovery | [CIRCUIT_BREAKER.md](CIRCUIT_BREAKER.md) |
| Progressive delay (50%–90% threshold) | [CIRCUIT_BREAKER.md](CIRCUIT_BREAKER.md) |
| Global blocked check (all circuits OPEN → 503) | [CIRCUIT_BREAKER.md](CIRCUIT_BREAKER.md) |
| Acceptable status codes | [CIRCUIT_BREAKER.md](CIRCUIT_BREAKER.md) |
| Retry across backends | [LOAD_BALANCING.md](LOAD_BALANCING.md#retrying-across-backends) |
| Requeue on 429 + S7PREQUEUE | [LOAD_BALANCING.md](LOAD_BALANCING.md#handling-responses) |
| TTL (total request budget) | [TIMEOUTS.md](TIMEOUTS.md) |
| Per-host Timeout | [TIMEOUTS.md](TIMEOUTS.md#synchronous-requests) |
| Per-request override headers (S7PTTL, S7PTimeout) | [TIMEOUTS.md](TIMEOUTS.md#per-request-overrides) |
| AsyncTriggerTimeout / AsyncTimeout / AsyncTTLSecs | [TIMEOUTS.md](TIMEOUTS.md#async-requests) |

---

## Request Governance

Validation and priority rules applied before a request enters the queue.

| Concept | Document |
|---------|----------|
| Validation pipeline execution order | [REQUEST_VALIDATION.md](REQUEST_VALIDATION.md) |
| Inbound key/OAuth validation (step 1) | [REQUEST_VALIDATION.md](REQUEST_VALIDATION.md#scenario-5-require-an-inbound-shared-key-header) |
| App ID validation (Entra allowlist, step 2) | [REQUEST_VALIDATION.md](REQUEST_VALIDATION.md#scenario-4-block-unknown-entra-app-ids) |
| Header stripping (DisallowedHeaders, step 3) | [REQUEST_VALIDATION.md](REQUEST_VALIDATION.md#scenario-2-strip-internal-headers-before-forwarding) |
| Required headers (step 5, returns 417) | [REQUEST_VALIDATION.md](REQUEST_VALIDATION.md#scenario-1-require-specific-headers-on-every-request) |
| Header value validation rules (step 6) | [REQUEST_VALIDATION.md](REQUEST_VALIDATION.md#scenario-3-validate-header-values-against-a-per-user-allowlist) |
| User profiles: structure, fields, refresh | [USER_PROFILES.md](USER_PROFILES.md) |
| User ID field, profile lookup | [USER_PROFILES.md](USER_PROFILES.md) |
| Suspended users | [USER_PROFILES.md](USER_PROFILES.md#user-suspension) |
| async-config profile field | [USER_PROFILES.md](USER_PROFILES.md) |
| Priority mapping (PriorityKeys / PriorityValues / PriorityWorkers) | [ADVANCED_CONFIGURATION.md](ADVANCED_CONFIGURATION.md#priority-management) |
| Per-user throttling (UserPriorityThreshold) | [ADVANCED_CONFIGURATION.md](ADVANCED_CONFIGURATION.md#user-governance) |

---

## Async Mode

Long-running request handling that decouples client wait from backend processing.

| Concept | Document |
|---------|----------|
| Async mode overview, three-level enablement | [AsyncOperation.md](AsyncOperation.md) |
| AsyncTriggerTimeout and async upgrade (202 response) | [TIMEOUTS.md](TIMEOUTS.md#async-requests) |
| Azure Service Bus configuration and status events | [AsyncOperation.md](AsyncOperation.md#azure-service-bus-configuration) |
| Result blob storage and returned base URIs | [AsyncOperation.md](AsyncOperation.md) |
| Blob retention and lifecycle management | [StorageBlobConfig.md](StorageBlobConfig.md) |
| Async processing variables (full reference) | [ENVIRONMENT_VARIABLES.md](ENVIRONMENT_VARIABLES.md#async-processing-variables) |

---

## Observability

How the proxy exposes telemetry about its own operation.

| Concept | Document |
|---------|----------|
| ProxyEvent model, event headers, fan-out architecture | [OBSERVABILITY.md](OBSERVABILITY.md) |
| Application Insights (primary production sink) | [OBSERVABILITY.md](OBSERVABILITY.md#telemetry-channels) |
| Event Hubs (high-volume streaming) | [OBSERVABILITY.md](OBSERVABILITY.md#telemetry-channels) |
| Local log file (development) | [OBSERVABILITY.md](OBSERVABILITY.md#telemetry-channels) |
| Token telemetry from SSE streams (`processor=OpenAI`) | [OBSERVABILITY.md](OBSERVABILITY.md) |
| Custom event logger (IEventClient + IHostedService) | [OBSERVABILITY.md](OBSERVABILITY.md#custom-event-loggers) |
| Health endpoints: /liveness, /readiness, /startup | [HEALTH_CHECKING.md](HEALTH_CHECKING.md) |
| Sidecar mode health isolation (port 9000) | [HEALTH_CHECKING.md](HEALTH_CHECKING.md#2-sidecar-mode-high-performance) |
| Observability logging variables | [ENVIRONMENT_VARIABLES.md](ENVIRONMENT_VARIABLES.md#logging--monitoring-variables) |

---

## Configuration Management

How settings reach the proxy and when they take effect.

| Concept | Document |
|---------|----------|
| Warm / Cold / Hidden setting classification | [CONFIGURATION_SETTINGS.md](CONFIGURATION_SETTINGS.md) |
| Settings organized by frequency of use (Essential / Common / Advanced) | [CONFIGURATION_CATEGORIES.md](CONFIGURATION_CATEGORIES.md) |
| Azure App Configuration: setup, RBAC, Sentinel pattern | [AZURE_APP_CONFIGURATION.md](AZURE_APP_CONFIGURATION.md) |
| Composite connection string format | [BACKEND_HOSTS.md](BACKEND_HOSTS.md) |
| All environment variables (exhaustive reference) | [ENVIRONMENT_VARIABLES.md](ENVIRONMENT_VARIABLES.md) |
| Minimum required configuration | [ENVIRONMENT_VARIABLES.md](ENVIRONMENT_VARIABLES.md#minimum-required-configuration) |
| Copy-paste configurations for common scenarios | [SCENARIOS.md](SCENARIOS.md) |

---

## Authentication and Security

How the proxy authenticates to backends and restricts inbound callers.

| Concept | Document |
|---------|----------|
| Managed Identity for backends (`usemi=true`) | [BACKEND_HOSTS.md](BACKEND_HOSTS.md) |
| Keyless auth for Azure OpenAI / AI Foundry | [AI_FOUNDRY_INTEGRATION.md](AI_FOUNDRY_INTEGRATION.md) |
| OAuth2 Bearer token attachment | [BACKEND_HOSTS.md](BACKEND_HOSTS.md) |
| App ID allowlist (Entra, >13 app IDs) | [REQUEST_VALIDATION.md](REQUEST_VALIDATION.md) |
| Header stripping to prevent internal header leakage | [REQUEST_VALIDATION.md](REQUEST_VALIDATION.md) |
| VNet deployment and sovereign cloud | [OVERVIEW.md](OVERVIEW.md) |
| Security overview and responsible disclosure | [SECURITY.md](SECURITY.md) |
| Securing the proxy with APIM | [POC-Secure-the-proxy.md](POC-Secure-the-proxy.md) |

---

## Deployment Architecture

How the proxy is packaged and run on Azure.

| Concept | Document |
|---------|----------|
| Azure Container Apps deployment | [CONTAINER_DEPLOYMENT.md](CONTAINER_DEPLOYMENT.md) |
| Sidecar deployment (proxy + health probe containers) | [SIDECAR_DEPLOYMENT.md](SIDECAR_DEPLOYMENT.md) |
| Build and deploy scripts, parameters file | [SIDECAR_DEPLOYMENT.md](SIDECAR_DEPLOYMENT.md) |
| Azure AI Foundry / OpenAI integration patterns | [AI_FOUNDRY_INTEGRATION.md](AI_FOUNDRY_INTEGRATION.md) |
| APIM integration and reference policy | [../APIM-Policy/readme.md](../APIM-Policy/readme.md) |
| Day-2 operations | [../deployment/DAY2_OPERATIONS.md](../deployment/DAY2_OPERATIONS.md) |

---

## Protocol and Headers

Named HTTP signals that cross the client-proxy and proxy-backend boundaries.

| Header | Direction | Document |
|--------|-----------|----------|
| `S7PPriorityKey` | Client → proxy | [RESPONSE_CODES.md](RESPONSE_CODES.md#request-headers-proxy-reads-these) |
| `S7PTTL` | Client → proxy | [TIMEOUTS.md](TIMEOUTS.md#per-request-overrides) |
| `S7PTimeout` | Client → proxy | [TIMEOUTS.md](TIMEOUTS.md#per-request-overrides) |
| `S7PAsyncMode` | Client → proxy | [AsyncOperation.md](AsyncOperation.md) |
| `S7PDEBUG` | Client → proxy | [RESPONSE_CODES.md](RESPONSE_CODES.md#request-headers-proxy-reads-these) |
| `S7PREQUEUE` | Backend → proxy | [RESPONSE_CODES.md](RESPONSE_CODES.md#backend-429-and-requeue) |
| `Request-Queue-Duration` | Proxy → client | [RESPONSE_CODES.md](RESPONSE_CODES.md#response-headers-proxy-adds-these) |
| `Request-Process-Duration` | Proxy → client | [RESPONSE_CODES.md](RESPONSE_CODES.md#response-headers-proxy-adds-these) |
| `BackendHost` | Proxy → client | [RESPONSE_CODES.md](RESPONSE_CODES.md#response-headers-proxy-adds-these) |
| `Total-Latency` | Proxy → client | [RESPONSE_CODES.md](RESPONSE_CODES.md#response-headers-proxy-adds-these) |
| `Attempts` | Proxy → client | [RESPONSE_CODES.md](RESPONSE_CODES.md#response-headers-proxy-adds-these) |
| `Lifetime-Attempts` | Proxy → client | [RESPONSE_CODES.md](RESPONSE_CODES.md#response-headers-proxy-adds-these) |

---

## Getting Started

| Goal | Start here |
|------|-----------|
| First deployment | [QUICKSTART.md](QUICKSTART.md) |
| Local development | [BEGINNER_DEVELOPMENT.md](BEGINNER_DEVELOPMENT.md) |
| Advanced development & performance tuning | [ADVANCED_DEVELOPMENT.md](ADVANCED_DEVELOPMENT.md) |
| Understanding the architecture | [OVERVIEW.md](OVERVIEW.md) |
| Diagnosing a problem | [TroubleshootTOC.md](TroubleshootTOC.md) |
| Code structure and internals | [design.md](design.md) |

## Proof-of-Concept Guides

| Scenario | Document |
|----------|----------|
| OpenAI failover across regions | [POC-OpenAI-Failover.md](POC-OpenAI-Failover.md) |
| Failover configuration | [POC-Failover-configuration.md](POC-Failover-configuration.md) |
| Priority-based routing | [POC-Priority-configuration.md](POC-Priority-configuration.md) |
| Securing the proxy | [POC-Secure-the-proxy.md](POC-Secure-the-proxy.md) |
| Securing APIM | [POC-security-the-apim.md](POC-security-the-apim.md) |
| Chargeback and token tracking | [POC-Chargeback.md](POC-Chargeback.md) |
