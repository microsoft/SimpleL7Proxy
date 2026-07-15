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
- [ ] What keys are supported in a `Host1` connection string? (`host=`, `probe=`, `weight=`, `usemi=`, `processor=`, etc.)
- [ ] How does the proxy discover that a backend is healthy or unhealthy?
- [ ] How do I configure path-based routing — send `/openai/` to one host and `/embeddings/` to another?
- [ ] What is "direct mode" and when should I use it?
- [ ] How do I use Managed Identity instead of an API key?

### Load balancing
- [ ] What are the three load balancing modes (roundrobin / latency / random) and when do I use each?
- [ ] How does the proxy retry across backends when one fails?
- [ ] What is `MaxAttempts` and what happens when it is exceeded?

### Timeouts
- [ ] What is the difference between `Timeout` (per-host) and `TTL` (total request budget)?
- [ ] How do I set a timeout per backend host?
- [ ] How can a caller override the timeout on a per-request basis?

### Circuit breaker
- [ ] What settings control when a circuit opens? (`CBErrorThreshold`, `CBTimeslice`)
- [ ] What HTTP status codes count as failures?
- [ ] How quickly does the circuit recover once the backend is healthy again?

### Workers and queue
- [ ] How many workers should I configure?
- [ ] What is `MaxQueueLength` and what happens when the queue is full?
- [ ] How does the priority queue work — who gets served first?

### Hot reload
- [ ] Which settings can be changed without restarting the container?
- [ ] How do I connect to Azure App Configuration for hot-reload?
- [ ] What is the Sentinel key pattern and how does it work?

### Governance
- [ ] How do I restrict which callers can use the proxy (Entra App ID allowlist)?
- [ ] How do I assign different priority tiers to different callers?
- [ ] How do I configure per-user request limits?

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
