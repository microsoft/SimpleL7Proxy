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
  **Answer:** Capture the HTTP status code and response body first — proxy-generated errors include a plain-English reason in the body. Then call `/readiness` to see the proxy's current health state (including whether any backends are active). Check the response headers for `BackendHost`, `x-Backend-Attempts`, and any error headers the proxy added. If you need a full trace, check `eventslog.json` in the proxy's working directory — it writes one JSON record per request with timing, backend used, and status codes.
- [ ] What does the proxy tell me in the response body when something goes wrong?
  **Answer:** Proxy-generated error responses contain a JSON body with a plain-English reason. A `503` body also includes an `attempts` array where each entry shows the backend URL, the HTTP status code it returned (or the error type if the connection failed), and any error message. Reading the `attempts` array is usually faster than digging through logs and shows exactly which hosts were tried.

### Per-symptom questions (each guide must answer all of these)

#### Getting 503 Service Unavailable
- [ ] What does 503 mean in the context of this proxy? (all backends tried and failed)
  **Answer:** A proxy-generated `503` means the request worked through every eligible backend — those passing the [path filter](../Glossary.md#backend-management) and not blocked by an open [circuit breaker](../Glossary.md#reliability) — and none returned a success. Read the `attempts` array in the response body to see exactly which hosts were tried and what each returned. This is distinct from a backend directly returning `503`, which would appear as one entry in the same `attempts` array.
- [ ] How do I read the JSON error body to see which hosts were tried and what each returned?
  **Answer:** Read the `attempts` array in the response body, because each entry shows the backend host, its status code, and the error text for that attempt.
- [ ] Is this a circuit breaker problem or a real backend problem — how do I tell?
  **Answer:** Search the logs for lines containing `[CB-DELAY]` (the proxy is slowing traffic before the circuit trips) or `Circuit breaker BLOCKING` (the host is actively being skipped). Call `/readiness` — if active backend count is zero and the logs show circuit entries rather than probe-failure entries, it is a [circuit breaker](../Glossary.md#reliability) issue. A real backend failure shows up as an `attempts` entry in the `503` response body with an actual HTTP status code returned from the backend.
- [ ] How do I force the proxy to retry a specific host for diagnosis?
  **Answer:** Temporarily narrow routing so only that host matches the request path, then resend the request and observe the single-host behavior directly.
- [ ] What do I check after fixing it to confirm 503 is gone?
  **Answer:** Verify `/readiness` returns `200` and repeat the failing call until it succeeds without falling back to a `503` body.

#### Getting 429 Too Many Requests
- [ ] Is this a proxy 429 (queue full) or a backend 429 (throttled) — how do I tell?
  **Answer:** A proxy-generated `429` has no `BackendHost` response header (because no backend was contacted) and its JSON body explains the proxy-side reason: queue full, circuits all open, or no active hosts. A backend `429` will have a `BackendHost` header and the body comes from the backend itself. If the [requeue](../Glossary.md#reliability) mechanism is active, a backend `429` may be transparently requeued and the client will never see it.
- [ ] What setting controls when the queue rejects requests? (`MaxQueueLength`)
  **Answer:** `MaxQueueLength` is the setting that defines when the queue stops accepting new requests.
- [ ] How do I tell if a specific user or priority tier is being throttled?
  **Answer:** Check the user ID and priority headers involved in the request, then compare them with `UserPriorityThreshold`, `PriorityKeys`, and queue behavior in logs or telemetry.
- [ ] What do I do if the backend is returning 429 and I want the proxy to requeue instead of fail?
  **Answer:** Make the backend return `S7PREQUEUE: true` with a `retry-after` value so the proxy delays and requeues the request.

#### Getting 412 Precondition Failed
- [ ] What does 412 mean here? (TTL expired while waiting in the queue)
  **Answer:** A `412 Precondition Failed` means the request's total time budget ([TTL](../Glossary.md#request-lifecycle)) ran out before a worker picked it up and sent it to any backend. This usually means the queue is backed up — more requests are arriving than workers can process. The immediate fix is to raise `DefaultTTLSecs` so requests have more time to wait; the root-cause fix is to add more workers or reduce request volume.
- [ ] What is TTL and where does it come from? (default, or `S7PTTL` header)
  **Answer:** [TTL (Time-to-Live)](../Glossary.md#request-lifecycle) is the total wall-clock budget in seconds for a request, starting from when it enters the queue. The default comes from `DefaultTTLSecs` (default 300 seconds, or 5 minutes). Individual callers can override it per-request using the [`S7PTTL`](../Glossary.md#protocol-and-headers) header. Whichever value is smaller applies — a caller sending `S7PTTL: 10` will expire after 10 seconds even if `DefaultTTLSecs` is 300.
- [ ] How do I increase the TTL so requests don't expire?
  **Answer:** Raise `DefaultTTLSecs` globally or send a larger `S7PTTL` value on the requests that need more time.
- [ ] How do I tell if TTL is set incorrectly by the caller?
  **Answer:** Check the `x-Request-Queue-Duration` header in the `412` response (which shows how long the request waited) and compare it with the `S7PTTL` header value the caller sent. If the queue duration is close to the `S7PTTL` value and `S7PTTL` is much shorter than `DefaultTTLSecs`, the caller is sending an unexpectedly tight deadline that overrides the server default. If `S7PTTL` is absent, the request used `DefaultTTLSecs` as the budget.

#### Getting 400 Bad Request (InvalidTTL)
- [ ] What does `InvalidTTL` mean? (malformed TTL value in request header)
  **Answer:** `400 InvalidTTL` means the proxy could not parse the [`S7PTTL`](../Glossary.md#protocol-and-headers) header value. The three accepted formats are: a plain integer (seconds from now, e.g., `120`); a `+`-prefixed Unix timestamp in seconds marking the absolute deadline (e.g., `+1718000000`); or an ISO 8601 UTC datetime string. Any other format — including empty strings or non-numeric text — causes this error. See the question below for format examples.
- [ ] What is the correct format for the `S7PTTL` header?
  **Answer:** Three formats are accepted: (1) a plain integer meaning seconds from now — `S7PTTL: 120` means expire in 2 minutes; (2) a `+` prefix followed by a Unix timestamp in seconds marking the absolute deadline — `S7PTTL: +1718000000`; (3) an ISO 8601 UTC datetime string such as `S7PTTL: 2024-06-10T00:00:00Z`. The simplest and most readable format is a plain integer. Anything else causes `400 InvalidTTL`.
- [ ] How do I identify which callers are sending the bad header?
  **Answer:** Search APIM access logs or the proxy's request log for `S7PTTL` header values that are not plain integers and do not start with `+` or look like a datetime. In APIM, a misconfigured `set-header` policy is the most common source — check any policy that injects `S7PTTL` and verify its value expression produces a valid integer string.

#### Circuit breaker stuck OPEN
- [ ] How do I tell if a circuit is open? (which header or log field shows circuit state)
  **Answer:** Search the proxy logs for lines containing `[CB-DELAY]` (the proxy is adding delay before a host's circuit trips) or `Circuit breaker BLOCKING` (the host is actively being skipped). Call `/readiness` — an unhealthy response combined with circuit-related log entries rather than probe-failure entries confirms the [circuit breaker](../Glossary.md#reliability) is the issue. Note that a circuit opening and a host leaving the [active pool](../Glossary.md#backend-management) are two separate mechanisms that can independently stop traffic from reaching a backend.
- [ ] What causes a circuit to stay open longer than expected?
  **Answer:** The circuit stays open as long as the failure count within the last `CBTimeslice` seconds is still above `CBErrorThreshold`. Most common causes: the backend is still actively failing (verify with a direct `curl` from the same network); `CBErrorThreshold` is set too low for the backend's normal error rate; or `CBTimeslice` is large so historical failures take a long time to expire. See [→ Circuit Breaker](../Glossary.md#reliability).
- [ ] How do I manually reset a circuit or force a backend back into rotation?
  **Answer:** There is no manual reset command. The circuit closes automatically once failures older than `CBTimeslice` seconds expire and the count drops below `CBErrorThreshold`. Practical workarounds: fix the underlying backend issue and wait for the window to drain; temporarily lower `CBTimeslice` to drain old failures faster; or restart the proxy container to clear all in-memory circuit state immediately.
- [ ] How do I tune `CBErrorThreshold` and `CBTimeslice` to be less aggressive?
  **Answer:** Raise `CBErrorThreshold`, and if stale failures are hanging around too long, shorten `CBTimeslice` so the window reflects only recent behavior.

#### Async request never completes / 202 never issued
- [ ] What conditions must all be true for a 202 to be issued?
  **Answer:** Four conditions must all be true: (1) `AsyncModeEnabled=true` at the proxy level; (2) the request includes the async opt-in header (default name `S7PAsyncMode`); (3) the [user profile](../Glossary.md#request-governance) for that caller has an `async-config` block that enables async; and (4) the backend has **not** responded within `AsyncTriggerTimeout` milliseconds (default 10,000 ms) — if the backend responds faster than this threshold, the proxy returns a normal synchronous response and never issues `202`. All four must hold simultaneously. See [→ AsyncTriggerTimeout](../Glossary.md#async-mode).
- [ ] How do I tell if the proxy upgraded to async or is still processing synchronously?
  **Answer:** A `202 Accepted` response means the proxy released the HTTP connection and is continuing processing in the background. The response body includes a reference to where the result will be written — a Blob Storage URL, and optionally a Service Bus notification. A synchronous completion returns the backend's actual status code (`200`, `201`, etc.) directly — not `202`.
- [ ] How do I check if the blob storage container exists and has the right permissions?
  **Answer:** Check the proxy startup logs for `[BLOB]` connection messages. `AsyncBlobStorageConfig` is a semicolon-delimited connection string specifying the storage account endpoint and container name. The proxy's [Managed Identity](../Glossary.md#authentication-and-security) must have the `Storage Blob Data Contributor` role on that container — RBAC (Role-Based Access Control) is Azure's permission system. Verify by checking the managed identity's role assignments in the Azure portal or by attempting a direct blob write using the Azure CLI with that identity.
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
  **Answer:** The startup probe must have a timeout budget longer than one full backend poll cycle. The poll interval is 15 seconds by default, so give the startup probe at least 30–45 seconds. When [Sidecar Mode](../Glossary.md#observability) is enabled — a separate health-probe container running on port 9000 that shields the proxy from probe traffic — point all ACA probe targets at port `9000` instead of the proxy's main port.

#### Event Hub messages not arriving
- [ ] What configuration is required for Event Hub telemetry to work?
  **Answer:** `EVENT_LOGGERS` must include `eventhub`, and you must also set the Event Hub name plus either a connection string or a managed-identity namespace.
- [ ] How do I verify the connection string is correct and the namespace is reachable?
  **Answer:** Check startup for `[EVENT HUB]` connection messages and confirm the namespace, hub name, and sender role assignment are valid from the proxy environment.
- [ ] What does the proxy do if Event Hub is unreachable — does it fail requests or continue?
  **Answer:** If Event Hub is unreachable at startup, that telemetry sink is disabled for the life of the container but proxy request handling continues normally — all other configured sinks (Application Insights, local file) remain active. A connection error appears in startup logs under `[EVENT HUB]`. Request failures are never caused by telemetry sink failures.

#### App Configuration not loading or refreshing
- [ ] What RBAC role does the proxy's managed identity need?
  **Answer:** The proxy's [Managed Identity](../Glossary.md#authentication-and-security) needs the `App Configuration Data Reader` RBAC role on the App Configuration instance. RBAC (Role-Based Access Control) is Azure's permission system — this role grants read-only access to configuration keys. Without it, the proxy starts but cannot read settings from App Configuration and falls back to environment variables only.
- [ ] How do I tell if settings are coming from App Config or from environment variables?
  **Answer:** If `AZURE_APPCONFIG_ENDPOINT` and any App Configuration connection string are both unset, the proxy uses environment variables only. When App Configuration is connected, it reads keys with a `Warm:` or `Cold:` prefix that match the configured `AZURE_APPCONFIG_LABEL` — a label used to isolate one deployment's settings from another (for example, `production` vs `staging`). Startup logs show which source is active for each setting group.
- [ ] How do I force a settings reload without restarting the container?
  **Answer:** Change the Warm setting, then update `Warm:Sentinel` so every instance reloads its Warm values on the next refresh interval.
- [ ] What is the Sentinel key and what happens if it is missing?
  **Answer:** [`Warm:Sentinel`](../Glossary.md#configuration-management) is a key in Azure App Configuration whose only job is to signal running proxy instances that [Warm settings](../Glossary.md#configuration-management) have changed. Because the proxy polls rather than receiving push notifications, it needs this stable change signal. If `Warm:Sentinel` is missing or you update a Warm setting without also changing `Warm:Sentinel`, the new setting values will be loaded at the next container restart but will **not** hot-reload into currently running instances.

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
