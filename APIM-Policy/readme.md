# Priority-with-retry: Advanced APIM Policy for Azure OpenAI

The **Priority-with-retry** policy is the **essential routing engine** when using **SimpleL7Proxy** with Azure API Management. It ensures that priority signals, affinity tokens, and throttling backpressure are correctly communicated between the proxy and backend models.

> **Implementation Note:** SimpleL7Proxy should always be paired with this policy (or one derived from it). Using generic routing policies will result in a loss of priority queuing capabilities and observability.

Beyond proxy integration, this policy provides enterprise-grade routing, throttling, and resiliency for any Azure OpenAI workload. Furthermore, since most other LLM providers mimic OpenAI's API structure and headers (for streaming, rate limits, etc.), this policy will likely work with other model backends with minimal adaptation.

## When to Use This Policy

Use this policy when you need:
1. **Tiered Service Levels:** Guarantee capacity for critical apps while throttling background tasks.
2. **Cost Optimization:** Maximize PTU utilization and restrict PayGo usage to specific priorities.
3. **High Availability:** Automatically failover between regions and backend types.
4. **Throttling Management:** Provide specific signals to clients (like SimpleL7Proxy) to queue requests during high load instead of failing immediately.

## Key Capabilities

- **Smart Priority Routing:** Routes requests based on priority (High/Medium/Low), backend health, and deployment type (PTU vs. PayGo).
- **Concurrency Control:** Enforces per-backend concurrency limits (`limitConcurrency`) to prevent overloading.
- **Resiliency:** Intelligent circuit breaking and retry logic that avoids throttled backends.
- **Cost Efficiency:** Prioritizes pre-paid PTU capacity before spilling over to PayGo endpoints.
- **Backend Affinity:** Supports sticky routing via affinity headers to maximize cache hits on OpenAI backends.
- **Streaming Support:** Fully compatible with streaming responses.
- **Observability:** Detailed execution logs via the `backendLog` response header.

## Control Flow

![Request Flow](./Flow.png)


## Configuration Guide

### 1. Define Backends

Locate the `listBackends` variable initialization in the `<inbound>` region. Add your Azure OpenAI endpoints:

```xml
<set-variable name="listBackends" value="@{
    JArray backends = new JArray();
    backends.Add(new JObject()
    {
        { "url", "https://your-ptu-endpoint.openai.azure.com/" },
        { "path", "" },
        { "priorityGroup", 1 },          // Lower number wins
        { "label", "PTU" },             // Informational label for logging
        { "acceptablePriorities", new JArray(1, 2, 3) }, // Priorities this backend can handle
        { "limitConcurrency", "high" }, // Optional: high (100), medium (50), low (10), or off. Defaults to off.
        { "bufferResponse", false },     // Optional: set false for streaming. Defaults to true.
        { "timeout", 120 },              // Optional: backend timeout in seconds. Defaults to 10.
        { "auth", "MI" }               // "MI", a literal API key, or "" for no auth header
    });
    // Add more backends...
    return backends;
}" />
```

The current backend schema is `priorityGroup`, `label`, `acceptablePriorities`, `limitConcurrency`, `bufferResponse`, `timeout`, and `auth`.

- Uppercase-first variants such as `LimitConcurrency`, `BufferResponse`, and `Timeout` are normalized to lowercase when the policy loads.
- If `limitConcurrency`, `bufferResponse`, or `timeout` are omitted, the policy defaults them to `off`, `true`, and `10` seconds.
- Older samples that use `priority`, `ModelType`, or `api-key` should be updated to `priorityGroup`, `label`, and `auth`.

### 2. Configure Priority Rules

Adjust retry behavior per priority level using the `priorityCfg` variable:

```xml
<set-variable name="priorityCfg" value="@{
    JObject cfg = new JObject();
    // Priority 1: Aggressive retries
    cfg["1"] = new JObject { { "retryCount", 5 }, { "requeue", true } }; 
    // Priority 3: Fail fast
    cfg["3"] = new JObject { { "retryCount", 1 }, { "requeue", false } };
    return cfg;
}" />
```

### 3. Configure Headers

You can customize the header names used for control logic by modifying the variables at the top of the `<inbound>` block:

```xml
<set-variable name="priorityHeaderName" value="llm_proxy_priority" /> <!-- Header for priority (1, 2, 3) -->
<set-variable name="PolicyCycleCounterHeaderName" value="x-PolicyCycleCounter" /> <!-- Tracks retry attempts count -->
<set-variable name="AffinityHeaderName" value="x-backend-affinity" /> <!-- Sticky session support -->
```

## Migrating from v2.0.1 to v2.1.0

Most migrations are configuration-only. The main work is updating `listBackends` entries to the new schema and checking any retry settings that depended on the older retry-budget behavior.

### What changed

1. **Backend field names changed.**
    - `priority` -> `priorityGroup`
    - `ModelType` -> `label`
    - `api-key` -> `auth`
    - `LimitConcurrency`, `BufferResponse`, and `Timeout` are now read as `limitConcurrency`, `bufferResponse`, and `timeout`

2. **Authentication is now explicit.**
    - In v2.0.1, an empty `api-key` meant "use Managed Identity".
    - In v2.1.0, `auth: "MI"` means Managed Identity, `auth: "<key>"` means send `api-key: <key>`, and `auth: ""` means send no auth header.

3. **Backend URLs are now composed from `url` plus optional `path`.**
    - In v2.0.1, the policy appended `/openai` when building `backendUrl`.
    - In v2.1.0, the policy combines `url` and `path` during normalization and uses the result as-is.
    - If you relied on the automatic `/openai` append, add `"path": "/openai"` or include `/openai` directly in `url`.

4. **Missing backend settings now get defaults.**
    - If `limitConcurrency` is omitted, the policy sets it to `off`.
    - If `bufferResponse` is omitted, the policy sets it to `true`.
    - If `timeout` is omitted, the policy sets it to `10` seconds.

5. **Retry budget handling bug fix.**
    - v2.0.1 allowed the request path to keep going while `RetryCount >= 0`.
    - v2.1.0 only retries while `RetryCount > 0`.
    - If you previously used `retryCount: 1` the policy retried twice.  For the same behaviour increase it to `2`.

6. **PTU skip-on-context-window now keys off `label`.**
    - In v2.0.1, the context-window-exceeded path skipped PTU backends when `ModelType == "PTU"`.
    - In v2.1.0, it skips them when `label == "PTU"`.

### Before and after example

v2.0.1 backend entry:

```xml
backends.Add(new JObject()
{
     { "url", "https://your-resource.openai.azure.com/" },
     { "priority", 1 },
     { "ModelType", "PTU" },
     { "acceptablePriorities", new JArray(1,2,3) },
     { "LimitConcurrency", "off" },
     { "BufferResponse", true },
     { "Timeout", 30 },
     { "api-key", "" }
});
```

v2.1.0 backend entry:

```xml
backends.Add(new JObject()
{
     { "url", "https://your-resource.openai.azure.com/" },
     { "path", "/openai" },
     { "priorityGroup", 1 },
     { "label", "PTU" },
     { "acceptablePriorities", new JArray(1,2,3) },
     { "limitConcurrency", "off" },
     { "bufferResponse", true },
     { "timeout", 30 },
     { "auth", "MI" }
});
```

### Migration checklist

- Rename `priority`, `ModelType`, and `api-key` to `priorityGroup`, `label`, and `auth`.
- Add `path` if your old config relied on the built-in `/openai` append.
- Change Managed Identity backends from empty `api-key` to `auth: "MI"`.
- Raise `retryCount` if you depended on the older `>= 0` retry behavior.
- Keep `label: "PTU"` on PTU backends if you want context-window-exceeded requests to skip them.
- Remove `limitConcurrency`, `bufferResponse`, or `timeout` only if the new defaults are acceptable.

## Standalone Usage & Client Headers

While this policy is optimized for the **SimpleL7Proxy**, it serves as a powerful standalone routing engine. To unlock features like sticky sessions and priority handling without the proxy, your client application should manage the following headers:

### Input Headers (Client -> APIM)

| Header Name | Default | Purpose |
| :--- | :--- | :--- |
| **Priority** | `llm_proxy_priority` | Set to `1` (High), `2` (Medium), or `3` (Low) to determine routing tier. Defaults to `3` if missing. |
| **Affinity** | `x-backend-affinity` | Send the hash received from a previous response to route to the same backend node (Session Stickiness/Cache Optimization). |
| **Cycle Counter** | `x-PolicyCycleCounter` | (Optional) If implementing a client-side retry loop, pass the last received value to maintain a cumulative attempt count for diagnostics. |

### Output Headers (APIM -> Client)

| Header Name | Default | Purpose |
| :--- | :--- | :--- |
| **Affinity** | `x-backend-affinity` | Returns the hash of the backend used. The client should store this for subsequent requests in the same session. |
| **Requeue Signal** | `S7PREQUEUE` | If `true` on a `429` response, it indicates a soft throttle (capacity full). SimpleL7Proxy queues these; standalone clients should sleep for `retry-after-ms`. |
| **Retry Delay** | `retry-after-ms` | Detailed backoff time (in ms) recommended before the next attempt. |

## Example Scenarios

The Priority-with-retry policy can be applied to various business scenarios with different optimization goals:

1. [**Financial Services Scenario**](./scenarios/financial-services-scenario.md) - Prioritizing performance for critical trading operations while managing costs for lower-priority workloads.
2. [**Cost Optimization Scenario**](./scenarios/cost-optimization-scenario.md) - Focusing on minimizing Azure OpenAI costs while maintaining acceptable performance for all workloads.
3. [**High Availability Scenario**](./scenarios/high-availability-scenario.md) - Ensuring maximum service availability across multiple regions and deployment types.

Each scenario demonstrates how to configure the policy to meet different business requirements.


## FAQ

**How do I install this?**
Paste the contents of `Priority-with-retry-enhancedLog.xml` into your API policy editor in Azure API Management.

**How does authentication work?**
Set `auth` to `"MI"` to send a managed identity bearer token, set it to a non-empty string to send that value as the `api-key` header, or leave it empty to send no auth header.

**How do I debug?**
Set the header `S7PDEBUG: true` in your request. Inspect the `backendLog` header in the response for execution traces.
