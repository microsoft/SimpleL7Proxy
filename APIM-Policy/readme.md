# Priority-with-retry: Advanced APIM Policy for OpenAI Service Routing

## When to Use This Policy

- You need to maintain high availability across multiple backend services
- Different request types require different priority levels
- You want to optimize cost by intelligently routing traffic
- Your application requires resilience against throttling and service interruptions

## What This Policy Does

The Priority-with-retry policy delivers robust, intelligent request routing and operational control:

- **Smart Priority Routing:** Directs requests to the best backend based on priority, health (throttling), deployment type (PTU/PayGo), and region. Balances load and avoids hotspots by randomizing among suitable backends.
- **Concurrency & Throttling Control:** Applies per-backend concurrency limits (`LimitConcurrency`), detects throttling in real time, and avoids throttled backends until they recover using in-memory cache.
- **Flexible Retry & Queueing:** Supports configurable retry and requeue strategies per priority, including graduated retry counts and smart queuing. If all backends are busy, returns a precise retry-after value.
- **Cost Efficiency:** Maximizes use of pre-paid PTU resources before using PayGo, and restricts PayGo for non-critical workloads.
- **Authentication Options:** Supports both API Key and Azure Managed Identity for backend authentication.
- **Detailed Diagnostics:** Provides logging with timestamps and diagnostic info in response headers for monitoring and troubleshooting.
- **No External Dependencies:** Maintains backend and throttling state in memory.

This policy has been rigorously tested and successfully benchmarked with two OpenAI pay-as-you-go instances deployed in different regions, achieving a sustained performance of over 23 million tokens per minute over an extended period.

![image](./Flow.pdf)

## How to Configure

- **Backends:** Define your available backends, their priorities, and which priority levels they should accept.
- **Priorities:** Set different retry behaviors and counts for different priority levels.
- **Concurrency:** Tune the concurrency limits for different tiers.
- **Cost Controls:** Configure priority-based access to different pricing tiers (PTU vs. PayGo).
- **Retry Logic:** Customize retry-after durations based on workload importance and cost considerations.

## Example Scenarios

The Priority-with-retry policy can be applied to various business scenarios with different optimization goals:

1. [**Financial Services Scenario**](./scenarios/financial-services-scenario.md) - Prioritizing performance for critical trading operations while managing costs for lower-priority workloads.
2. [**Cost Optimization Scenario**](./scenarios/cost-optimization-scenario.md) - Focusing on minimizing Azure OpenAI costs while maintaining acceptable performance for all workloads.
3. [**High Availability Scenario**](./scenarios/high-availability-scenario.md) - Ensuring maximum service availability across multiple regions and deployment types.

Each scenario demonstrates how to configure the policy to meet different business requirements.

## Overview

This document provides an overview of the **Priority-with-retry.xml** Azure API Management (APIM) policy, explaining its purpose and functionality. The policy is designed to route requests to specific backends based on their assigned priority, with built-in mechanisms for retrying and requeuing requests when necessary.

For detailed technical explanations of how the policy works internally, please see our [How It Works](./how-it-works.md) document.

## Customization Instructions

### Adding New Backends

To add new backends, modify the initialization of the listBackends variable in the <inbound> section:

``` XML
<set-variable name="listBackends" value="@{
    JArray backends = new JArray();
    backends.Add(new JObject()
    {
        { "url", "https://new-backend.example.com/" },
        { "priority", 3 },
        { "isThrottling", false },
        { "retryAfter", DateTime.MinValue },
        { "ModelType", "NEW" },
        { "acceptablePriorities", new JArray(1, 2, 3) },
        { "api-key", "new-api-key" },
        { "defaultRetryAfter", 15 },
        { "LimitConcurrency", "low" } // Set concurrency limit: "high", "medium", "low", or "off"
    });
    return backends;
}" />
```

To use an API key, paste its value into the designated section. If you prefer to use **Azure Managed Identity**, leave the value blank.

**Concurrency Control (`LimitConcurrency`):**

- Use `"high"`, `"medium"`, or `"low"` to set the backend's concurrency limit to a preset value.
    - `"high"`: Allows the most concurrent requests (best for robust or PTU-backed endpoints)
    - `"medium"`: Moderate concurrency (default for most PayGo endpoints)
    - `"low"`: Restricts concurrency (useful for fragile or rate-limited endpoints)
    - `"off"`: Disables concurrency limiting for this backend
- If omitted, the policy uses its default concurrency behavior.

Choose the value that matches your backend's capacity and throttling tolerance. For Azure OpenAI, `"medium"` is a safe starting point for PayGo, and `"high"` for PTU.

### Adjusting Priority Configuration

To change the retry count or requeue behavior for a specific priority, update the priorityCfg variable:

``` XML
<set-variable name="priorityCfg" value="@{
    JObject cfg = new JObject();
    cfg["1"] = new JObject {
        { "retryCount", 100 },
        { "requeue", true }
    };
    cfg["2"] = new JObject {
        { "retryCount", 10 },
        { "requeue", false }
    };
    cfg["3"] = new JObject {
        { "retryCount", 5 },
        { "requeue", true }
    };
    return cfg;
}" />
```

### Modifying Retry Logic

To adjust the retry condition or count, modify the <retry> element in the <backend> section:

``` XML
<retry condition="@(context.Variables.GetValueOrDefault<bool>("ShouldRetry", true##" count="50" interval="1">
```

* *condition:* Specifies when retries should occur.
* *count:* Sets the maximum number of retries.
* *interval:* Defines the delta delay (in seconds) between retries.  0 means linear while 1 means, add 1 second after each retry.

*Customizing Logging:*

To add custom log entries, update the *backendLog* variable in the <backend> section:

``` XML
<set-variable name="backendLog" value="@{
    string backendLog = context.Variables.GetValueOrDefault<string>("backendLog", "");
    backendLog += " Custom log entry: " + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    return backendLog;
}" />
```

## Testing and Debugging ##

*Enable Debugging:*

Set the *S7PDEBUG* header to true in your requests to the proxy enable detailed tracing.

*Inspect Logs:*

Review the *backendLog* variable in the response headers to understand backend selection and processing details.

*Simulate Failures:*

Test the retry and requeue logic by simulating backend failures or throttling.

## Notes

- **PTU First:** The policy always tries pre-paid PTU endpoints before PayGo to maximize value.
- **Consistent Models:** Use the same model (e.g., GPT-4) across deployment types for consistent results.
- **Retry-After:** Lower-priority requests may be asked to retry later, letting higher-priority requests go first during busy periods.
- **Cost Controls:** 
  - PTU handles all priorities to use committed capacity.
  - PayGo is restricted for low-priority traffic to control costs.
  - Low-priority workloads can be routed to internal or free-tier resources.
  - Lower priorities have longer retry-after times, ensuring critical operations are prioritized.

## FAQ

**How do I install this policy?**  
Paste the policy XML into your API operation in the Azure portal.

**What do I need to use this policy?**  
An Azure API Management instance and at least one Azure OpenAI or compatible backend.

**Which APIM versions are supported?**  
All current versions.

**How many backends should I configure?**  
At least 2-3 for redundancy; dozens are supported.

**How do I set priorities?**  
Map critical operations to priority 1, standard to 2, background to 3.

**How can I test before production?**  
Use APIM's test feature and set the S7PDEBUG header to true for diagnostics.

**Is it safe to store API keys in the policy?**  
Use Azure Key Vault for production instead of hardcoding keys.

**How do I prevent users from setting their own priority?**  
Add APIM validation policies to enforce or override the priority header.

**What if all backends are throttling?**  
Increase backend capacity, adjust retry strategy, or add client-side throttling.

**How do I monitor the policy?**  
Check backendLog in response headers or export to Application Insights/Log Analytics.

**Common issues?**  
Misconfigured priorities, retry counts, or backend authentication.

**Need help?**  
File an issue in the GitHub repo or contact Azure support.