# What the Proxy Is Telling You When Something Goes Wrong

Every unexpected status code is a signal. The proxy puts the reason right in the response — the hosts it tried, why each failed, and what to fix. Here's how to read it, organized by what you're seeing rather than what you already know.

---

## Quick Answers

<table>
<tr>
<td width="33%" valign="top">

### Getting 503 — all backends failed?
The proxy tried every backend and all failed. Read the JSON error body for per-attempt host + code details. Distinguish real backend failures from circuit-breaker skips by checking `/readiness` first.

[→ Getting 503 — all backends failed?](#getting-503--all-backends-failed-1)

</td>
<td width="33%" valign="top">

### Getting 429 — request rejected?
A proxy 429 means the request was rejected before any backend was tried. The response body tells you why: queue full, circuit breakers all open, or no active hosts. Each cause has a different fix.

[→ Getting 429 — request rejected?](#getting-429--request-rejected-1)

</td>
<td width="33%" valign="top">

### Getting 412 — TTL expired?
The request waited in the queue longer than its `DefaultTTLSecs` budget (default 300 s). Increase `DefaultTTLSecs` or have callers set a longer `S7PTTL` header. Check for a backed-up queue first.

[→ Getting 412 — TTL expired?](#getting-412--ttl-expired-1)

</td>
</tr>
<tr>
<td width="33%" valign="top">

### Circuit breaker stuck open?
The circuit self-heals when failures age out of the `CBTimeslice` window (default 60 seconds). If it stays open, backends are still actively failing. Check logs for `[CB-DELAY]` and `Circuit breaker BLOCKING` entries.

[→ Circuit breaker stuck open?](#circuit-breaker-stuck-open-1)

</td>
<td width="33%" valign="top">

### Async not returning 202?
Four conditions must all be true: `AsyncModeEnabled=true`, the request carries the opt-in header (`S7PAsyncMode`), the user profile has a valid `async-config` block, and the backend hasn't responded within `AsyncTriggerTimeout` milliseconds. If the backend replies before that timeout, the proxy returns a normal synchronous response.

[→ Async not returning 202?](#async-not-returning-202-1)

</td>
<td width="33%" valign="top">

### Where to start if unsure?
Use the symptom lookup table in `TroubleshootTOC.md`. Identify your HTTP status code or symptom, click the matching row, and follow the dedicated guide.

[→ Where to start if unsure?](#where-to-start-if-unsure-1)

</td>
</tr>
</table>

---

## Full Answers

### Where to start if unsure?

#### How do I find the right guide for my symptom without reading every doc? (symptom → guide lookup table)

The troubleshooting guides are organized by symptom. Start with `TroubleshootTOC.md` — it maps the most common HTTP status codes and behaviors directly to the dedicated guide.

#### What is the fastest way to get a first-pass diagnosis? (which headers and logs to check first)

The answer is often in the response itself. Check the HTTP status code and response body first — proxy-generated errors include a plain-English reason. Then call `/readiness` to see current backend health. Check response headers: `BackendHost` (which backend was used), `x-Backend-Attempts` (how many backends were tried), and any error headers. For a full trace, check `eventslog.json` in the working directory — one JSON record per request with timing, backend used, and status codes.

#### What does the proxy tell me in the response body when something goes wrong?

On proxy-side failures, a JSON error body is returned. A `503` body includes an `attempts` array where each entry shows the backend URL, the HTTP status code it returned, and any error message. Reading `attempts` is usually faster than searching logs and shows exactly which hosts were tried and why each failed.

**Example `503` body:**
```json
{
  "error": "All backends failed",
  "attempts": [
    { "host": "https://api1.example.com", "status": 500, "message": "Internal Server Error" },
    { "host": "https://api2.example.com", "status": 503, "message": "Service Unavailable" }
  ]
}
```

---

### Getting 503 — all backends failed?

#### What does 503 mean in the context of this proxy? (all backends tried and failed)

A `503` means every eligible backend was tried — those passing the [path filter](../Glossary.md#backend-management) and not blocked by an open [circuit breaker](../Glossary.md#reliability) — and none returned a success. Read the `attempts` array in the response body to see exactly which hosts were tried and what each returned. A backend directly returning `503` appears as one entry in that same array.

#### How do I read the JSON error body to see which hosts were tried and what each returned?

SimpleL7Proxy includes the `attempts` array in the `503` response body. Each entry shows the backend host, its status code, and the error text for that attempt. This is the fastest way to see whether a real backend error or a circuit breaker skip caused the failure.

#### Is this a circuit breaker problem or a real backend problem — how do I tell?

SimpleL7Proxy logs circuit breaker activity distinctly. Search the logs for `[CB-DELAY]` (delay before the circuit trips) or `Circuit breaker BLOCKING` (host actively skipped). Call `/readiness` — zero active backends combined with circuit-related log entries (rather than probe-failure entries) indicates a circuit breaker issue. A real backend failure shows an actual HTTP status code in the `attempts` entry.

#### How do I force the proxy to retry a specific host for diagnosis?

SimpleL7Proxy routes by `path=`. Temporarily narrow routing so only that host matches the request path, then resend the request to observe single-host behavior directly.

#### What do I check after fixing it to confirm 503 is gone?

SimpleL7Proxy's `/readiness` endpoint reflects backend health. Verify it returns `200` and repeat the failing call until it succeeds without the `503` body.

---

### Getting 429 — request rejected?

#### Is this a proxy 429 (queue full) or a backend 429 (throttled) — how do I tell?

SimpleL7Proxy generates its own `429` before contacting any backend when the queue is full, all circuits are open, or no active hosts are available. A proxy `429` has **no `BackendHost` response header** and its JSON body explains the proxy-side reason. A backend `429` has a `BackendHost` header and the body comes from the backend itself. If [requeue](../Glossary.md#reliability) is active, a backend `429` may be transparently requeued and the client will never see it.

| Signal | Proxy `429` | Backend `429` |
|--------|------------|---------------|
| `BackendHost` header | Absent | Present |
| Response body | Proxy JSON reason | Backend response |
| Fix | Increase `MaxQueueLength` or workers | Configure requeue or add backends |

#### What setting controls when the queue rejects requests? (`MaxQueueLength`)

SimpleL7Proxy stops accepting new requests when the queue reaches `MaxQueueLength` (default 1000) and returns `429` immediately.

#### How do I tell if a specific user or priority tier is being throttled?

SimpleL7Proxy enforces per-user limits via `UserPriorityThreshold`. Check the user ID and priority headers in the rejected request and compare them with `UserPriorityThreshold`, `PriorityKeys`, and queue depth in logs or telemetry.

#### What do I do if the backend is returning 429 and I want the proxy to requeue instead of fail?

SimpleL7Proxy supports transparent requeue when the backend returns `S7PREQUEUE: true` with a `retry-after` value. Configure the backend (or the APIM policy in front of it) to add this header on `429` responses.

---

### Getting 412 — TTL expired?

#### What does 412 mean here? (TTL expired while waiting in the queue)

SimpleL7Proxy returns `412 Precondition Failed` when a request's total time budget ([TTL](../Glossary.md#request-lifecycle)) runs out before a worker picks it up. This usually means the queue is backed up — more requests are arriving than workers can process. The immediate fix is to raise `DefaultTTLSecs`; the root-cause fix is more workers or lower request volume.

#### What is TTL and where does it come from? (default, or `S7PTTL` header)

SimpleL7Proxy assigns each request a TTL starting when it enters the queue. The default is `DefaultTTLSecs` (default 300 seconds). Individual callers can override per-request using the [`S7PTTL`](../Glossary.md#protocol-and-headers) header. The smaller value applies — a caller sending `S7PTTL: 10` expires after 10 seconds even if `DefaultTTLSecs` is 300.

#### How do I increase the TTL so requests don't expire?

SimpleL7Proxy respects a higher `DefaultTTLSecs` globally, or callers can send a larger `S7PTTL` value per request.

#### How do I tell if TTL is set incorrectly by the caller?

SimpleL7Proxy adds `x-Request-Queue-Duration` to the `412` response, showing how long the request waited. Compare it with the `S7PTTL` the caller sent. If queue duration ≈ `S7PTTL` and `S7PTTL` is much shorter than `DefaultTTLSecs`, the caller is sending an unexpectedly tight deadline.

---

### Getting 400 — InvalidTTL

#### What does `InvalidTTL` mean? (malformed TTL value in request header)

SimpleL7Proxy returns `400 InvalidTTL` when it cannot parse the [`S7PTTL`](../Glossary.md#protocol-and-headers) header value. Three formats are accepted: a plain integer (seconds from now), a `+`-prefixed Unix timestamp, or an ISO 8601 UTC datetime string. Any other format — including empty strings or non-numeric text — causes this error.

#### What is the correct format for the `S7PTTL` header?

SimpleL7Proxy accepts exactly three `S7PTTL` formats:

| Format | Example | Meaning |
|--------|---------|---------|
| Plain integer | `S7PTTL: 120` | Expire 120 seconds from now |
| `+` Unix timestamp | `S7PTTL: +1718000000` | Expire at that absolute epoch second |
| ISO 8601 UTC | `S7PTTL: 2024-06-10T00:00:00Z` | Expire at that UTC datetime |

The plain integer is the simplest and most readable. Anything else causes `400 InvalidTTL`.

#### How do I identify which callers are sending the bad header?

SimpleL7Proxy logs the bad value. Search APIM access logs or `eventslog.json` for `S7PTTL` values that are not integers, `+` timestamps, or ISO datetimes. A misconfigured `set-header` policy in APIM is the most common source.

---

### Circuit breaker stuck open?

#### How do I tell if a circuit is open? (which header or log field shows circuit state)

SimpleL7Proxy logs circuit breaker state changes. Search logs for `[CB-DELAY]` (delay before tripping) or `Circuit breaker BLOCKING` (host actively skipped). Call `/readiness` — an unhealthy response combined with circuit log entries (not probe-failure entries) confirms the [circuit breaker](../Glossary.md#reliability) is open.

#### What causes a circuit to stay open longer than expected?

SimpleL7Proxy's circuit stays open until the failure count within the last `CBTimeslice` seconds drops below `CBErrorThreshold`. Common causes: the backend is still failing (verify with a direct `curl` from the same network); `CBErrorThreshold` is set too low for the backend's normal error rate; or `CBTimeslice` is long so historical failures take time to expire.

#### How do I manually reset a circuit or force a backend back into rotation?

SimpleL7Proxy has no manual circuit reset. The circuit closes automatically as failures older than `CBTimeslice` seconds expire. Workarounds: fix the backend issue and wait; temporarily lower `CBTimeslice`; or restart the container to clear all in-memory circuit state immediately.

#### How do I tune `CBErrorThreshold` and `CBTimeslice` to be less aggressive?

SimpleL7Proxy becomes less aggressive when `CBErrorThreshold` is raised (tolerates more failures before opening) or `CBTimeslice` is shortened (old failures expire faster).

---

### Async not returning 202?

#### What conditions must all be true for a 202 to be issued?

SimpleL7Proxy issues `202` only when all four conditions hold simultaneously:
1. `AsyncModeEnabled=true` at the proxy level
2. The request includes the async opt-in header (default `S7PAsyncMode`)
3. The [user profile](../Glossary.md#request-governance) for that caller has an `async-config` block
4. The backend has **not** responded within `AsyncTriggerTimeout` milliseconds (default 10,000 ms) — if the backend responds faster, the proxy returns a normal synchronous response

See [→ AsyncTriggerTimeout](../Glossary.md#async-mode).

#### How do I tell if the proxy upgraded to async or is still processing synchronously?

SimpleL7Proxy returns `202 Accepted` when it releases the HTTP connection to process in the background. The response body includes the Blob Storage URL where results will be written and, optionally, a Service Bus notification reference. A synchronous response returns the backend's actual status code (`200`, `201`, etc.) directly.

#### How do I check if the blob storage container exists and has the right permissions?

SimpleL7Proxy logs Blob Storage connection attempts at startup under `[BLOB]`. The managed identity must have `Storage Blob Data Contributor` on the container. Verify via Azure portal role assignments or `az storage blob upload` with that identity.

#### How do I check if the Service Bus topic received the completion event?

SimpleL7Proxy uses the `AsyncSBConfig` connection string to send completion events. Verify the topic/subscription name and that the managed identity has the `Azure Service Bus Data Sender` role.

---

### Backend hosts not appearing in the healthy pool

#### How do I tell which backends the proxy considers healthy at startup?

SimpleL7Proxy reports active backend count in `/readiness` and startup logs. Call `/readiness` immediately after startup — zero active hosts means no backend passed its initial probe.

#### What is the probe path and what happens if it is wrong?

SimpleL7Proxy marks a backend unhealthy and removes it from the active pool when its probe path does not return a success response (HTTP 200–299). A wrong path — a `404` or `401` — has the same effect as a network failure.

#### What does the proxy do if all hosts fail their probe at startup?

SimpleL7Proxy starts with zero active hosts, `/readiness` returns unhealthy, and all incoming requests are rejected until at least one backend passes its probe or a `mode=direct` backend is configured.

#### How do I debug a probe failure without deploying a code change?

SimpleL7Proxy logs probe results. Curl the backend probe URL directly from the same network, then verify the `HostN` connection string, `PollTimeout`, and `SuccessRate` settings match the backend's behavior.

---

### Health probes failing / pod restarting

#### What are `/liveness`, `/readiness`, and `/startup` and what does each one check?

SimpleL7Proxy exposes three health endpoints: `/liveness` confirms the process is alive; `/readiness` confirms the proxy can safely take traffic (at least one backend healthy); `/startup` confirms startup and the first backend poll cycle have finished.

#### What causes liveness to fail but readiness to pass (or vice versa)?

SimpleL7Proxy's liveness and readiness can diverge. Liveness failures indicate process stalls or sidecar update problems. Readiness failures with liveness still healthy usually mean the process is up but no backend is ready.

#### How do I configure ACA health probe settings to match the proxy's startup time?

SimpleL7Proxy's startup probe must have a timeout budget longer than one full backend poll cycle (15 seconds by default). Give the startup probe at least 30–45 seconds. When [Sidecar Mode](../Glossary.md#observability) is enabled — a separate health-probe container on port 9000 — point all ACA probe targets at port `9000`.

---

### Event Hub messages not arriving

#### What configuration is required for Event Hub telemetry to work?

SimpleL7Proxy requires `EVENT_LOGGERS` to include `eventhub`, plus the Event Hub name and either a connection string or a managed-identity namespace.

#### How do I verify the connection string is correct and the namespace is reachable?

SimpleL7Proxy logs Event Hub connection attempts at startup under `[EVENT HUB]`. Verify the namespace, hub name, and sender role assignment are valid from the proxy's network environment.

#### What does the proxy do if Event Hub is unreachable — does it fail requests or continue?

SimpleL7Proxy disables that telemetry sink at startup if Event Hub is unreachable, but request handling continues normally — all other configured sinks (Application Insights, local file) remain active. Request failures are never caused by telemetry sink failures.

---

### App Configuration not loading or refreshing

#### What RBAC role does the proxy's managed identity need?

SimpleL7Proxy reads App Configuration using its managed identity, which must have the `App Configuration Data Reader` role on the App Configuration instance. Without it, the proxy starts but falls back to environment variables only.

#### How do I tell if settings are coming from App Config or from environment variables?

SimpleL7Proxy uses environment variables only when `AZURE_APPCONFIG_ENDPOINT` is unset. When App Configuration is connected, it reads keys prefixed with `Warm:` or `Cold:` matching `AZURE_APPCONFIG_LABEL`. Startup logs show which source is active for each setting group.

#### How do I force a settings reload without restarting the container?

SimpleL7Proxy hot-reloads Warm settings when `Warm:Sentinel` changes. Update the setting, then bump `Warm:Sentinel` — all running instances reload within ~30 seconds.

#### What is the Sentinel key and what happens if it is missing?

SimpleL7Proxy polls App Configuration on a ~30-second interval and only reloads when [`Warm:Sentinel`](../Glossary.md#configuration-management) changes. If `Warm:Sentinel` is missing or you update a Warm setting without changing it, the new values won't hot-reload into running instances — they take effect only at the next container restart.

---

## You Should Now Be Able To

- [ ] Problem is identified (root cause, not just symptom)
- [ ] Fix has been applied
- [ ] Fix has been verified (the error is gone, the behavior is correct)
- [ ] Understands why the problem occurred so they can prevent it next time
- [ ] Knows which setting to change to reduce likelihood of recurrence

---

## Related Documents

| Document | What it covers |
|----------|----------------|
| [Troubleshooting Index](../TroubleshootTOC.md) | Symptom-to-guide lookup |
| [503 Responses](../troubleshooting/requests-503.md) | All eligible backends failing |
| [429 Responses](../troubleshooting/requests-429.md) | Proxy rejection versus backend throttling |
| [412 Responses](../troubleshooting/requests-412.md) | Request TTL expiry |
| [400 Invalid TTL](../troubleshooting/requests-400-invalid-ttl.md) | Invalid `S7PTTL` header formats |
| [Circuit Breaker](../troubleshooting/circuit-breaker.md) | A backend circuit remaining open |
| [Async Requests](../troubleshooting/async-requests.md) | Async requests that do not complete |
| [Async 202](../troubleshooting/async-202-never-issued.md) | Async requests that remain synchronous |
| [Backend Hosts](../troubleshooting/backend-hosts.md) | Backends missing from the healthy pool |
| [Health Probes](../troubleshooting/health-probes.md) | Probe failures and container restarts |
| [Event Hub](../troubleshooting/event-hub.md) | Missing telemetry messages |
| [App Configuration](../troubleshooting/app-configuration.md) | Settings that do not load or refresh |

---
