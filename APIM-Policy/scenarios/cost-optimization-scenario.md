# Cost Optimization Scenario

## Overview

Consider a SaaS company offering AI-enhanced document processing services with the following workload types:

1. **Enterprise Tier Customers** - Paying premium subscription fees
2. **Professional Tier Customers** - Mid-range subscription fees
3. **Free Tier Users** - No subscription fees, limited usage

## Challenge

The company needs to maximize profit margins by carefully controlling Azure OpenAI costs while still providing acceptable service levels to different customer tiers. They have the following Azure OpenAI deployments:

- 1 x PTU deployment (pre-paid, fixed cost)
- 2 x PayGo deployments in different regions (pay per token, variable cost)
- 1 x Limited deployment for testing (internal use only)

During promotional periods, free tier usage can spike unexpectedly, threatening to consume expensive PayGo capacity and impact paying customers.

## Solution with Priority-with-retry

By implementing this policy, the company can:

1. **Define Priority Behaviors**:
```XML
<set-variable name="priorityCfg" value="@{
    JObject cfg = new JObject();
    // Enterprise tier - moderate retries
    cfg["1"] = new JObject {
        { "retryCount", 10 },
        { "requeue", true }
    };
    // Pro tier - limited retries
    cfg["2"] = new JObject {
        { "retryCount", 5 },
        { "requeue", true }
    };
    // Free tier - minimal retries with long delays
    cfg["3"] = new JObject {
        { "retryCount", 2 },
        { "requeue", true }
    };
    return cfg;
}" />
```

## Example Backend Configuration

``` XML
<set-variable name="listBackends" value="@{
    JArray backends = new JArray();
    // PTU backend: accepts all priorities
    backends.Add(new JObject()
    {
        { "url", "https://ptu-backend.example.com/" },
        { "priority", 1 },
        { "isThrottling", false },
        { "retryAfter", DateTime.MinValue },
        { "ModelType", "GPT-4" },
        { "acceptablePriorities", new JArray(1, 2, 3) },
        { "api-key", "ptu-api-key" },
        { "defaultRetryAfter", 5 },
        { "LimitConcurrency", "high" }
    });
    // PayGo backend 1: only for enterprise and pro (no free tier)
    backends.Add(new JObject()
    {
        { "url", "https://paygo1-backend.example.com/" },
        { "priority", 2 },
        { "isThrottling", false },
        { "retryAfter", DateTime.MinValue },
        { "ModelType", "GPT-4" },
        { "acceptablePriorities", new JArray(1, 2) },
        { "api-key", "paygo1-api-key" },
        { "defaultRetryAfter", 15 },
        { "LimitConcurrency", "medium" }
    });
    // PayGo backend 2: only for enterprise (no pro or free tier)
    backends.Add(new JObject()
    {
        { "url", "https://paygo2-backend.example.com/" },
        { "priority", 2 },
        { "isThrottling", false },
        { "retryAfter", DateTime.MinValue },
        { "ModelType", "GPT-4" },
        { "acceptablePriorities", new JArray(1) },
        { "api-key", "paygo2-api-key" },
        { "defaultRetryAfter", 15 },
        { "LimitConcurrency", "medium" }
    });
    // Limited backend for free tier only (internal/testing, cheaper model)
    backends.Add(new JObject()
    {
        { "url", "https://limited-backend.example.com/" },
        { "priority", 3 },
        { "isThrottling", false },
        { "retryAfter", DateTime.MinValue },
        { "ModelType", "GPT-3.5-Turbo" },
        { "acceptablePriorities", new JArray(3) },
        { "api-key", "limited-backend-key" },
        { "defaultRetryAfter", 60 },
        { "LimitConcurrency", "low" }
    });
    return backends;
}" />
```

> For details on how `LimitConcurrency` works and available options, see [How It Works](../how-it-works.md#how-limitconcurrency-works).

## Results

With this configuration:

1. **Enterprise Tier (Priority 1)**:
   - First attempts use the PTU instance (maximizing pre-paid capacity)
   - If throttled, can use either PayGo deployment
   - Moderate retry count (10) with requeuing for reliability
   - Gets priority access to all backends

2. **Professional Tier (Priority 2)**:
   - First attempts use the PTU instance
   - If throttled, can only use the first PayGo deployment
   - Limited retry count (5) with requeuing
   - Cannot use the second PayGo deployment (cost control)

3. **Free Tier (Priority 3)**:
   - First attempts use the PTU instance (only when not being used by paying customers)
   - If PTU is unavailable, only uses the limited deployment with GPT-3.5 Turbo
   - Minimal retry count (2) with very long retry-after times (60 seconds)
   - Never consumes expensive PayGo capacity

This implementation achieves several cost optimization goals:

- **Maximizes PTU Usage**: Ensures pre-paid capacity is fully utilized
- **Prioritizes Revenue-Generating Traffic**: Paying customers get preferential access to all resources
- **Creates Service Tiers**: Different customer segments get appropriate service levels
- **Prevents Cost Overruns**: Free tier traffic can never consume expensive PayGo resources
- **Implements Graceful Degradation**: During high load, free tier requests automatically back off
- **Model Tiering**: Uses less expensive models for free tier users

The policy maintains acceptable performance for all customer tiers while strictly controlling costs, directly supporting the company's profit margin goals.
