# Content Brief: 🔧 Diagnose a Problem

> **Purpose:** Get someone from "something is broken" to "it is fixed and I know why" as fast as possible. Every guide must be symptom-first. Every guide must end with verification that the problem is gone.

---

## Quick Answers

<table>
<tr>
<td width="33%" valign="top">

### Getting 503 — all backends failed?
The proxy tried every backend and all failed. Read the JSON error body for per-attempt host + code details. Distinguish real backend failures from circuit-breaker skips by checking `/readiness` first.

[→ 503 diagnosis guide](../troubleshooting/requests-503.md)

</td>
<td width="33%" valign="top">

### Getting 429 — request rejected?
A proxy 429 means the request was rejected before any backend was tried. The response body tells you why: queue full, circuit breakers all open, or no active hosts. Each cause has a different fix.

[→ 429 diagnosis guide](../troubleshooting/requests-429.md)

</td>
<td width="33%" valign="top">

### Getting 412 — TTL expired?
The request waited in the queue longer than its `DefaultTTLSecs` budget (default 300 s). Increase `DefaultTTLSecs` or have callers set a longer `S7PTTL` header. Check for a backed-up queue first.

[→ 412 diagnosis guide](../troubleshooting/requests-412.md)

</td>
</tr>
<tr>
<td width="33%" valign="top">

### Circuit breaker stuck open?
The circuit self-heals when failures age out of the `CBTimeslice` window (default 60 s). If it stays open, backends are still actively failing. Check logs for `[CB-DELAY]` and `Circuit breaker BLOCKING` entries.

[→ Circuit breaker guide](../troubleshooting/circuit-breaker.md)

</td>
<td width="33%" valign="top">

### Async not returning 202?
Three gates must all be true: `AsyncModeEnabled=true`, the request carries the opt-in header (`S7PAsyncMode`), and the user profile has a valid `async-config` block. If the backend responds before `AsyncTriggerTimeout`, sync is expected.

[→ Async 202 guide](../troubleshooting/async-202-never-issued.md)

</td>
<td width="33%" valign="top">

### Where to start if unsure?
Use the symptom lookup table in `TroubleshootTOC.md`. Identify your HTTP status code or symptom, click the matching row, and follow the dedicated guide.

> **⚠️ GAP:** No "first 5 checks" block exists for when the symptom is unknown. → [Content gap details](#content-gaps-to-fill)

</td>
</tr>
</table>

---

## Reader Profile

| | |
|---|---|
| **Who** | On-call SREs, operators, any engineer whose proxy is misbehaving |
| **Why they come here** | Something broke right now or isn't doing what they expect |
| **When they read this** | Incident response; unexpected response codes; a backend stuck as unhealthy; async never completing |

---

## Questions the troubleshooting section MUST answer

### Entrypoint
- [ ] How do I find the right guide for my symptom without reading every doc? (symptom → guide lookup table)
- [ ] What is the fastest way to get a first-pass diagnosis? (which headers and logs to check first)
- [ ] What does the proxy tell me in the response body when something goes wrong?

### Per-symptom questions (each guide must answer all of these)

#### Getting 503 Service Unavailable
- [ ] What does 503 mean in the context of this proxy? (all backends tried and failed)
- [ ] How do I read the JSON error body to see which hosts were tried and what each returned?
- [ ] Is this a circuit breaker problem or a real backend problem — how do I tell?
- [ ] How do I force the proxy to retry a specific host for diagnosis?
- [ ] What do I check after fixing it to confirm 503 is gone?

#### Getting 429 Too Many Requests
- [ ] Is this a proxy 429 (queue full) or a backend 429 (throttled) — how do I tell?
- [ ] What setting controls when the queue rejects requests? (`MaxQueueLength`)
- [ ] How do I tell if a specific user or priority tier is being throttled?
- [ ] What do I do if the backend is returning 429 and I want the proxy to requeue instead of fail?

#### Getting 412 Precondition Failed
- [ ] What does 412 mean here? (TTL expired while waiting in the queue)
- [ ] What is TTL and where does it come from? (default, or `S7PTTL` header)
- [ ] How do I increase the TTL so requests don't expire?
- [ ] How do I tell if TTL is set incorrectly by the caller?

#### Getting 400 Bad Request (InvalidTTL)
- [ ] What does `InvalidTTL` mean? (malformed TTL value in request header)
- [ ] What is the correct format for the `S7PTTL` header?
- [ ] How do I identify which callers are sending the bad header?

#### Circuit breaker stuck OPEN
- [ ] How do I tell if a circuit is open? (which header or log field shows circuit state)
- [ ] What causes a circuit to stay open longer than expected?
- [ ] How do I manually reset a circuit or force a backend back into rotation?
- [ ] How do I tune `CBErrorThreshold` and `CBTimeslice` to be less aggressive?

#### Async request never completes / 202 never issued
- [ ] What conditions must all be true for a 202 to be issued?
- [ ] How do I tell if the proxy upgraded to async or is still processing synchronously?
- [ ] How do I check if the blob storage container exists and has the right permissions?
- [ ] How do I check if the Service Bus topic received the completion event?

#### Backend hosts not appearing in the healthy pool
- [ ] How do I tell which backends the proxy considers healthy at startup?
- [ ] What is the probe path and what happens if it is wrong?
- [ ] What does the proxy do if all hosts fail their probe at startup?
- [ ] How do I debug a probe failure without deploying a code change?

#### Health probes failing / pod restarting
- [ ] What are `/liveness`, `/readiness`, and `/startup` and what does each one check?
- [ ] What causes liveness to fail but readiness to pass (or vice versa)?
- [ ] How do I configure ACA health probe settings to match the proxy's startup time?

#### Event Hub messages not arriving
- [ ] What configuration is required for Event Hub telemetry to work?
- [ ] How do I verify the connection string is correct and the namespace is reachable?
- [ ] What does the proxy do if Event Hub is unreachable — does it fail requests or continue?

#### App Configuration not loading or refreshing
- [ ] What RBAC role does the proxy's managed identity need?
- [ ] How do I tell if settings are coming from App Config or from environment variables?
- [ ] How do I force a settings reload without restarting the container?
- [ ] What is the Sentinel key and what happens if it is missing?

---

## What the reader can do AFTER reading a troubleshooting guide

- [ ] Problem is identified (root cause, not just symptom)
- [ ] Fix has been applied
- [ ] Fix has been verified (the error is gone, the behavior is correct)
- [ ] Understands why the problem occurred so they can prevent it next time
- [ ] Knows which setting to change to reduce likelihood of recurrence

---

## Existing documents that cover this area

| Document | What it covers | Gap? |
|----------|----------------|------|
| [TroubleshootTOC.md](../TroubleshootTOC.md) | Symptom → guide index | Entry point — verify every symptom has a guide |
| [troubleshooting/requests-503.md](../troubleshooting/requests-503.md) | 503 diagnosis | Verify it answers all per-symptom questions above |
| [troubleshooting/requests-429.md](../troubleshooting/requests-429.md) | 429 diagnosis | Verify it distinguishes proxy 429 vs backend 429 |
| [troubleshooting/requests-412.md](../troubleshooting/requests-412.md) | 412 / TTL expiry | Verify it explains TTL mechanics clearly |
| [troubleshooting/requests-400-invalid-ttl.md](../troubleshooting/requests-400-invalid-ttl.md) | 400 / bad TTL format | Verify it shows the correct header format |
| [troubleshooting/circuit-breaker.md](../troubleshooting/circuit-breaker.md) | Stuck open circuit | Verify it explains how to read circuit state |
| [troubleshooting/async-requests.md](../troubleshooting/async-requests.md) | Async never completing | Verify it covers blob + Service Bus checks |
| [troubleshooting/async-202-never-issued.md](../troubleshooting/async-202-never-issued.md) | 202 not issued | Verify three-level enablement is explained |
| [troubleshooting/backend-hosts.md](../troubleshooting/backend-hosts.md) | Backends not in pool | Verify probe path error is covered |
| [troubleshooting/health-probes.md](../troubleshooting/health-probes.md) | Pod restarting | Verify ACA probe timing config is covered |
| [troubleshooting/event-hub.md](../troubleshooting/event-hub.md) | No Event Hub messages | Verify connection string and RBAC are covered |
| [troubleshooting/app-configuration.md](../troubleshooting/app-configuration.md) | App Config not loading | Verify Sentinel key and RBAC are covered |

---

## Content gaps to fill

- [ ] Every guide must end with a "Verification" checklist (not a table) — explicit pass/fail signals
- [ ] TroubleshootTOC.md should show the most common symptoms first, rare ones last
- [ ] Add a "first 5 checks" block at the top of TroubleshootTOC.md for when you don't know the symptom yet
- [ ] Distinguish proxy-generated error codes from backend pass-through codes in every guide
- [ ] Add a "what you will see in logs / App Insights" note to each guide so SREs can correlate
