# Content Brief: ⚙️ Configure Backends and Settings

> **Purpose:** Give operators everything they need to configure a running proxy for their specific workload. Every question an operator asks while staring at an environment variable file must be answered here — in order of how often they'll need it.

---

## Quick Answers

<table>
<tr>
<td width="33%" valign="top">

### How do I add a backend?
Set `Host1` (through `Host9`) to a semicolon-delimited connection string: `host=https://api.example.com;probe=/health`. The `host` and `probe` keys are the minimum for a probed backend.

[→ Connection string keys](../BACKEND_HOSTS.md#reference--connection-string-keys)

</td>
<td width="33%" valign="top">

### Which load balance mode to use?
`latency` (default) routes to the fastest backend. `roundrobin` distributes evenly. `random` shuffles each time. For AI workloads with uneven throughput, `latency` or `roundrobin` is recommended.

[→ Load Balancing Modes](../LOAD_BALANCING.md)

</td>
<td width="33%" valign="top">

### Timeout vs TTL — what's the difference?
`Timeout` (default 20 min) is the per-attempt limit for a single backend call. `DefaultTTLSecs` (default 300 s) is the total budget for a request including all retries. TTL expiry returns 412 to the client.

[→ Timeouts explained](../TIMEOUTS.md)

</td>
</tr>
<tr>
<td width="33%" valign="top">

### When does a circuit breaker open?
When failures in the last `CBTimeslice` seconds (default 60 s) exceed `CBErrorThreshold` (default 50), the circuit opens and the host is skipped. It self-heals when old failures age out of the window.

[→ Circuit breaker settings](../CIRCUIT_BREAKER.md)

</td>
<td width="33%" valign="top">

### How many workers should I set?
Default is 10. Increase for higher concurrent throughput; decrease to reduce resource use. Workers are partitioned by priority tier when `PriorityWorkers` is configured.

> **⚠️ GAP:** No guidance on workers-per-backend sizing formula exists in any doc. → [Content gap details](#content-gaps-to-fill)

</td>
<td width="33%" valign="top">

### How do I change settings without a restart?
Connect to Azure App Configuration (`AZURE_APPCONFIG_ENDPOINT`). Warm settings update across all instances within ~30 s when the Sentinel key changes. Cold settings still require a restart.

[→ Hot-reload via App Configuration](../AZURE_APP_CONFIGURATION.md)

</td>
</tr>
</table>

---

## Reader Profile

| | |
|---|---|
| **Who** | Operators and platform engineers configuring a running deployment; SREs tuning behavior in production |
| **Why they come here** | The proxy is running but needs to be configured for the real workload: backends, timeouts, load balancing, governance |
| **When they read this** | After first run; before production; when a setting isn't doing what they expect |

---

## Questions this section MUST answer

### Backends
- [ ] How do I add a backend host? What is the connection string format?
  **Answer:** Add `Host1` through `Host9` with a semicolon-delimited connection string such as `host=https://api.example.com;probe=/health`.
- [ ] What keys are supported in a `Host1` connection string? (`host=`, `probe=`, `weight=`, `usemi=`, `processor=`, etc.)
  **Answer:** The documented keys include `host`, `probe`, `path`, `mode`, `ipaddress`, `processor`, `usemi` or `useoauth`, `audience`, `api-key`, `api-key-header`, `stripprefix`, and `retryafter`.
- [ ] How does the proxy discover that a backend is healthy or unhealthy?
  **Answer:** The [health poller](../Glossary.md#backend-management) calls each backend's probe path every `PollInterval` milliseconds (default 15,000 ms — every 15 seconds). It tracks the percentage of recent probes that returned `2xx`. When this rolling [success rate](../Glossary.md#backend-management) drops below `SuccessRate` (default 80%), the backend is removed from the [active pool](../Glossary.md#backend-management) and stops receiving traffic. It is automatically readmitted once its success rate recovers.
- [ ] How do I configure path-based routing — send `/openai/` to one host and `/embeddings/` to another?
  **Answer:** Add `path=/openai/` to one backend and `path=/embeddings/` to another in their connection strings. The [path filter](../Glossary.md#backend-management) evaluates specific paths first, so a backend with `path=/openai/` receives only requests whose URL starts with `/openai/`. A backend with no `path=` key acts as the catch-all and receives any request that did not match a more specific path.
- [ ] What is "direct mode" and when should I use it?
  **Answer:** Adding `mode=direct` to a host connection string disables the health probe entirely and treats the backend as always available. Use this for backends that start on demand — for example, a provisioned Azure OpenAI endpoint that may have zero active replicas when idle. Without `mode=direct`, the [health poller](../Glossary.md#backend-management) would fail while the backend is cold and incorrectly remove it from the [active pool](../Glossary.md#backend-management). See [→ Direct Mode](../Glossary.md#backend-management).
- [ ] How do I use Managed Identity instead of an API key?
  **Answer:** Add `usemi=true` and `audience=<resource-uri>` to the host connection string — for example, `audience=https://cognitiveservices.azure.com/` for Azure OpenAI. The proxy uses its [Managed Identity](../Glossary.md#authentication-and-security) (an Azure-managed credential attached to the container that requires no stored secrets) to request a short-lived OAuth2 access token for that resource at runtime. This is safer than a static API key because there is nothing to rotate or accidentally leak. See [→ Keyless Auth](../Glossary.md#authentication-and-security).

### Load balancing
- [ ] What are the three load balancing modes (roundrobin / latency / random) and when do I use each?
  **Answer:** `roundrobin` cycles through backends one at a time, distributing requests evenly regardless of performance. `latency` reorders backends by observed response time (measured by the [health poller](../Glossary.md#backend-management)) and always routes to the fastest one first. `random` assigns backends in a different order on each request, providing spread without tracking any state. For AI workloads with uneven backend throughput, `latency` or `roundrobin` is recommended. See [→ Load Balance Mode](../Glossary.md#backend-management).
- [ ] How does the proxy retry across backends when one fails?
  **Answer:** After the [path filter](../Glossary.md#backend-management) narrows candidates and load balancing determines their order, the worker tries backends one by one. It skips any host whose [circuit breaker](../Glossary.md#reliability) is open. If a backend returns a status code not in `AcceptableStatusCodes`, it counts as a failure and the proxy advances to the next host. Retries continue until a success, until all hosts are exhausted (returning `503`), or until the request's [TTL](../Glossary.md#request-lifecycle) expires (returning `412`).
- [ ] What is `MaxAttempts` and what happens when it is exceeded?
  **Answer:** `MaxAttempts` applies only when `IterationMode=MultiPass`, which allows the proxy to cycle through the backend list more than once. In that mode, `MaxAttempts` is the total number of backend attempts across all cycles — once reached, the proxy stops retrying and returns `503` to the client. The default `IterationMode=SinglePass` tries each backend at most once and `MaxAttempts` has no effect. See [→ IterationMode](../Glossary.md#backend-management).

### Timeouts
- [ ] What is the difference between `Timeout` (per-host) and `TTL` (total request budget)?
  **Answer:** `Timeout` (default 20 minutes) caps a single backend call — if the backend does not respond within that time, the attempt fails and the proxy tries the next host. [TTL](../Glossary.md#request-lifecycle) (`DefaultTTLSecs`, default 300 seconds) caps the entire request lifetime: queue wait time plus every retry attempt. When TTL expires, the caller receives `412`. For most AI workloads, the TTL is the more relevant limit to tune.
- [ ] How do I set a timeout per backend host?
  **Answer:** The current proxy docs describe a global `Timeout` and a per-request `S7PTimeout` override, but they do not document a separate per-host timeout knob in `HostN`.
- [ ] How can a caller override the timeout on a per-request basis?
  **Answer:** A caller sends the [`S7PTimeout`](../Glossary.md#protocol-and-headers) header with a value in milliseconds. The `S7P` prefix is the proxy's request-header namespace. For example, `S7PTimeout: 60000` sets a 60-second per-attempt limit for that request, overriding the global `Timeout`. The request's overall [TTL](../Glossary.md#request-lifecycle) still applies as the ceiling.

### Circuit breaker
- [ ] What settings control when a circuit opens? (`CBErrorThreshold`, `CBTimeslice`)
  **Answer:** `CBErrorThreshold` (default 50) is how many backend failures are tolerated before the [circuit breaker](../Glossary.md#reliability) opens. `CBTimeslice` (default 60 seconds) is how far back those failures are counted. Think of it as a rolling window: if 50 failures occur within any 60-second span, the circuit opens for that host. Failures older than `CBTimeslice` are discarded automatically, so a backend with occasional errors will not stay blocked indefinitely.
- [ ] What HTTP status codes count as failures?
  **Answer:** Any response code not listed in `AcceptableStatusCodes` counts as a [circuit breaker](../Glossary.md#reliability) failure. The default list is `200, 202, 400, 401, 403, 404, 408, 410, 412, 417`. Notably, `429` and `5xx` codes are not in the default list and will increment the failure counter. If you want the proxy to requeue on `429` instead of counting it as a failure, configure the backend to return [`S7PREQUEUE: true`](../Glossary.md#protocol-and-headers) on its `429` responses. See [→ AcceptableStatusCodes](../Glossary.md#reliability) and [→ Requeue](../Glossary.md#reliability).
- [ ] How quickly does the circuit recover once the backend is healthy again?
  **Answer:** [Auto-recovery](../Glossary.md#reliability) is automatic. As failures older than `CBTimeslice` seconds expire, the failure count drops. Once it falls below `CBErrorThreshold`, the circuit closes and the host re-enters the [active pool](../Glossary.md#backend-management). No manual action is needed — the window drains on its own as time passes.

### Workers and queue
- [ ] How many workers should I configure?
  **Answer:** Start with the default (`Workers=10`). Each worker handles one request at a time, so 10 workers means at most 10 simultaneous in-flight requests — any beyond that wait in the queue. Increase `Workers` to handle more concurrent load. Because `Workers` is a [Cold setting](../Glossary.md#configuration-management), changing it requires a container restart. See [→ Priority Workers](../Glossary.md#request-governance) if you want to reserve a portion of the worker pool for high-priority traffic.
- [ ] What is `MaxQueueLength` and what happens when the queue is full?
  **Answer:** `MaxQueueLength` (default 1000) is the maximum number of requests that can wait in the [priority queue](../Glossary.md#request-lifecycle) at one time. Once it is full, new arrivals are rejected immediately with a proxy-generated `429 Too Many Requests` — no backend is contacted. This `429` comes from the proxy itself, not a backend; see [→ Getting 429](05-diagnose-a-problem.md#getting-429-too-many-requests) to distinguish the two.
- [ ] How does the priority queue work — who gets served first?
  **Answer:** [Priority 1 is the highest priority](../Glossary.md#request-lifecycle) — lower integers are dispatched first, so a priority-1 request is served before a priority-2 request. Without worker reservations, a flood of high-priority requests could monopolize all workers and leave lower-priority requests waiting indefinitely (known as starvation). [Priority Workers](../Glossary.md#request-governance) prevent this by dedicating a fixed number of worker slots to each priority tier, guaranteeing lower tiers always have some capacity.

### Hot reload
- [ ] Which settings can be changed without restarting the container?
  **Answer:** [Warm settings](../Glossary.md#configuration-management) are hot-reloaded from Azure App Configuration within ~30 seconds when the [Sentinel](../Glossary.md#configuration-management) key changes — no container restart needed. [Cold settings](../Glossary.md#configuration-management) (such as `Workers` and `AsyncModeEnabled`) only take effect after a container restart. There is also a third category: Hidden settings, which are derived at startup from connection strings and cannot be changed at runtime at all.
- [ ] How do I connect to Azure App Configuration for hot-reload?
  **Answer:** Set `AZURE_APPCONFIG_ENDPOINT` or a connection string, assign `App Configuration Data Reader`, and use the matching `AZURE_APPCONFIG_LABEL`.
- [ ] What is the Sentinel key pattern and how does it work?
  **Answer:** [`Warm:Sentinel`](../Glossary.md#configuration-management) is a key in Azure App Configuration whose sole purpose is to signal that settings have changed. Because the proxy polls App Configuration on a ~30-second interval (rather than receiving push notifications), it needs a stable change signal. After updating any [Warm setting](../Glossary.md#configuration-management), change the value of `Warm:Sentinel` to anything new — all running instances detect the change on their next poll and reload their Warm settings atomically. If you forget to bump Sentinel, the updated values are never applied to running containers.

### Governance
- [ ] How do I restrict which callers can use the proxy (Entra App ID allowlist)?
  **Answer:** Set `ValidateAuthAppID=true`, then configure `ValidateAuthAppIDHeader` (the request header carrying the caller's Microsoft Entra Application ID — this is Azure's identity platform, formerly known as Azure Active Directory; the default header is `X-MS-CLIENT-PRINCIPAL-ID`) and `ValidateAuthAppIDUrl` (a URL or file path returning the list of permitted app IDs as JSON). Requests from unlisted app IDs are rejected with `401` before any backend is contacted. See [→ App ID Allowlist](../Glossary.md#request-governance).
- [ ] How do I assign different priority tiers to different callers?
  **Answer:** Set `PriorityKeyHeader` to the name of the request header that carries the caller's tier value (default `S7PPriorityKey`). Set `PriorityKeys` to a comma-separated list of expected header values and `PriorityValues` to the corresponding priority integers — for example, `PriorityKeys=gold,silver` maps to `PriorityValues=1,2`. Optionally configure [Priority Workers](../Glossary.md#request-governance) to reserve dedicated worker slots per tier. See [→ Priority Mapping](../Glossary.md#request-governance).
- [ ] How do I configure per-user request limits?
  **Answer:** First, enable [user profiles](../Glossary.md#request-governance) by setting `UserProfilesUrl` or `UserProfilesPath` to a JSON file that defines per-user settings. Once enabled, `UserPriorityThreshold` (a decimal fraction from 0.0 to 1.0, default 0.1) caps how much of the worker pool a single user can occupy simultaneously. For example, the default value of `0.1` means one user's requests can hold at most 10% of workers — with `Workers=10`, that is 1 worker at a time per user.

---

## What the reader can do AFTER reading this

- [ ] All backend hosts are configured and healthy
- [ ] Timeouts and circuit breaker thresholds match the SLA of their backends
- [ ] Load balancing strategy is chosen and applied
- [ ] Governance rules (if needed) are in place
- [ ] Settings are stored in App Configuration and hot-reload is working
- [ ] Knows what to go to next: a POC to validate, or OBSERVABILITY to set up telemetry

---

## Existing documents that cover this area

| Document | What it covers | Gap? |
|----------|----------------|------|
| [CONFIGURATION_CATEGORIES.md](../CONFIGURATION_CATEGORIES.md) | Settings grouped by goal: essential / common / advanced | Entry point — verify it is the first document operators read |
| [ENVIRONMENT_VARIABLES.md](../ENVIRONMENT_VARIABLES.md) | Exhaustive reference for all variables | Reference doc — too long to read top-to-bottom |
| [BACKEND_HOSTS.md](../BACKEND_HOSTS.md) | Host connection string format, routing, health polling | Primary backend config reference |
| [LOAD_BALANCING.md](../LOAD_BALANCING.md) | Load balance modes, retry, requeue | Covers retry and pipeline well |
| [TIMEOUTS.md](../TIMEOUTS.md) | Timeout vs TTL, per-request overrides | Covers async too — keep async section linked from Async area |
| [CIRCUIT_BREAKER.md](../CIRCUIT_BREAKER.md) | CB states, thresholds, recovery | Self-contained — link from reliability context |
| [ADVANCED_CONFIGURATION.md](../ADVANCED_CONFIGURATION.md) | Priority management, user governance | Contains content for both operator and governance readers |
| [AZURE_APP_CONFIGURATION.md](../AZURE_APP_CONFIGURATION.md) | App Config setup, RBAC, Sentinel | Required before hot-reload will work |
| [SCENARIOS.md](../SCENARIOS.md) | Copy-paste config blocks for common patterns | The fastest path to a working config |

---

## Content gaps to fill

- [ ] A "start here" decision tree: what kind of workload? → which settings matter?
- [ ] An annotated minimal `Host1` connection string with all optional keys explained inline
- [ ] A single "Warm / Cold / Hidden" table at the top of the config section so operators know what they can change live
- [ ] A worked example: two backends, path routing, different timeouts — shows all the moving parts together
- [ ] A "do not set these unless you understand them" callout for dangerous settings (`Workers`, `CBErrorThreshold`)
