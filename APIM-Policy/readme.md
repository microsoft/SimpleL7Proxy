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
- **Concurrency Control:** Enforces per-backend concurrency limits (`LimitConcurrency`) to prevent overloading.
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
        { "priority", 1 },               // 1=High, 2=Medium, 3=Low
        { "ModelType", "PTU" },          // Informational label for logging
        { "acceptablePriorities", new JArray(1, 2, 3) }, // Priorities this backend can handle
        { "LimitConcurrency", "high" },  // high (100), medium (50), low (10), or off
        { "BufferResponse", false },     // Set to false for Streaming; true to buffer full response
        { "Timeout", 120 },              // Backend timeout in seconds
        { "api-key", "your-api-key" }    // Leave blank if using Managed Identity
    });
    // Add more backends...
    return backends;
}" />
```

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
Paste the contents of `Priority-with-retry.xml` into your API policy editor in the Azure Portal.

**How does authentication work?**
The policy supports both API Keys (defined in `listBackends`) and Azure Managed Identity (recommended).

**How do I debug?**
Set the header `S7PDEBUG: true` in your request. Inspect the `backendLog` header in the response for execution traces.
