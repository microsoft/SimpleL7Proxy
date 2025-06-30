# Financial Services Scenario

## Overview

Consider a financial services company that uses OpenAI services for several critical functions:

1. **High-Priority Trading Insights** - Must be processed as quickly as possible, even during high load
2. **Medium-Priority Customer Service AI** - Should respond quickly but can tolerate some delay
3. **Low-Priority Overnight Batch Processing** - Can be scheduled during off-peak hours

## Challenge

The company has three OpenAI service instances:
- Instance A: Premium tier in East US (expensive but high capacity)
- Instance B: Standard tier in West US (moderate cost and capacity)
- Instance C: Basic tier in Central US (cost-effective but limited capacity)

Additionally, the company has invested in a Provisioned Throughput Unit (PTU) deployment for their GPT-4 model to guarantee capacity for their most critical workloads.

During market volatility, all systems experience high load simultaneously, causing throttling and service degradation across all priority levels. The company faces two competing challenges:

1. **Performance Needs**: Ensuring trading insights remain responsive even during peak demand
2. **Cost Management**: Preventing low-priority workloads from consuming expensive PayGo resources

## Solution with Priority-with-retry

By implementing this policy, the company can:

1. **Define Priority Behaviors**:
```XML
<set-variable name="priorityCfg" value="@{
    JObject cfg = new JObject();
    // Trading insights - high retry count, no requeue
    cfg["1"] = new JObject {
        { "retryCount", 50 },
        { "requeue", false }
    };
    // Customer service - moderate retries with requeue
    cfg["2"] = new JObject {
        { "retryCount", 10 },
        { "requeue", true }
    };
    // Batch processing - minimal retries with requeue
    cfg["3"] = new JObject {
        { "retryCount", 3 },
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
        { "url", "https://critical-ptu.example.com/" },
        { "priority", 1 },
        { "isThrottling", false },
        { "retryAfter", DateTime.MinValue },
        { "ModelType", "GPT-4" },
        { "acceptablePriorities", new JArray(1) },
        { "api-key", "critical-ptu-key" },
        { "defaultRetryAfter", 5 },
        { "LimitConcurrency", "high" }
    });
    backends.Add(new JObject()
    {
        { "url", "https://standard-paygo.example.com/" },
        { "priority", 2 },
        { "isThrottling", false },
        { "retryAfter", DateTime.MinValue },
        { "ModelType", "GPT-4" },
        { "acceptablePriorities", new JArray(2, 3) },
        { "api-key", "standard-paygo-key" },
        { "defaultRetryAfter", 20 },
        { "LimitConcurrency", "medium" }
    });
    backends.Add(new JObject()
    {
        { "url", "https://internal-openai-central.openai.azure.com/" },
        { "priority", 3 },
        { "isThrottling", false },
        { "retryAfter", DateTime.MinValue },
        { "ModelType", "GPT4" },
        { "deploymentType", "Internal" }, // Internal capacity, not PayGo
        { "acceptablePriorities", new JArray(3) }, // Only for low priority traffic
        { "api-key", "internal-instance-key" },
        { "defaultRetryAfter", 30 } // Longer retry for low priority
    });
    return backends;
}" />
```

> For details on how `LimitConcurrency` works and available options, see [How It Works](../how-it-works.md#how-limitconcurrency-works).

## Results

With this configuration:

1. **High-Priority Trading Requests**:
   - First attempt goes to the PTU instance to leverage pre-paid capacity
   - If PTU is throttled, immediately tries the Premium PayGo instance
   - If still throttled, falls back to Standard PayGo
   - Uses high retry count (50) without requeuing, ensuring immediate response (success or failure)

2. **Medium-Priority Customer Service**:
   - First attempt goes to the PTU instance (maximizing pre-paid utilization)
   - If throttled, tries the Standard PayGo instance
   - Uses moderate retry count (10) with requeuing
   - If all instances are throttled, requeues with appropriate retry-after value

3. **Low-Priority Batch Processing**:
   - First tries the PTU instance (only if capacity is available)
   - If throttled, uses only the Internal/Free tier
   - Never uses Premium or Standard PayGo instances (cost control)
   - Limited retries (3) with requeue behavior
   - Automatically backs off during high-load periods with longer retry-after times

This implementation balances maximum performance for critical operations while optimizing costs by restricting lower-priority workloads from consuming expensive resources.
