# See It Work: Runnable Scenarios That Show the Proxy Catching Failures

Don't take our word for it. Each scenario runs in under five minutes using the included simulator — no real Azure OpenAI account needed — and shows the proxy handling the exact failures that would otherwise reach your users.

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

> Every POC file must answer all five questions. These are the content requirements for each scenario.

### What will I observe in failover?

#### What will I observe? (exact observable outcomes, not "the proxy will route traffic")

SimpleL7Proxy makes the backend retry visible in the response headers. Each POC should name the exact pass or fail signals up front, such as `200 OK`, `202 Accepted`, `403`, `503`, `x-Backend-Attempts`, `x-backend-affinity`, or a specific telemetry record.

**Example — failover:** Client sends one request. Backend A returns `429`. `x-Backend-Attempts: 2` in the response confirms the proxy retried. `BackendHost` shows Backend B was used. The client sees `200 OK`.

#### What do I need running before I start? (simulator, config, Azure resources if any)

SimpleL7Proxy POCs are designed to need only the proxy itself and the simulator or backend for that scenario. Each POC lists only the concrete prerequisites: usually the proxy, the simulator or backend, and any Azure services the scenario truly depends on.

#### How long will this take? (must be < 5 minutes for setup + execution)

SimpleL7Proxy POCs are intended to stay under five minutes once the base environment exists.

---

### What does priority routing prove?

#### What are the exact commands to run?

SimpleL7Proxy POCs provide copy-paste-ready commands so the reader doesn't have to translate prose into commands — usually `curl`, `az`, or policy snippets.

#### What do I send to trigger the behavior?

SimpleL7Proxy responds to the specific trigger for each scenario. For priority routing, send a request with the priority header (`llm_proxy_priority: 1`). For failover, send any request when the primary backend is configured to return `429`. For chargeback, include `X-UserID`.

#### What should I see in the response headers / body / logs during each step?

SimpleL7Proxy produces predictable observable changes at each step. The reader should see specific headers, statuses, bodies, or log lines that change in a predictable way — not narrative descriptions.

---

### How does chargeback telemetry work?

#### What headers confirm the behavior? (e.g., `x-Backend-Attempts: 2` for failover)

SimpleL7Proxy injects these headers as POC confirmation signals:

| Header | What it confirms |
|--------|-----------------|
| `x-Backend-Attempts` | How many backends the proxy tried |
| `x-backend-affinity` | Which backend the APIM policy selected |
| `BackendHost` | Which backend ultimately served the request |
| `x-Request-Queue-Duration` | How long the request waited in the queue |
| HTTP status code | Final outcome |

#### What log entries or App Insights events confirm it? (exact field names and values)

SimpleL7Proxy logs a structured event per request. Verification should call out the exact `backendLog`, Application Insights field, or event dimension that proves the proxy took the expected path.

#### What would I see if it did NOT work? (how to distinguish success from silent failure)

SimpleL7Proxy POCs include a failure description for each scenario — the wrong status code, a missing header change, or telemetry that never appears — so you can distinguish success from a silent misconfiguration.

---

### Do I need real Azure OpenAI?

#### What setting(s) controlled the behavior I just observed?

SimpleL7Proxy behavior is driven by a small set of configuration knobs in each scenario. The scenario names them explicitly — such as backend priority lists, retry count, timeout, auth settings, or telemetry configuration.

#### What is the state machine? (e.g., CLOSED → OPEN → recovery for circuit breaker)

SimpleL7Proxy's behavior in each scenario follows a simple state transition. Each POC explains this — for example: `select → throttle → retry → recover` for failover, or `validate → reject` vs `validate → allow` for security.

---

### How do I verify a POC worked?

#### What variants can I try to see different behavior?

SimpleL7Proxy POCs include variants: changing priority, latency, retry count, auth mode, backend health, or the chosen backend set to exercise different paths.

---

### How do I secure the proxy?

#### What is the first thing I should change to adapt this to my own workload?

SimpleL7Proxy POCs are written with demo endpoints and identities. The first adaptation is to replace demo endpoints, headers, identities, and the backend list with the real ones you plan to use in production.

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
