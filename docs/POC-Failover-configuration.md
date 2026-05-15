# POC: Failover Configuration

## Overview

This document walks through a minimal, self-contained proof-of-concept for the **Priority-with-retry** APIM policy. The goal is to show exactly what happens when a primary backend becomes unavailable — and to give you a working setup you can run yourself in a few minutes.

The POC uses a controllable delay function as the primary backend. By making it respond slower than the policy's configured `Timeout`, you can watch the failover sequence happen in real time: the policy cancels the forward, marks the primary as throttling, and routes the next attempt to a healthy secondary — all without the client seeing a failure. Everything is observable through response headers.

## Goal

One thing worth understanding early: in production, Azure OpenAI signals overload in two different ways, and this policy handles both identically. This POC demonstrates both paths so you can verify the behaviour before depending on it.

The two real-world overload signals that trip the same failover path:

- **Use case 1 — Timeout:** Backend A delays beyond its `Timeout` → APIM cancels the forward → policy classifies `likelyTimeout=true`.
- **Use case 2 — `429 Too Many Requests`:** Backend A returns `429` with a `Retry-After` header → policy classifies `isTempError=true` and reads the header.

In both cases the sequence is the same:

1. A request is dispatched to **Backend A** (primary).
2. Backend A signals failure (timeout or `429`).
3. The policy marks Backend A `isThrottling=true` with a cool-down (`now + 10s` for the timeout path, `now + Retry-After + 2s` for the 429 path).
4. The retry loop re-selects → **Backend B** (secondary) is the only unthrottled candidate.
5. Backend B responds with `200 OK`. The client sees a single successful response (plus the `backendLog` header showing the failover).

The main configuration below drives **use case 1** (timeout) — it's the easiest to reproduce because the delay function gives you full control over latency. The [Variant — drive the 429 path](#variant--drive-the-429-path-with-the-simulator) section swaps Backend A to the LLM Simulator's `/api/error/429` endpoint to run **use case 2** against the exact same policy, which is faster and closer to what you'll see in production.

## Applying the policy to APIM

The policy file is [`APIM-Policy/Priority-with-retry-enhancedLog.xml`](../../APIM-Policy/Priority-with-retry-enhancedLog.xml). It needs to be applied at the **API level** (not product or global) on the API you want to proxy through.

**Azure portal:**
1. Open your APIM instance → **APIs** → select the target API.
2. Select **All operations** in the left panel (so the policy applies to every operation on the API).
3. Click the `</>` icon in the **Inbound processing** tile to open the policy editor.
4. Replace the entire contents of the editor with the contents of `Priority-with-retry-enhancedLog.xml`.
5. Click **Save**.

**Azure CLI / ARM / Bicep** — if you prefer scripted deployment:
```bash
az apim api policy create \
  --resource-group <rg> \
  --service-name <apim-name> \
  --api-id <api-id> \
  --value "$(cat APIM-Policy/Priority-with-retry-enhancedLog.xml)" \
  --format xml
```

After saving, the `listBackends` variable in the `<inbound>` block is what you'll edit to point at your backends. The next section covers the exact values for this POC.

## Prerequisites

- An APIM instance with `Priority-with-retry-enhancedLog.xml` installed on the target API (see above).
- **The LLM Simulator Azure Function must be deployed before running this POC.** It provides both backends — the slow `/api/delay` endpoint for the timeout path and the `/api/error/429` endpoint for the 429 variant. See [`functions/Readme.md`](../../functions/Readme.md) for the fastest way to get it running (portal ZIP deploy, no build required). The relevant endpoints:
  - `GET|POST /api/delay?delay=<ms>` — anonymous auth, returns a response after approximately `delay` milliseconds (normal distribution, stddev ~200ms).
  - `GET|POST /api/error/429?retryAfter=<sec>` — anonymous auth, returns `429` immediately with a real `Retry-After` header.
- Note the function app hostname — e.g. `https://<funcapp>.azurewebsites.net` — you'll use it in both backend entries below.
- Managed Identity is not required; leave `api-key` blank in the backend configuration.

## Policy Configuration

### 1. Backend list — same Azure Function, two different delay values

Both backends point at the same deployed function. The only differences are the `delay` query parameter (large for the primary, small for the secondary) and the policy-side `Timeout`. Keeping a single function under test means there are no infrastructure variables — if failover works here, the policy logic is correct.

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

The policy reads an optional `llm_proxy_priority` header from each request and uses the value (1, 2, or 3) to filter which backends are eligible. If the header is absent, it defaults to **priority 3**. Both backends in this POC have `"acceptablePriorities": [1,2,3]`, so they are eligible at every priority level — which means you don't need to send the header at all for the POC to work. A plain `curl` with no priority header will behave exactly the same as one with `-H "llm_proxy_priority: 3"`.

The `priorityCfg` variable maps each priority level to a retry budget:

```xml
<set-variable name="priorityCfg" value="@{
    JObject cfg = new JObject();
    cfg["1"] = new JObject { { "retryCount", 2 }, { "requeue", false } };
    cfg["2"] = new JObject { { "retryCount", 2 }, { "requeue", false } };
    cfg["3"] = new JObject { { "retryCount", 2 }, { "requeue", false } };
    return cfg;
}" />
```

`retryCount: 2` is sufficient — one cycle is used by the timeout on Backend A, and the next picks Backend B.

`requeue: false` keeps the POC focused. Setting it to `true` would cause the policy to return `429 + S7PREQUEUE` once retries are exhausted, which is useful to test separately but adds noise here.

## How the Failover Plays Out

Understanding the internal sequence makes it easier to interpret the response headers and diagnose anything unexpected. This is what the policy's `<backend><retry>` block does on each request:

1. **Cycle 1 — pick Backend A.** `backendIndex = 0` (Backend A is unthrottled and has priority 1). `CallAttemptStartTime` is stamped, `forward-request` is invoked with `timeout=5`.
2. **APIM cancels the forward at 5s.** `callCompleted` stays `false`. `context.Response` is null/incomplete.
3. **Timeout classification.** The `ErrorScenario` expression evaluates `deltaSeconds (≈5) ≥ Timeout × 0.9 (4.5)` → `likelyTimeout = true`. Backend A is updated: `isThrottling = true`, `retryAfter = UtcNow + 10s`. `activityLog` records `"Throttling [0] by 10s, likely timeout ..."`.
4. **ShouldRetry recomputed.** `isPermError = false`, `retryCount ≥ 0`, and `unThrottledBackends > 0` (Backend B is fine) → `ShouldRetry = true`. The retry loop iterates.
5. **Cycle 2 — pick Backend B.** `backendIndex = 1`. Backend B responds `200 OK` within its 30s timeout.
6. **Outbound.** Policy emits `x-Backend-Attempts: 2`, `x-backend-affinity: <hashOfBackendB>`, and a `backendLog` header summarizing both attempts.

### Timeout vs. 429 — same outcome in production

It's worth being explicit about why these two failure modes lead to identical client outcomes. Azure OpenAI can signal "stop sending traffic" in two ways, and this policy treats both as the same failover trigger:

| Backend signal | Where it's classified in the policy | Resulting state |
| :--- | :--- | :--- |
| **Forward times out** (no response within `Timeout`) | `ErrorScenario` sets `likelyTimeout` when elapsed ≥ `Timeout × 0.9` → `wasLimited=false`, `callCompleted=false` branch | `isThrottling=true`, `retryAfter = UtcNow + 10s` (hard-coded cool-down) |
| **`429 Too Many Requests`** with a `Retry-After` header | `isTempError` becomes true, falls into the `<when condition="isTempError">` block that parses `retry-after` from response headers | `isThrottling=true`, `retryAfter = UtcNow + (header value + 2s)` |

In both cases the next `<retry>` cycle finds Backend A throttled, Backend B available, and routes accordingly. The client sees `200 OK` with the same set of diagnostic headers either way. The practical difference is cost: a `429` fails fast and honours the backend's own cool-down window, while a timeout burns the full `Timeout` seconds of wall-clock time on the failed attempt.

This POC drives the timeout path because it's easy to reproduce with the `delay` function. In production traffic against Azure OpenAI you will most often see the `429` path; both flow through the same failover logic. To exercise the `429` path directly with the simulator, see [Variant — drive the 429 path](#variant--drive-the-429-path-with-the-simulator) below.

<details>
<summary>Variant — drive the 429 path with the simulator</summary>

The LLM Simulator's `/api/error/429` endpoint returns a real `429 Too Many Requests` response with a `Retry-After` header, immediately. Swapping Backend A's URL to this endpoint exercises the second failover path with the exact same policy — same two-backend setup, same verification steps, but the failed attempt takes milliseconds instead of seconds.

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

### Why run this variant

The timeout path is easier to set up, but the 429 path is closer to what you'll actually see in production. Azure OpenAI returns `429` with a `Retry-After` header when it's under load — it doesn't wait for your client to time out. Running this variant confirms that the policy correctly reads the header and applies the backend's own cool-down value rather than the hard-coded fallback. It also makes iteration faster: you can test short (`?retryAfter=1`) and long (`?retryAfter=60`) throttle windows by changing a query parameter.

</details>

## Verifying the POC

### Request

The delay function accepts `GET` and `POST` with no required body, so a plain `curl` works. If you're using the 429 variant, the same command applies — just make sure Backend A is pointing at `/api/error/429`.

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

After the first request triggers failover, Backend A is marked throttled for 10 seconds (or the `Retry-After` value for the 429 path). Requests arriving during that window skip Backend A entirely and go straight to Backend B — you'll see `x-Backend-Attempts: 1` instead of `2`. Once the window expires, the policy resets Backend A and it becomes eligible again on the next request.

This sequence is easy to observe manually:

1. Fire request #1 → 2 attempts, `~5s` latency, succeeds on Backend B.
2. Fire request #2 within 10s → 1 attempt, fast, succeeds on Backend B (no timeout). `x-Backend-Attempts: 1`.
3. Wait > 10s, fire request #3 → 2 attempts again (Backend A is re-tried, times out, fails over).

<details>
<summary>Tuning and further exploration</summary>

Once the basic POC is working, a few variations are worth trying:

- **Change the timeout window:** lower Backend A's `Timeout` to `2` and set `?delay=5000` for a faster failover, or raise both values to exaggerate the latency cost and make it more visible.
- **Point Backend A at a non-routable host:** the policy classifies unreachable endpoints the same way as timeouts and will still fail over — useful for confirming behaviour during infrastructure failures.
- **Test the requeue path:** set `requeue: true` and `retryCount: 0`. With no retries left after the timeout, the policy returns `429 + S7PREQUEUE: true + retry-after-ms` — the signal that tells SimpleL7Proxy to re-enqueue the request rather than return an error.
- **Send a burst:** the delay function uses a normal distribution (mean = your `?delay` value, stddev ~200ms). A burst of concurrent requests lets you watch the throttle window open and close on Backend A in real time.

</details>

<details>
<summary>Notes</summary>

- `BufferResponse: true` on Backend A is fine for this POC. The policy doesn't attempt to stream from an endpoint that's timing out.
- The 10-second throttle window for the timeout path is hard-coded in the policy (`backend["retryAfter"] = DateTime.UtcNow.AddSeconds(10)` in the `ErrorScenario` timeout branch). Change it there if you need a different cool-down.
- This is the minimal two-node version of the mechanism described in the [high availability scenario](./high-availability-scenario.md). The same failover logic scales to any number of backends.

</details>

---

## Related Documentation

- [POC-Priority-configuration.md](POC-Priority-configuration.md) — Routing requests across backends by priority tier
- [POC-Chargeback.md](POC-Chargeback.md) — Token-level usage tracking and per-user cost attribution
- [BACKEND_HOSTS.md](BACKEND_HOSTS.md) — Host connection string options including `Timeout` and `retryCount`
- [OBSERVABILITY.md](OBSERVABILITY.md) — Token metrics, telemetry channels, and event logger configuration
