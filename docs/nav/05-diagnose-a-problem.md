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
  **Answer:** Start with `TroubleshootTOC.md`, because it maps the most common symptoms directly to the dedicated troubleshooting guide.
- [ ] What is the fastest way to get a first-pass diagnosis? (which headers and logs to check first)
  **Answer:** Capture the HTTP status and body first, check `/readiness`, and then inspect the proxy-added response headers and `eventslog.json` or telemetry for the same request.
- [ ] What does the proxy tell me in the response body when something goes wrong?
  **Answer:** Proxy-generated errors usually include a concrete reason, and `503` responses can also include an attempts list that shows which backends were tried.

### Per-symptom questions (each guide must answer all of these)

#### Getting 503 Service Unavailable
- [ ] What does 503 mean in the context of this proxy? (all backends tried and failed)
  **Answer:** A proxy `503` means the request exhausted every eligible backend and none of them completed successfully.
- [ ] How do I read the JSON error body to see which hosts were tried and what each returned?
  **Answer:** Read the `attempts` array in the response body, because each entry shows the backend host, its status code, and the error text for that attempt.
- [ ] Is this a circuit breaker problem or a real backend problem — how do I tell?
  **Answer:** If readiness shows open circuits or the logs show hosts being blocked before calls are made, it is a circuit issue, while real backend failures show actual attempt entries and backend status codes.
- [ ] How do I force the proxy to retry a specific host for diagnosis?
  **Answer:** Temporarily narrow routing so only that host matches the request path, then resend the request and observe the single-host behavior directly.
- [ ] What do I check after fixing it to confirm 503 is gone?
  **Answer:** Verify `/readiness` returns `200` and repeat the failing call until it succeeds without falling back to a `503` body.

#### Getting 429 Too Many Requests
- [ ] Is this a proxy 429 (queue full) or a backend 429 (throttled) — how do I tell?
  **Answer:** A proxy `429` explains queue, circuit, or host-availability problems in its body before any backend call, while a backend `429` appears only after a backend attempt or requeue decision.
- [ ] What setting controls when the queue rejects requests? (`MaxQueueLength`)
  **Answer:** `MaxQueueLength` is the setting that defines when the queue stops accepting new requests.
- [ ] How do I tell if a specific user or priority tier is being throttled?
  **Answer:** Check the user ID and priority headers involved in the request, then compare them with `UserPriorityThreshold`, `PriorityKeys`, and queue behavior in logs or telemetry.
- [ ] What do I do if the backend is returning 429 and I want the proxy to requeue instead of fail?
  **Answer:** Make the backend return `S7PREQUEUE: true` with a `retry-after` value so the proxy delays and requeues the request.

#### Getting 412 Precondition Failed
- [ ] What does 412 mean here? (TTL expired while waiting in the queue)
  **Answer:** A `412` means the request's TTL expired before a worker could send it to any backend.
- [ ] What is TTL and where does it come from? (default, or `S7PTTL` header)
  **Answer:** TTL is the total request lifetime budget, and it comes from `DefaultTTLSecs` unless the caller overrides it with `S7PTTL`.
- [ ] How do I increase the TTL so requests don't expire?
  **Answer:** Raise `DefaultTTLSecs` globally or send a larger `S7PTTL` value on the requests that need more time.
- [ ] How do I tell if TTL is set incorrectly by the caller?
  **Answer:** Compare the request's queue duration with the actual `S7PTTL` value the caller sent, because a small caller override will beat the server default every time.

#### Getting 400 Bad Request (InvalidTTL)
- [ ] What does `InvalidTTL` mean? (malformed TTL value in request header)
  **Answer:** `InvalidTTL` means the proxy could not parse the TTL header value into any supported format.
- [ ] What is the correct format for the `S7PTTL` header?
  **Answer:** Use a relative number of seconds, a `+`-prefixed Unix epoch second, or a parseable UTC datetime such as ISO 8601.
- [ ] How do I identify which callers are sending the bad header?
  **Answer:** Inspect client, APIM, or proxy request logs for the `S7PTTL` header value and trace which caller or policy generated the malformed string.

#### Circuit breaker stuck OPEN
- [ ] How do I tell if a circuit is open? (which header or log field shows circuit state)
  **Answer:** A failing `/readiness` probe and log lines such as `[CB-DELAY]` or `Circuit breaker BLOCKING` are the clearest signs that a circuit is open.
- [ ] What causes a circuit to stay open longer than expected?
  **Answer:** It usually stays open because the backend is still failing, the threshold is too low, or `CBTimeslice` is so large that old failures remain in the window.
- [ ] How do I manually reset a circuit or force a backend back into rotation?
  **Answer:** The docs do not describe a manual reset path, so the normal fix is to restore the backend or retune the circuit settings and let the sliding window age out.
- [ ] How do I tune `CBErrorThreshold` and `CBTimeslice` to be less aggressive?
  **Answer:** Raise `CBErrorThreshold`, and if stale failures are hanging around too long, shorten `CBTimeslice` so the window reflects only recent behavior.

#### Async request never completes / 202 never issued
- [ ] What conditions must all be true for a 202 to be issued?
  **Answer:** `AsyncModeEnabled` must be true, the request must carry the async opt-in header, the user's `async-config` must be enabled, and the request must outlast `AsyncTriggerTimeout`.
- [ ] How do I tell if the proxy upgraded to async or is still processing synchronously?
  **Answer:** A real async upgrade returns `202 Accepted` plus async result information, while a normal sync completion returns the final backend status directly.
- [ ] How do I check if the blob storage container exists and has the right permissions?
  **Answer:** Verify `AsyncBlobStorageConfig`, the user's configured container name, and the managed identity or connection string permissions for blob read and write access.
- [ ] How do I check if the Service Bus topic received the completion event?
  **Answer:** Confirm `AsyncSBConfig` is correct and that the caller's configured topic or subscription actually receives the expected async status events.

#### Backend hosts not appearing in the healthy pool
- [ ] How do I tell which backends the proxy considers healthy at startup?
  **Answer:** Use `/readiness` and startup logs to see whether the active host count is nonzero and whether a backend made it into the healthy pool.
- [ ] What is the probe path and what happens if it is wrong?
  **Answer:** The probe path is the backend health URL, and if it does not return `2xx` the host is marked unhealthy and removed from the active set.
- [ ] What does the proxy do if all hosts fail their probe at startup?
  **Answer:** It starts with zero active hosts, readiness stays unhealthy, and requests are rejected until a host becomes healthy or direct mode is used.
- [ ] How do I debug a probe failure without deploying a code change?
  **Answer:** Curl the backend probe directly from the same network, then verify the `HostN` string, `PollTimeout`, and success-rate settings.

#### Health probes failing / pod restarting
- [ ] What are `/liveness`, `/readiness`, and `/startup` and what does each one check?
  **Answer:** `/liveness` checks whether the process is alive, `/readiness` checks whether the proxy can safely take traffic, and `/startup` checks whether startup and the first backend poll cycle have finished.
- [ ] What causes liveness to fail but readiness to pass (or vice versa)?
  **Answer:** Liveness failures point to process stalls or sidecar update problems, while readiness failures with liveness still healthy usually mean the app is up but no backend is ready.
- [ ] How do I configure ACA health probe settings to match the proxy's startup time?
  **Answer:** Give the startup probe a budget longer than one full backend poll cycle and point probes to port `9000` when the sidecar mode is enabled.

#### Event Hub messages not arriving
- [ ] What configuration is required for Event Hub telemetry to work?
  **Answer:** `EVENT_LOGGERS` must include `eventhub`, and you must also set the Event Hub name plus either a connection string or a managed-identity namespace.
- [ ] How do I verify the connection string is correct and the namespace is reachable?
  **Answer:** Check startup for `[EVENT HUB]` connection messages and confirm the namespace, hub name, and sender role assignment are valid from the proxy environment.
- [ ] What does the proxy do if Event Hub is unreachable — does it fail requests or continue?
  **Answer:** The Event Hub backend is disabled and other sinks continue, so request handling continues even though Event Hub telemetry is missing.

#### App Configuration not loading or refreshing
- [ ] What RBAC role does the proxy's managed identity need?
  **Answer:** The managed identity needs the `App Configuration Data Reader` role.
- [ ] How do I tell if settings are coming from App Config or from environment variables?
  **Answer:** If no App Configuration endpoint or connection string is set the proxy uses environment variables only, and when App Configuration is connected it reads matching `Warm:` and `Cold:` keys for the active label.
- [ ] How do I force a settings reload without restarting the container?
  **Answer:** Change the Warm setting, then update `Warm:Sentinel` so every instance reloads its Warm values on the next refresh interval.
- [ ] What is the Sentinel key and what happens if it is missing?
  **Answer:** `Warm:Sentinel` is the hot-reload trigger, and if it is missing or never changes Warm settings will not refresh automatically at runtime.

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
