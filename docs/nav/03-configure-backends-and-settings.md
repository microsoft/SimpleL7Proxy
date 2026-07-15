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
  **Answer:** The health poller calls each host's probe on a schedule, records success or failure, and keeps the host active only while its rolling success rate stays above `SuccessRate`.
- [ ] How do I configure path-based routing — send `/openai/` to one host and `/embeddings/` to another?
  **Answer:** Give each host a `path=` prefix, because specific path matches win first and the catch-all host is used only when no specific route matches.
- [ ] What is "direct mode" and when should I use it?
  **Answer:** `mode=direct` disables probing and treats the host as always healthy, which is the right choice for scale-to-zero or serverless backends.
- [ ] How do I use Managed Identity instead of an API key?
  **Answer:** Set `usemi=true` and supply the target `audience`, so the proxy acquires a token for that backend instead of sending an API key.

### Load balancing
- [ ] What are the three load balancing modes (roundrobin / latency / random) and when do I use each?
  **Answer:** Use `roundrobin` for even distribution, `latency` when you want the fastest backend first, and `random` when you want simple spread without a stable order.
- [ ] How does the proxy retry across backends when one fails?
  **Answer:** After path filtering and host ordering, the worker tries eligible hosts in sequence, skipping open circuits and moving to the next host on non-success responses.
- [ ] What is `MaxAttempts` and what happens when it is exceeded?
  **Answer:** `MaxAttempts` caps total retries in `MultiPass` mode, and once the cap is hit the request stops retrying and fails with the normal exhaustion outcome.

### Timeouts
- [ ] What is the difference between `Timeout` (per-host) and `TTL` (total request budget)?
  **Answer:** `Timeout` limits one backend attempt, while `TTL` limits the full life of the request including queue wait and every retry.
- [ ] How do I set a timeout per backend host?
  **Answer:** The current proxy docs describe a global `Timeout` and a per-request `S7PTimeout` override, but they do not document a separate per-host timeout knob in `HostN`.
- [ ] How can a caller override the timeout on a per-request basis?
  **Answer:** A caller can send the `S7PTimeout` header with a timeout value in milliseconds.

### Circuit breaker
- [ ] What settings control when a circuit opens? (`CBErrorThreshold`, `CBTimeslice`)
  **Answer:** `CBErrorThreshold` sets how many failures are allowed, and `CBTimeslice` sets how long those failures stay in the sliding window.
- [ ] What HTTP status codes count as failures?
  **Answer:** Any backend status not included in `AcceptableStatusCodes` counts as a circuit-breaker failure.
- [ ] How quickly does the circuit recover once the backend is healthy again?
  **Answer:** Recovery is automatic once enough old failures age out of the `CBTimeslice` window and the host falls back below the threshold.

### Workers and queue
- [ ] How many workers should I configure?
  **Answer:** Start with the default `Workers=10`, then raise it as throughput needs grow or reserve tiers explicitly with `PriorityWorkers`.
- [ ] What is `MaxQueueLength` and what happens when the queue is full?
  **Answer:** `MaxQueueLength` is the maximum in-memory queue size, and once it is full the proxy rejects new work with a proxy-generated `429`.
- [ ] How does the priority queue work — who gets served first?
  **Answer:** Lower-number priority requests are dequeued first, and optional priority worker reservations keep higher tiers from being starved by lower ones.

### Hot reload
- [ ] Which settings can be changed without restarting the container?
  **Answer:** Warm settings can be changed live through App Configuration, while Cold settings still require a container restart.
- [ ] How do I connect to Azure App Configuration for hot-reload?
  **Answer:** Set `AZURE_APPCONFIG_ENDPOINT` or a connection string, assign `App Configuration Data Reader`, and use the matching `AZURE_APPCONFIG_LABEL`.
- [ ] What is the Sentinel key pattern and how does it work?
  **Answer:** `Warm:Sentinel` is the refresh trigger, so after you change a Warm key you bump Sentinel and all running instances reload their Warm settings on the next poll.

### Governance
- [ ] How do I restrict which callers can use the proxy (Entra App ID allowlist)?
  **Answer:** Turn on `ValidateAuthAppID` and point the proxy at the allowlist source and header that carry the caller's app ID.
- [ ] How do I assign different priority tiers to different callers?
  **Answer:** Map a request header through `PriorityKeyHeader`, `PriorityKeys`, and `PriorityValues`, then optionally reserve workers with `PriorityWorkers`.
- [ ] How do I configure per-user request limits?
  **Answer:** Enable user profiles and use `UserPriorityThreshold` so one caller cannot dominate more than the configured share of active requests.

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
