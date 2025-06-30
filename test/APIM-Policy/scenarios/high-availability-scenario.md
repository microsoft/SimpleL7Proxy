# High Availability Scenario

## Overview

Consider a global healthcare company with the following critical AI workloads:

1. **Patient Diagnostic Support** - Must be highly available with low latency
2. **Healthcare Provider Recommendations** - Requires high availability but can tolerate some latency
3. **Research Data Processing** - High throughput with flexible timing

## Challenge

The company has regulatory requirements for high availability (99.99%) and data residency concerns requiring deployments in multiple geographic regions. They have the following Azure OpenAI deployments:

- 2 x PTU deployments in different regions (US and Europe)
- 3 x PayGo deployments across US, Europe, and Asia
- 1 x Development/Test deployment

Any regional outage or capacity issue must not impact the critical patient diagnostic services. The solution must also handle regional throttling events gracefully while maintaining global availability.

## Solution with Priority-with-retry

By implementing this policy with a focus on high availability, the company can:

1. **Configure Multi-Region Backend Strategy**:
```XML
<set-variable name="listBackends" value="@{
    JArray backends = new JArray();
    // US PTU instance - primary for US workloads
    backends.Add(new JObject() {
        { "url", "https://ptu-us-openai.openai.azure.com/" },
        { "priority", 1 },
        { "region", "US" },
        { "isThrottling", false },
        { "retryAfter", DateTime.MinValue },
        { "ModelType", "GPT4" },
        { "deploymentType", "PTU" },
        { "acceptablePriorities", new JArray(1, 2) }, // Patient diagnostics and provider recommendations
        { "api-key", "ptu-us-key" },
        { "defaultRetryAfter", 3 }
    });
    // Europe PTU instance - primary for EU workloads, secondary for US
    backends.Add(new JObject() {
        { "url", "https://ptu-eu-openai.openai.azure.com/" },
        { "priority", 1 },
        { "region", "EU" },
        { "isThrottling", false },
        { "retryAfter", DateTime.MinValue },
        { "ModelType", "GPT4" },
        { "deploymentType", "PTU" },
        { "acceptablePriorities", new JArray(1, 2) }, // Patient diagnostics and provider recommendations
        { "api-key", "ptu-eu-key" },
        { "defaultRetryAfter", 3 }
    });
    // US PayGo instance - overflow for US region
    backends.Add(new JObject() {
        { "url", "https://paygo-us-openai.openai.azure.com/" },
        { "priority", 2 },
        { "region", "US" },
        { "isThrottling", false },
        { "retryAfter", DateTime.MinValue },
        { "ModelType", "GPT4" },
        { "deploymentType", "PayGo" },
        { "acceptablePriorities", new JArray(1, 2, 3) }, // All priorities
        { "api-key", "paygo-us-key" },
        { "defaultRetryAfter", 5 }
    });
    // EU PayGo instance - overflow for EU region
    backends.Add(new JObject() {
        { "url", "https://paygo-eu-openai.openai.azure.com/" },
        { "priority", 2 },
        { "region", "EU" },
        { "isThrottling", false },
        { "retryAfter", DateTime.MinValue },
        { "ModelType", "GPT4" },
        { "deploymentType", "PayGo" },
        { "acceptablePriorities", new JArray(1, 2, 3) }, // All priorities
        { "api-key", "paygo-eu-key" },
        { "defaultRetryAfter", 5 }
    });
    // Asia PayGo instance - geographic redundancy
    backends.Add(new JObject() {
        { "url", "https://paygo-asia-openai.openai.azure.com/" },
        { "priority", 3 },
        { "region", "ASIA" },
        { "isThrottling", false },
        { "retryAfter", DateTime.MinValue },
        { "ModelType", "GPT4" },
        { "deploymentType", "PayGo" },
        { "acceptablePriorities", new JArray(1, 2, 3) }, // All priorities
        { "api-key", "paygo-asia-key" },
        { "defaultRetryAfter", 10 }
    });
    // Dev/Test instance - for research only
    backends.Add(new JObject() {
        { "url", "https://dev-openai.openai.azure.com/" },
        { "priority", 4 },
        { "region", "US" },
        { "isThrottling", false },
        { "retryAfter", DateTime.MinValue },
        { "ModelType", "GPT4" },
        { "deploymentType", "Development" },
        { "acceptablePriorities", new JArray(3) }, // Research only
        { "api-key", "dev-key" },
        { "defaultRetryAfter", 15 }
    });
    return backends;
}" />
```

2. **Define Priority Behaviors with High Availability Focus**:
```XML
<set-variable name="priorityCfg" value="@{
    JObject cfg = new JObject();
    // Patient diagnostics - maximum retries with no requeue
    cfg["1"] = new JObject {
        { "retryCount", 100 },
        { "requeue", false } // Must get immediate response
    };
    // Provider recommendations - high retries with requeue
    cfg["2"] = new JObject {
        { "retryCount", 25 },
        { "requeue", true }
    };
    // Research - moderate retries with requeue
    cfg["3"] = new JObject {
        { "retryCount", 10 },
        { "requeue", true }
    };
    return cfg;
}" />
```

## Example Backend Configuration

``` XML
<set-variable name="listBackends" value="@{
    JArray backends = new JArray();
    backends.Add(new JObject()
    {
        { "url", "https://region1-ptu.example.com/" },
        { "priority", 1 },
        { "isThrottling", false },
        { "retryAfter", DateTime.MinValue },
        { "ModelType", "GPT-4" },
        { "acceptablePriorities", new JArray(1, 2, 3) },
        { "api-key", "region1-ptu-key" },
        { "defaultRetryAfter", 5 },
        { "LimitConcurrency", "high" }
    });
    backends.Add(new JObject()
    {
        { "url", "https://region2-paygo.example.com/" },
        { "priority", 2 },
        { "isThrottling", false },
        { "retryAfter", DateTime.MinValue },
        { "ModelType", "GPT-4" },
        { "acceptablePriorities", new JArray(1, 2) },
        { "api-key", "region2-paygo-key" },
        { "defaultRetryAfter", 15 },
        { "LimitConcurrency", "medium" }
    });
    return backends;
}" />
```

> For details on how `LimitConcurrency` works and available options, see [How It Works](../how-it-works.md#how-limitconcurrency-works).

## Results

With this configuration:

1. **Patient Diagnostic Support (Priority 1)**:
   - Attempts are load-balanced between US and EU PTU instances based on availability
   - If both PTU instances are throttled, tries all three PayGo instances across regions
   - Uses very high retry count (100) without requeuing to ensure immediate response
   - Never falls back to development instance
   - Achieves 99.99% availability through multi-region redundancy

2. **Healthcare Provider Recommendations (Priority 2)**:
   - Similar routing to Patient Diagnostics
   - High retry count (25) with requeue option if all backends are throttled
   - Can be delayed during extreme load but will eventually be processed

3. **Research Data Processing (Priority 3)**:
   - Can use any backend including the development instance
   - Moderate retry count (10) with requeuing
   - During high load, automatically yields to higher-priority workloads

This implementation achieves several high availability goals:

- **Geographic Redundancy**: Workloads can be served from multiple global regions
- **Regional Failover**: If one region experiences issues, traffic automatically routes to other regions
- **Priority-Based Resilience**: Critical workloads have the highest retry counts and access to all production backends
- **Graceful Degradation**: Lower-priority workloads automatically yield resources during high demand
- **Compliance Support**: Can route requests to specific regions based on data residency requirements

The policy ensures that even during regional outages or capacity constraints, the most critical healthcare services remain available and responsive.
