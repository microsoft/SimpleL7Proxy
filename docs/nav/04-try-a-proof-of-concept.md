# See It Work: Runnable Scenarios That Show the Proxy Catching Failures

Each scenario runs in under five minutes using the included simulator — no real Azure OpenAI account needed — and shows the proxy handling the failures that would otherwise reach your users.

---

## Quick Answers

<table>
<tr>
<td width="33%" valign="top">

### What will I observe in failover?
When Backend A returns `429`, APIM marks it throttled and retries on Backend B. The client still sees `200 OK`. Verified via `x-Backend-Attempts: 2` and a changed `x-backend-affinity` header.

[→ What will I observe in failover?](#what-will-i-observe-in-failover)

</td>
<td width="33%" valign="top">

### What does priority routing prove?
A `llm_proxy_priority: 1` request routes only to backends whose `acceptablePriorities` includes priority 1. A request with no eligible backend returns `503`. Verified via `x-Backend-Attempts` and `backendLog`.

[→ What does priority routing prove?](#what-does-priority-routing-prove)

</td>
<td width="33%" valign="top">

### How does chargeback telemetry work?
Send a request with an `X-UserID` header. The proxy extracts token counts from the streaming response and logs them to Application Insights. Query by `X-UserID` to get per-user consumption.

[→ How does chargeback telemetry work?](#how-does-chargeback-telemetry-work)

</td>
</tr>
<tr>
<td width="33%" valign="top">

### Do I need real Azure OpenAI?
No. The included LLM Simulator (an Azure Function) returns realistic OpenAI-format responses, simulates `429` throttling, and supports configurable latency — all without a real model endpoint.

[→ Do I need real Azure OpenAI?](#do-i-need-real-azure-openai)

</td>
<td width="33%" valign="top">

### How do I verify a POC worked?
Each POC has a "What you will observe" section listing specific response headers (`x-Backend-Attempts`, `BackendHost`, `x-backend-affinity`) and, where relevant, App Insights queries.

> **⚠️ GAP:** No POC has a verification *checklist* (pass/fail signals independent of App Insights). → [Content gap details](#content-gaps-to-fill)

[→ How do I verify a POC worked?](#how-do-i-verify-a-poc-worked)

</td>
<td width="33%" valign="top">

### How do I secure the proxy?
The security POCs cover two layers: EasyAuth on the ACA proxy container (unauthenticated requests rejected before reaching the proxy), and `validate-jwt` in APIM for upstream caller validation.

[→ How do I secure the proxy?](#how-do-i-secure-the-proxy)

</td>
</tr>
</table>

---

## Full Answers

### What will I observe in failover?

#### What will I observe? (exact observable outcomes)

The proxy makes the retry visible in the response headers. For failover: the client sends one request, Backend A returns `429`, and the proxy retries on Backend B. The response the client receives is `200 OK`. Check these two headers to confirm it worked:

- `x-Backend-Attempts: 2` — the proxy tried two backends
- `BackendHost: <backend B url>` — Backend B served the final response

See → [POC-Failover-configuration.md](../POC-Failover-configuration.md) for the step-by-step runthrough.

#### What do I need running before I start?

The proxy and the LLM simulator. No Azure subscription needed for most scenarios — the simulator returns realistic OpenAI-format responses, simulates `429` throttling, and supports configurable latency.

#### How long will this take?

Under five minutes once the proxy and simulator are running. The simulator setup is the longest step — it's a single command.

---

### What does priority routing prove?

#### What are the exact commands to run?

Each POC provides copy-paste commands. See the relevant file for the exact `curl` or `az` calls:
- Failover: [POC-Failover-configuration.md](../POC-Failover-configuration.md)
- Priority routing: [POC-Priority-configuration.md](../POC-Priority-configuration.md)
- Chargeback: [POC-Chargeback.md](../POC-Chargeback.md)
- PTU → PAYGO failover: [POC-OpenAI-Failover.md](../POC-OpenAI-Failover.md)
- Securing the proxy: [POC-Secure-the-proxy.md](../POC-Secure-the-proxy.md)

#### What do I send to trigger the behavior?

It depends on the scenario:
- **Failover:** Send any request when Backend A is configured to return `429`.
- **Priority routing:** Send a request with the priority header — for example, `llm_proxy_priority: 1`.
- **Chargeback:** Include `X-UserID` in the request to tag it for per-user tracking.

#### What should I see in the response headers / body / logs during each step?

Each POC has a "What you will observe" section that lists the specific headers, status codes, or log lines that change at each step. The table in [How does chargeback telemetry work?](#how-does-chargeback-telemetry-work) below shows the headers shared across scenarios.

---

### How does chargeback telemetry work?

#### What headers confirm the behavior? (e.g., `x-Backend-Attempts: 2` for failover)

These are the headers the proxy adds to every proxied response:

| Header | What it confirms |
|--------|-----------------|
| `x-Backend-Attempts` | How many backends the proxy tried |
| `x-backend-affinity` | Which backend the APIM policy selected |
| `BackendHost` | Which backend ultimately served the request |
| `x-Request-Queue-Duration` | How long the request waited in the queue |
| HTTP status code | Final outcome |

#### What log entries or App Insights events confirm it?

For chargeback specifically: query Application Insights by the `X-UserID` dimension to see per-user token consumption. Each request emits an event with a `tokens` field that the proxy extracts from the streaming response body.

For failover: `backendLog` in the event record shows which backends were tried and their individual status codes.

#### What would I see if it did NOT work?

- **Failover not working:** `x-Backend-Attempts: 1` (only one backend tried) and the client receives an error code rather than `200 OK`.
- **Priority routing not working:** Low-priority requests are not held back under load — queue depth stays flat regardless of priority.
- **Chargeback not working:** Application Insights shows no token fields, or querying by `X-UserID` returns no records. Check that `EVENT_LOGGERS` includes `appinsights` and the connection string is set.

---

### Do I need real Azure OpenAI?

No — see [Quick Answer above](#do-i-need-real-azure-openai). The LLM Simulator covers failover, priority, and chargeback without a real endpoint. The APIM-based PTU → PAYGO failover POC does require an actual APIM instance.

#### What setting(s) controlled the behavior I just observed?

The key configuration knobs depend on the scenario:

| Scenario | Key settings |
|----------|-------------|
| Failover | `Host1`, `Host2` connection strings; `CBErrorThreshold` |
| Priority routing | `PriorityKeys`, `PriorityValues`, `PriorityWorkers` |
| Chargeback | `EVENT_LOGGERS`, `UserProfilesUrl`, processor key in `Host1` |
| Security | `ValidateAuthAppID`, `ValidateAuthAppIDHeader` |

#### What is the state machine?

Each scenario follows a simple state transition:

- **Failover:** `select backend → receive 429 → retry on next backend → return 200 to client`
- **Circuit breaker:** `CLOSED → failures exceed threshold → OPEN → failures age out → CLOSED`
- **Security:** `validate caller ID → not in allowlist → reject 401` vs `validate → allowed → forward`

---

### How do I verify a POC worked?

#### What variants can I try to see different behavior?

- **Failover:** Configure both backends to return `429` — expect `503`.
- **Priority routing:** Fill the queue with low-priority requests, then send a high-priority request and observe it served first.
- **Chargeback:** Use different `X-UserID` values; each should appear as a separate record in App Insights.

---

### How do I secure the proxy?

#### What is the first thing I should change to adapt this to my own workload?

Replace demo endpoints, app registration IDs, and backend connection strings with your own. Start with `Host1` pointing at your real backend, then add EasyAuth against your Entra tenant. See → [POC-Secure-the-proxy.md](../POC-Secure-the-proxy.md).

---

## POC scenarios to cover

| Scenario | What behavior it demonstrates | Observable signal | Status |
|----------|-------------------------------|-------------------|--------|
| Failover | Backend goes unhealthy → traffic routes to secondary | Response still `200 OK`; `BackendHost` header changes | Exists: [POC-Failover-configuration.md](../POC-Failover-configuration.md) — review against brief |
| Priority routing | High-priority requests served before low-priority | Low-priority queue length grows under load; high-priority returns fast | Exists: [POC-Priority-configuration.md](../POC-Priority-configuration.md) — review against brief |
| Chargeback | Per-user token consumption captured in App Insights | Query by user ID returns token count | Exists: [POC-Chargeback.md](../POC-Chargeback.md) — review against brief |
| OpenAI PTU → PAYGO failover via APIM | PTU returns `429` → APIM retries on PAYGO | `x-Backend-Attempts: 2`; `x-backend-affinity` changes | Exists: [POC-OpenAI-Failover.md](../POC-OpenAI-Failover.md) — verify runnable |
| Securing the proxy | Unauthenticated request rejected; authenticated allowed | `403` vs `200` | Exists: [POC-Secure-the-proxy.md](../POC-Secure-the-proxy.md) — verify against brief |

---

## You Should Now Be Able To

- [ ] Can describe what happened in plain language
- [ ] Can explain which setting(s) produced the observed behavior
- [ ] Can reproduce the behavior reliably
- [ ] Knows which configuration knobs to change for their real workload
- [ ] Knows which doc to go to for deeper configuration of that feature

---

## Content gaps to fill

- [ ] Every POC must have a "TL;DR < 5 min" section at the top with numbered steps and expected output
- [ ] Every POC must have a "What you will observe" block listing behavior as pure bullets (not narrative)
- [ ] Every POC must have a verification checklist (not a table — a checklist of pass/fail signals)
- [ ] Every POC must have a "why this happened" state machine (even a simple 3-state diagram)
- [ ] Every POC must be verifiable without Azure App Insights (observable from response headers alone)
- [ ] Add a POC index page that shows all scenarios at a glance with a one-line description of what each proves
