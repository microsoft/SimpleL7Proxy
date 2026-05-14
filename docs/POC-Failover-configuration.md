# POC: Failover Configuration

## Overview

A minimal proof-of-concept showing how the **Priority-with-retry** policy fails over from a slow / unresponsive primary backend to a healthy secondary backend on timeout.

The primary endpoint is wired to a controllable **sleep function** that delays its response longer than the configured `Timeout`. The policy detects the timeout, marks the primary as throttling for 10 seconds, and the next retry cycle picks the secondary endpoint — all without the client seeing the failure.

## Goal

Demonstrate, end-to-end, that **either of the two real-world overload signals** trips the same failover path:

- **Use case 1 — Timeout:** Backend A delays beyond its `Timeout` → APIM cancels the forward → policy classifies `likelyTimeout=true`.
- **Use case 2 — `429 Too Many Requests`:** Backend A returns `429` with a `Retry-After` header → policy classifies `isTempError=true` and reads the header.

In both cases the sequence is the same:

1. A request is dispatched to **Backend A** (primary).
2. Backend A signals failure (timeout or `429`).
3. The policy marks Backend A `isThrottling=true` with a cool-down (`now + 10s` for the timeout path, `now + Retry-After + 2s` for the 429 path).
4. The retry loop re-selects → **Backend B** (secondary) is the only unthrottled candidate.
5. Backend B responds with `200 OK`. The client sees a single successful response (plus the `backendLog` header showing the failover).

The main configuration below drives **use case 1** (timeout) because it's trivial to reproduce with the `delay` function. The [Variant — drive the 429 path](#variant--drive-the-429-path-with-the-simulator) section at the end swaps Backend A to the simulator's `/api/error/429` endpoint to exercise **use case 2** with the exact same policy.

## Prerequisites

- An APIM instance with `Priority-with-retry-enhancedLog.xml` installed on the target API.
- **The `delay` Azure Function from [`functions/Delay.cs`](../../functions/Delay.cs) must be deployed before running this POC.** It is the only backend used by both endpoints. The function:
  - Route: `GET|POST /api/delay`
  - Auth level: anonymous
  - Query parameter `delay` = mean response delay **in milliseconds** (normal distribution around it; `0` returns immediately).
  - Returns a static text body (the novel sample) plus a `TOKENPROCESSOR` header.
- Deploy from the `functions/` folder (e.g. `func azure functionapp publish <funcapp>` or via the provided `deploy-flex.sh`). Note the resulting hostname — e.g. `https://<funcapp>.azurewebsites.net` — you'll use it in both backend entries below.
- Managed Identity is not required for this POC because the function uses anonymous auth; leave `api-key` blank.

## Policy Configuration

### 1. Backend list — same Azure Function, two different delay values

Both backends point at the **same deployed `delay` function**. The only differences are the `delay` query parameter (large for the primary, small for the secondary) and the policy-side `Timeout`. This isolates the failover behaviour to the policy itself — there is exactly one moving part (the function) under test.

Replace the `listBackends` initialization in `<inbound>`:

```xml
<set-variable name="listBackends" value="@{
    JArray backends = new JArray();
    string salt = "0123456789";

    // Backend A: primary — asks the delay function for ~15s before responding.
    // Policy Timeout is 5s, so the forward-request is cancelled and `likelyTimeout` trips.
    backends.Add(new JObject()
    {
        { "url", "https://<your-funcapp>.azurewebsites.net/api/delay?delay=15000" },
        { "priority", 1 },
        { "ModelType", "PAYGO" },
        { "acceptablePriorities", new JArray(1,2,3) },
        { "LimitConcurrency", "off" },
        { "BufferResponse", true },
        { "Timeout", 5 },                  // <-- primary times out after 5s
        { "api-key", "" }
    });

    // Backend B: secondary — same function, ~200ms delay, well within Timeout.
    backends.Add(new JObject()
    {
        { "url", "https://<your-funcapp>.azurewebsites.net/api/delay?delay=200" },
        { "priority", 2 },
        { "ModelType", "PAYGO" },
        { "acceptablePriorities", new JArray(1,2,3) },
        { "LimitConcurrency", "off" },
        { "BufferResponse", true },
        { "Timeout", 30 },
        { "api-key", "" }
    });

    foreach (JObject backend in backends) {
        string saltedUrl = salt + backend["url"].ToString();
        backend["affinity"] = string.Concat(
            System.Security.Cryptography.SHA256.Create()
            .ComputeHash(System.Text.Encoding.UTF8.GetBytes(saltedUrl))
            .Take(10)
            .Select(b => b.ToString("x2"))
        );
        backend["isThrottling"]      = false;
        backend["retryAfter"]        = DateTime.MinValue;
        backend["defaultRetryAfter"] = 10;
    }
    return backends;
}" />
```

Key settings:

| Field (Backend A) | Value | Why |
| :--- | :--- | :--- |
| `url` | `.../api/delay?delay=15000` | Function sleeps ~15s before responding. |
| `Timeout` | `5` | The forward-request is cancelled at 5s; `likelyTimeout` triggers because elapsed ≥ 0.9 × Timeout. |
| `priority` | `1` | Picked first when no backend is throttling. |
| `LimitConcurrency` | `off` | Keeps the POC focused on the timeout path. |

| Field (Backend B) | Value | Why |
| :--- | :--- | :--- |
| `url` | `.../api/delay?delay=200` | Same function, fast response. |
| `Timeout` | `30` | Comfortably exceeds the 200ms delay. |
| `priority` | `2` | Only picked after Backend A is marked throttling. |

### 2. Priority config — allow at least one retry

```xml
<set-variable name="priorityCfg" value="@{
    JObject cfg = new JObject();
    cfg["1"] = new JObject { { "retryCount", 2 }, { "requeue", false } };
    cfg["2"] = new JObject { { "retryCount", 2 }, { "requeue", false } };
    cfg["3"] = new JObject { { "retryCount", 2 }, { "requeue", false } };
    return cfg;
}" />
```

`retryCount: 2` is enough — one cycle is consumed by the timeout on Backend A; the next picks Backend B.

`requeue: false` keeps the POC simple: we don't want the policy returning `429 + S7PREQUEUE` while both backends are still considered. With only two endpoints this also avoids edge cases when Backend A is throttled but the retry budget is exhausted.

## How the Failover Plays Out

This sequence is encoded in the policy's `<backend><retry>` block:

1. **Cycle 1 — pick Backend A.** `backendIndex = 0` (Backend A is unthrottled and has priority 1). `CallAttemptStartTime` is stamped, `forward-request` is invoked with `timeout=5`.
2. **APIM cancels the forward at 5s.** `callCompleted` stays `false`. `context.Response` is null/incomplete.
3. **Timeout classification.** The `ErrorScenario` expression evaluates `deltaSeconds (≈5) ≥ Timeout × 0.9 (4.5)` → `likelyTimeout = true`. Backend A is updated: `isThrottling = true`, `retryAfter = UtcNow + 10s`. `activityLog` records `"Throttling [0] by 10s, likely timeout ..."`.
4. **ShouldRetry recomputed.** `isPermError = false`, `retryCount ≥ 0`, and `unThrottledBackends > 0` (Backend B is fine) → `ShouldRetry = true`. The retry loop iterates.
5. **Cycle 2 — pick Backend B.** `backendIndex = 1`. Backend B responds `200 OK` within its 30s timeout.
6. **Outbound.** Policy emits `x-Backend-Attempts: 2`, `x-backend-affinity: <hashOfBackendB>`, and a `backendLog` header summarizing both attempts.

### Timeout vs. 429 — same outcome in production

In normal use, an Azure OpenAI backend signals "stop sending traffic to me" in **two equivalent ways**, and this policy treats them as the same failover trigger:

| Backend signal | Where it's classified in the policy | Resulting state |
| :--- | :--- | :--- |
| **Forward times out** (no response within `Timeout`) | `ErrorScenario` sets `likelyTimeout` when elapsed ≥ `Timeout × 0.9` → `wasLimited=false`, `callCompleted=false` branch | `isThrottling=true`, `retryAfter = UtcNow + 10s` (hard-coded cool-down) |
| **`429 Too Many Requests`** with a `Retry-After` header | `isTempError` becomes true, falls into the `<when condition="isTempError">` block that parses `retry-after` from response headers | `isThrottling=true`, `retryAfter = UtcNow + (header value + 2s)` |

In both cases the next `<retry>` cycle sees Backend A as throttled, `unThrottledBackends` still > 0 (Backend B), and routes the request to Backend B — the client experience and the headers (`x-Backend-Attempts`, `backendLog`, etc.) are identical. The only differences are:

- A real `429` is **faster and cheaper** (no wasted timeout window) and the cool-down respects the backend's own `Retry-After` value.
- A timeout costs `Timeout` seconds of latency on the failed attempt and applies the policy's fixed 10-second throttle window.

This POC drives the timeout path because it's easy to reproduce with the `delay` function. In production traffic against Azure OpenAI you will most often see the `429` path; both flow through the same failover logic. To exercise the `429` path directly with the simulator, see [Variant — drive the 429 path](#variant--drive-the-429-path-with-the-simulator) below.

## Variant — drive the 429 path with the simulator

The LLM Simulator function (same `functions/` folder) exposes a built-in `429` endpoint that sets a real `Retry-After` header. Swap Backend A's URL to this endpoint and the policy follows the **second** failover path in the table above — same client experience, but failover happens in milliseconds instead of seconds.

### Backend A — point to `/api/error/429`

```jsonc
// Backend A: primary — returns 429 + Retry-After immediately.
// Policy classifies isTempError=true, reads Retry-After, marks throttling.
backends.Add(new JObject()
{
    { "url", "https://<your-funcapp>.azurewebsites.net/api/error/429?retryAfter=10" },
    { "priority", 1 },
    { "ModelType", "PAYGO" },
    { "acceptablePriorities", new JArray(1,2,3) },
    { "LimitConcurrency", "off" },
    { "BufferResponse", true },
    { "Timeout", 30 },                  // <-- no longer needs to be short
    { "api-key", "" }
});
```

Backend B stays unchanged (`/api/delay?delay=200`).

### What changes in the policy flow

| Step | Timeout variant (original) | 429 variant (this one) |
| :--- | :--- | :--- |
| Backend A response | None — APIM cancels at `Timeout=5s` | `429 Too Many Requests` returned in <100ms |
| Classification | `likelyTimeout=true` | `isTempError=true` |
| Cool-down value | Hard-coded 10s | Parsed from `Retry-After` header (`10 + 2 = 12s`) |
| Latency on Backend A attempt | ~5s | ~50–100ms |
| `x-Backend-Attempts` | `2` | `2` |
| Final response | `200 OK` from Backend B | `200 OK` from Backend B |

### Expected `backendLog` line for the failed attempt

```
Throttling [0] by 12s, isTempError=true, retry-after=10
```

vs. the timeout variant's:

```
Throttling [0] by 10s, likely timeout, deltaSeconds=5.0
```

### Why use the 429 variant

- **Production-realistic** — this is how Azure OpenAI actually signals overload.
- **Fast** — the failed attempt costs ~50ms instead of 5s, so a full POC sweep takes seconds.
- **Validates Retry-After parsing** — confirms the policy honours backend-supplied cool-down values, not just the hard-coded fallback.
- **Easy to vary** — append `?retryAfter=<seconds>` to the URL to test short (1s) or long (60s) throttle windows without re-deploying.


## Verifying the POC

### Request

The `delay` function accepts both `GET` and `POST`, so a plain `curl` is enough — no Azure OpenAI request body is required:

```bash
curl -i \
  -H "llm_proxy_priority: 1" \
  -H "Ocp-Apim-Subscription-Key: <your-key>" \
  "https://<your-apim>.azure-api.net/<your-api>/api/delay"
```

> The policy appends `/openai` to the backend URL in `<set-backend-service>`. For this POC the path the function actually receives is `/openai/api/delay` — the function ignores it and matches on the route, so the call still succeeds. If you prefer a clean path, remove the trailing `+ "/openai"` from the `backendUrl` set-variable for the duration of the POC.

### Expected response headers

| Header | Expected value | Meaning |
| :--- | :--- | :--- |
| `x-Backend-Attempts` | `2` | Two backends were called this request. |
| `x-backend-affinity` | hash of Backend B's URL | The successful backend is Backend B. |
| `x-PolicyCycleCounter` | `2` | Two cycles of the retry block executed. |
| `backendLog` | contains `Throttling [0] by 10s, likely timeout` followed by `Using PAYGO backend: https://<secondary>...` and `CALL SUCCESSFUL` | Step-by-step trace. |

### Expected status code

`200 OK` — the client never sees the timeout.

## Observing the Throttle Window

For 10 seconds after the timeout, Backend A stays marked `isThrottling=true`. Any request arriving in that window goes directly to Backend B on the **first** cycle (no wasted timeout). After `retryAfter` elapses, the top of the retry block flips Backend A back to `isThrottling=false`, so the next request again tries Backend A first — and the cycle repeats.

To watch this:

1. Fire request #1 → 2 attempts, `~5s` latency, succeeds on Backend B.
2. Fire request #2 within 10s → 1 attempt, fast, succeeds on Backend B (no timeout). `x-Backend-Attempts: 1`.
3. Wait > 10s, fire request #3 → 2 attempts again (Backend A is re-tried, times out, fails over).

## Cleanup / Tuning Knobs

- **Slower failover:** raise Backend A's `Timeout` to e.g. `20` (and bump `delay` to `25000`). The client now waits ~20s before seeing the response.
- **Faster failover:** set Backend A to `?delay=5000` with `Timeout: 2`.
- **Force a non-recoverable failure on Backend A:** point its URL to a non-routable host. The policy will still classify the attempt (via `ErrorScenario`) and fail over on the next cycle.
- **Test requeue path:** flip `requeue` to `true` and reduce `retryCount` to `0`. After the timeout, with no retries left, the policy returns `429 + S7PREQUEUE: true + retry-after-ms` — the exact signal SimpleL7Proxy uses to re-enqueue.
- **Vary the load:** the `delay` function uses a normal distribution (mean = your value, stdDev = 200ms). Send a burst of requests to see throttle/un-throttle transitions on Backend A.
## Notes

- `BufferResponse: true` on Backend A is appropriate for the POC; the policy does not stream from a timing-out endpoint.
- The 10-second throttle window is hard-coded in the policy (`backend["retryAfter"] = DateTime.UtcNow.AddSeconds(10)` inside the `ErrorScenario` timeout branch). Adjust there if a different cool-down is desired.
- This same mechanism is what powers the broader [high availability scenario](./high-availability-scenario.md) — this POC is the minimal two-node version of it.
