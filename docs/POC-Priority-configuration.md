# POC: Priority Levels

## Overview

This POC shows how the `acceptablePriorities` field on each backend restricts which requests it will handle. When a request carries a high-priority marker (e.g., `llm_proxy_priority: 1`), the policy only considers backends whose `acceptablePriorities` list includes `1`. Backends that don't include that value are skipped entirely — not tried and failed, just not in the eligible set.

The goal of this POC is to verify that:

1. A priority-1 request goes only to the backend reserved for it, even when other backends are available.
2. A priority-3 request lands on a shared backend.
3. If the reserved backend is absent and the request priority has no eligible backend, the policy returns `503` rather than silently routing to the wrong tier.

The LLM Simulator covers all three cases without any real model endpoints.

---

## What the policy does

Each backend in `listBackends` carries an `acceptablePriorities` array. Before the retry loop picks a backend, the `PriBackendIndxs` variable is built by filtering the backend list to only those whose `acceptablePriorities` contains the request's priority value:

```csharp
if (backend["acceptablePriorities"]?.Values<int>().Contains(requestPriority) == true) {
    list.Add(i);
}
```

The retry loop only iterates over backends in `PriBackendIndxs`. A backend that isn't in that list is invisible for the lifetime of the request, regardless of whether it's healthy.

The request priority is read from the `llm_proxy_priority` header. If the header is absent, the policy defaults to `3`.

---

## Prerequisites

- An APIM instance with `Priority-with-retry-enhancedLog.xml` applied to the target API. See [Applying the policy](#applying-the-policy).
- The LLM Simulator deployed as an Azure Function. See [`test/LLMSimulator/Readme.md`](../test/LLMSimulator/Readme.md) — the fastest path is the portal ZIP deploy. Verify it's running:
  ```bash
  curl https://<funcapp>.azurewebsites.net/api/health
  # → 200 OK
  ```
- Note the function app hostname — you'll use it in the backend list below.

---

## Applying the policy

The policy file is [`APIM-Policy/Priority-with-retry-enhancedLog.xml`](../../APIM-Policy/Priority-with-retry-enhancedLog.xml).

**Azure portal:**
1. Open your APIM instance → **APIs** → select the target API.
2. Select **All operations** in the left panel.
3. Click the `</>` icon in the **Inbound processing** tile.
4. Replace the editor contents with the XML file contents.
5. Click **Save**.

**Azure CLI:**
```bash
az apim api policy create \
  --resource-group <rg> \
  --service-name <apim-name> \
  --api-id <api-id> \
  --value "$(cat APIM-Policy/Priority-with-retry-enhancedLog.xml)" \
  --format xml
```

---

## Backend configuration

This POC uses three backends, all pointing at the same deployed LLM Simulator but with different `acceptablePriorities` lists:

| Name | Endpoint | `priority` | `acceptablePriorities` | Purpose |
|------|----------|-----------|------------------------|---------|
| Backend A | `/api/delay?delay=100` | `1` | `[1]` | Reserved for priority-1 requests only |
| Backend B | `/api/delay?delay=100` | `2` | `[2, 3]` | Shared: handles priority-2 and priority-3 |
| Backend C | `/api/error/500` | `3` | `[3]` | Priority-3 fallback that always fails — used to verify the 503 path |

Replace `listBackends` in the policy's `<inbound>` block:

```xml
<set-variable name="listBackends" value="@{
    JArray backends = new JArray();
    string salt = "0123456789";

    // Backend A: reserved for priority-1 only
    backends.Add(new JObject()
    {
        { "url", "https://<your-funcapp>.azurewebsites.net/api/delay?delay=100" },
        { "priority", 1 },
        { "ModelType", "PTU" },
        { "acceptablePriorities", new JArray(1) },
        { "LimitConcurrency", "off" },
        { "BufferResponse", true },
        { "Timeout", 30 },
        { "api-key", "" }
    });

    // Backend B: shared — handles priority-2 and priority-3
    backends.Add(new JObject()
    {
        { "url", "https://<your-funcapp>.azurewebsites.net/api/delay?delay=100" },
        { "priority", 2 },
        { "ModelType", "PAYGO" },
        { "acceptablePriorities", new JArray(2, 3) },
        { "LimitConcurrency", "off" },
        { "BufferResponse", true },
        { "Timeout", 30 },
        { "api-key", "" }
    });

    // Backend C: always returns 500 — used to verify 503 when no backend is eligible
    backends.Add(new JObject()
    {
        { "url", "https://<your-funcapp>.azurewebsites.net/api/error/500" },
        { "priority", 3 },
        { "ModelType", "PAYGO" },
        { "acceptablePriorities", new JArray(3) },
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

Also set `priorityCfg` to give each priority level one retry:

```xml
<set-variable name="priorityCfg" value="@{
    JObject cfg = new JObject();
    cfg["1"] = new JObject { { "retryCount", 1 }, { "requeue", false } };
    cfg["2"] = new JObject { { "retryCount", 1 }, { "requeue", false } };
    cfg["3"] = new JObject { { "retryCount", 1 }, { "requeue", false } };
    return cfg;
}" />
```

---

## Test cases

Replace `<your-apim>`, `<your-api>`, and `<your-key>` in each command.

### Test 1 — Priority-1 request routes to Backend A only

```bash
curl -i \
  -H "llm_proxy_priority: 1" \
  -H "Ocp-Apim-Subscription-Key: <your-key>" \
  "https://<your-apim>.azure-api.net/<your-api>/api/delay"
```

**Expected:**

| Header | Value | Meaning |
|--------|-------|---------|
| HTTP status | `200 OK` | Backend A responded. |
| `x-Backend-Attempts` | `1` | Only one backend was tried. |
| `x-backend-affinity` | hash of Backend A's URL | Confirms Backend A was used. |
| `x-PolicyCycleCounter` | `1` | One cycle; no retry needed. |
| `backendLog` | `CALL SUCCESSFUL` for Backend A URL | No fallback occurred. |

Backend B and Backend C were never in the candidate set — `PriBackendIndxs` contained only index 0 for `requestPriority = 1`.

---

### Test 2 — Priority-2 request routes to Backend B, skips Backend A

```bash
curl -i \
  -H "llm_proxy_priority: 2" \
  -H "Ocp-Apim-Subscription-Key: <your-key>" \
  "https://<your-apim>.azure-api.net/<your-api>/api/delay"
```

**Expected:**

| Header | Value | Meaning |
|--------|-------|---------|
| HTTP status | `200 OK` | Backend B responded. |
| `x-Backend-Attempts` | `1` | One backend tried. |
| `x-backend-affinity` | hash of Backend B's URL | Confirms Backend B was used. |
| `backendLog` | `CALL SUCCESSFUL` for Backend B URL | Correct. |

Backend A (`acceptablePriorities: [1]`) was not in the candidate set. Backend C was also excluded. Only Backend B matched `requestPriority = 2`.

---

### Test 3 — Default priority (no header) behaves the same as priority-3

```bash
curl -i \
  -H "Ocp-Apim-Subscription-Key: <your-key>" \
  "https://<your-apim>.azure-api.net/<your-api>/api/delay"
```

The policy defaults to `requestPriority = 3` when the header is absent. Backend B (`acceptablePriorities: [2, 3]`) and Backend C (`acceptablePriorities: [3]`) are both eligible.

Backend B (`priority: 2`) sorts before Backend C (`priority: 3`), so Backend B is tried first and responds successfully.

**Expected:** `200 OK`, `x-Backend-Attempts: 1`, affinity pointing at Backend B.

---

### Test 4 — No eligible backend returns 503

This test verifies what happens when a request carries a priority that no backend accepts. Remove Backend B's `2` from its `acceptablePriorities` list (leave it as `[3]` only), then send a priority-2 request:

```xml
{ "acceptablePriorities", new JArray(3) },  // temporarily changed for this test
```

```bash
curl -i \
  -H "llm_proxy_priority: 2" \
  -H "Ocp-Apim-Subscription-Key: <your-key>" \
  "https://<your-apim>.azure-api.net/<your-api>/api/delay"
```

**Expected:** `503 Service Unavailable`. `PriBackendIndxs` is empty for `requestPriority = 2`, so the retry loop has nothing to try.

Restore Backend B's `acceptablePriorities` to `[2, 3]` after verifying this.

---

### Test 5 — Backend A being throttled does not affect a priority-2 request

This confirms that priority isolation works even when a backend in a different tier is actively throttled. First, trigger a throttle on Backend A by changing it to point at `/api/error/429`:

```xml
{ "url", "https://<your-funcapp>.azurewebsites.net/api/error/429?retryAfter=30" },
```

Send a priority-1 request (this will fail over and mark Backend A throttled):
```bash
curl -i -H "llm_proxy_priority: 1" -H "Ocp-Apim-Subscription-Key: <your-key>" \
  "https://<your-apim>.azure-api.net/<your-api>/api/delay"
# Backend A returns 429; no other backend accepts priority-1 → 503
```

Immediately send a priority-2 request:
```bash
curl -i -H "llm_proxy_priority: 2" -H "Ocp-Apim-Subscription-Key: <your-key>" \
  "https://<your-apim>.azure-api.net/<your-api>/api/delay"
```

**Expected:** `200 OK` via Backend B. Backend A's throttle state is irrelevant because it was never in the candidate set for `requestPriority = 2`.

Restore Backend A's URL to `/api/delay?delay=100` after verifying.

---

## How to read the `backendLog` header

Each backend attempt appends a line to `backendLog`. For a successful single-attempt request it looks like:

```
Using PAYGO backend: https://<funcapp>.azurewebsites.net/api/delay?delay=100 ... CALL SUCCESSFUL
```

For a request where the first attempt was skipped (because no eligible backend was found at that index), only the successful attempt appears. For a retry where one backend was throttled and another succeeded, you'll see two lines:

```
Throttling [0] by 12s, isTempError=true, retry-after=10
Using PAYGO backend: https://<funcapp>.azurewebsites.net/api/delay?delay=100 ... CALL SUCCESSFUL
```

The `[0]` is the zero-based index of the throttled backend in `listBackends`.

---

<details>
<summary>Tuning</summary>

Once the basic tests pass, a few variations are worth exploring:

- **Restrict Backend B to priority-2 only** (`acceptablePriorities: [2]`) and send a priority-3 request. With no backend accepting `3`, you'll see a `503` — useful if you want to enforce hard tier boundaries with no shared fallback.
- **Add a PTU backend at priority 1** and a PAYGO backend at priorities 1–3. Priority-1 requests will prefer the PTU backend; only when it's throttled will the policy fall back to PAYGO — the standard cost-optimized pattern for Azure OpenAI deployments.
- **Set `requeue: true` for priority-3** and `retryCount: 0`. When the shared backend is throttled and retries are exhausted, the policy returns `429 + S7PREQUEUE: true`, which tells SimpleL7Proxy to re-enqueue the request rather than return an error to the caller.
- **Use `LimitConcurrency`** to cap how many simultaneous requests each backend handles. Combine with different concurrency caps per tier to simulate a PTU deployment with a fixed token budget.

</details>

---

## Related Documentation

- [POC-Failover-configuration.md](POC-Failover-configuration.md) — Automatic failover and retry behaviour when a backend is slow or unavailable
- [POC-Chargeback.md](POC-Chargeback.md) — Token-level usage tracking and per-user cost attribution
- [BACKEND_HOSTS.md](BACKEND_HOSTS.md) — Host connection string options including `acceptablePriorities` and `processor=`
- [OBSERVABILITY.md](OBSERVABILITY.md) — Token metrics, telemetry channels, and event logger configuration
