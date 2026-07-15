# Content Brief: 🧪 Try a Proof of Concept

> **Purpose:** Let an engineer reproduce a specific, observable proxy behavior in under 5 minutes using the included LLM simulator — no real Azure OpenAI endpoint required. Each POC must be independently runnable and produce a verifiable outcome.

---

## Quick Answers

<table>
<tr>
<td width="33%" valign="top">

### What will I observe in failover?
When Backend A returns `429`, APIM marks it throttled and retries on Backend B. The client still sees `200 OK`. Verified via `x-Backend-Attempts: 2` and a changed `x-backend-affinity` header.

[→ POC: Failover](../POC-Failover-configuration.md)

</td>
<td width="33%" valign="top">

### What does priority routing prove?
A `llm_proxy_priority: 1` request routes only to backends whose `acceptablePriorities` includes priority 1. A request with no eligible backend returns `503`. Verified via `x-Backend-Attempts` and `backendLog`.

[→ POC: Priority Levels](../POC-Priority-configuration.md)

</td>
<td width="33%" valign="top">

### How does chargeback telemetry work?
Send a request with an `X-UserID` header. The proxy extracts token counts from the streaming response and logs them to Application Insights. Query by `X-UserID` to get per-user consumption.

[→ POC: Chargeback](../POC-Chargeback.md)

</td>
</tr>
<tr>
<td width="33%" valign="top">

### Do I need real Azure OpenAI?
No. The included LLM Simulator (an Azure Function) returns realistic OpenAI-format responses, simulates `429` throttling, and supports configurable latency — all without a real model endpoint.

[→ LLM Simulator](../DUMMY_BACKEND.md)

</td>
<td width="33%" valign="top">

### How do I verify a POC worked?
Each POC has a "What you will observe" section listing specific response headers (`x-Backend-Attempts`, `BackendHost`, `x-backend-affinity`) and, where relevant, App Insights queries.

> **⚠️ GAP:** No POC has a verification *checklist* (pass/fail signals independent of App Insights). → [Content gap details](#content-gaps-to-fill)

</td>
<td width="33%" valign="top">

### How do I secure the proxy?
The security POCs cover two layers: EasyAuth on the ACA proxy container (unauthenticated requests rejected before reaching the proxy), and `validate-jwt` in APIM for upstream caller validation.

[→ POC: Secure the Proxy](../POC-Secure-the-proxy.md)

</td>
</tr>
</table>

---

## Reader Profile

| | |
|---|---|
| **Who** | Engineers validating proxy behavior before going to production; platform engineers preparing a stakeholder demo; developers learning how a specific feature works |
| **Why they come here** | They want to see the behavior, not just read about it — they want to observe failover happen, see priority routing in action, or confirm chargeback telemetry is captured |
| **When they read this** | Pre-production validation; preparing a demo; learning by doing; verifying a new configuration |

---

## Questions each POC MUST answer

> Every POC file must answer all five questions. These are the content requirements for each scenario.

### Before I run it
- [ ] What will I observe? (exact observable outcomes, not "the proxy will route traffic")
  **Answer:** Each POC should name the exact pass or fail signals up front, such as `200 OK`, `202 Accepted`, `403`, `503`, `x-Backend-Attempts`, `x-backend-affinity`, or a specific telemetry record.
- [ ] What do I need running before I start? (simulator, config, Azure resources if any)
  **Answer:** A good POC should list only the concrete prerequisites for that scenario, usually the proxy, the simulator or backend, and any Azure services that the scenario truly depends on.
- [ ] How long will this take? (must be < 5 minutes for setup + execution)
  **Answer:** These POCs are intended to stay under five minutes once the base environment already exists.

### While I run it
- [ ] What are the exact commands to run?
  **Answer:** The POC should provide copy-paste-ready commands, usually `curl`, `az`, or policy snippets, so the reader does not have to translate prose into commands.
- [ ] What do I send to trigger the behavior?
  **Answer:** Send the one request that exercises the feature, such as a priority header, an `X-UserID`, an async opt-in header, or a route that forces failover.
- [ ] What should I see in the response headers / body / logs during each step?
  **Answer:** The reader should see explicit headers, statuses, bodies, or log lines that change in a predictable way at each step of the scenario.

### How do I verify it worked?
- [ ] What headers confirm the behavior? (e.g., `x-Backend-Attempts: 2` for failover)
  **Answer:** The most common confirmation headers are `x-Backend-Attempts`, `x-backend-affinity`, `BackendHost`, queue or process timing headers, and the final HTTP status code.
- [ ] What log entries or App Insights events confirm it? (exact field names and values)
  **Answer:** Verification should call out the exact `backendLog`, Application Insights field, or event dimension that proves the proxy took the expected path.
- [ ] What would I see if it did NOT work? (how to distinguish success from silent failure)
  **Answer:** A complete POC should also say what failure looks like, such as the wrong status code, a missing header change, or telemetry that never appears.

### Why did it happen?
- [ ] What setting(s) controlled the behavior I just observed?
  **Answer:** The scenario should name the small set of knobs that drove the result, such as backend priority lists, retry count, timeout, auth settings, or telemetry configuration.
- [ ] What is the state machine? (e.g., CLOSED → OPEN → recovery for circuit breaker)
  **Answer:** Every POC should explain the simple state transition behind the result, such as select → throttle → retry → recover or validate → reject versus validate → allow.

### What can I change?
- [ ] What variants can I try to see different behavior?
  **Answer:** Useful variants include changing priority, latency, retry count, auth mode, backend health, or the chosen backend set.
- [ ] What is the first thing I should change to adapt this to my own workload?
  **Answer:** The first adaptation is usually to replace the demo endpoints, headers, identities, and backend list with the real ones you plan to use in production.

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

## What the reader can do AFTER each POC

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
