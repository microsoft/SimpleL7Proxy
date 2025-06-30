# Priority-with-retry: Advanced APIM Policy for OpenAI Service Routing

## Why Use This Policy?

This policy solves a critical challenge for organizations using multiple OpenAI service instances by providing intelligent request routing with priorities. It's especially valuable when:

- You need to maintain high availability across multiple backend services
- Different request types require different priority levels
- You want to optimize cost by intelligently routing traffic
- Your application requires resilience against throttling and service interruptions

## Key Capabilities & Functionality

The Priority-with-retry policy provides advanced, flexible request routing and robust operational features:

- **Priority-Based Routing:** Routes requests to specific backends based on priority headers, ensuring critical requests use preferred backends.
- **Smart Backend Selection:** Intelligently selects the most appropriate backend based on priority, throttling status, deployment type (PTU vs. PayGo), and region.
- **Adaptive Concurrency Control:** Applies different concurrency limits to each backend using the `LimitConcurrency` setting, preventing overload and reducing throttling.
- **Dynamic Throttling Detection:** Detects when backends are throttling and avoids them until they're available again, using in-memory cache for state.
- **Intelligent Request Queueing:** When all suitable backends are unavailable, returns a precise retry-after value based on when the next backend will become available, creating an intelligent queuing system.
- **Advanced Retry Strategies:** Configurable retry counts and strategies per request priority level, including graduated retry and requeue logic.
- **Load Balancing:** Randomly distributes requests among equally suitable backends to prevent hotspots.
- **Cost-Optimized Resource Allocation:** Routes traffic based on both priority and deployment type, maximizing use of pre-paid PTU resources before falling back to PayGo.
- **Authentication Support:** Supports both API Key and Azure Managed Identity authentication for backends.
- **Detailed Logging & Diagnostics:** Provides detailed logging (including timestamps) and adds diagnostic information to response headers for monitoring and troubleshooting.
- **Cache-Based State Management:** Maintains throttling state across requests without external dependencies, improving performance and reliability.

This policy has been rigorously tested and successfully benchmarked with two OpenAI pay-as-you-go instances deployed in different regions, achieving a sustained performance of over 10 million tokens per minute over an extended period.

![image](./Flow.pdf)

## How to Tune the Policy

- **Backend Configuration:** Define your available backends, their priorities, and which priority levels they should accept.
- **Priority Configuration:** Set different retry behaviors and counts for different priority levels.
- **Concurrency Limits:** Tune the concurrency limits for different tiers.
- **Cost Management:** Configure priority-based access to different pricing tiers (PTU vs. PayGo).
- **Retry Strategies:** Customize retry-after durations based on workload importance and cost considerations.

## Real-World Scenarios

The Priority-with-retry policy can be applied to various business scenarios with different optimization goals:

1. [**Financial Services Scenario**](./scenarios/financial-services-scenario.md) - Prioritizing performance for critical trading operations while managing costs for lower-priority workloads.
2. [**Cost Optimization Scenario**](./scenarios/cost-optimization-scenario.md) - Focusing on minimizing Azure OpenAI costs while maintaining acceptable performance for all workloads.
3. [**High Availability Scenario**](./scenarios/high-availability-scenario.md) - Ensuring maximum service availability across multiple regions and deployment types.

Each scenario demonstrates how to configure the policy to meet different business requirements.

## Overview

This document provides an overview of the **Priority-with-retry.xml** Azure API Management (APIM) policy, explaining its purpose and functionality. The policy is designed to route requests to specific backends based on their assigned priority, with built-in mechanisms for retrying and requeuing requests when necessary.

For detailed technical explanations of how the policy works internally, please see our [How It Works](./how-it-works.md) document.

### Key Functionality

- Routes requests to backends based on priority and availability
- Detects and avoids throttled backends for the appropriate duration
- Implements configurable retry and requeue strategies based on priority
- Supports both API Key and Azure Managed Identity authentication
- Provides detailed logging for monitoring and debugging

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

Set the *S7PDEBUG* header to true in your requests to enable detailed tracing.

*Inspect Logs:*

Review the *backendLog* variable in the response headers to understand backend selection and processing details.

*Simulate Failures:*

Test the retry and requeue logic by simulating backend failures or throttling.

## Conclusion ##

The Priority-with-retry.xml policy is a practical tool for managing priority-based request routing and retries in Azure API Management. By customizing the variables and logic described above, you can adapt the policy to meet your specific needs. For further assistance, refer to the Azure API Management documentation or consult your team.

**Important Notes on Configuration:**

- **PTU Prioritization**: Since Provisioned Throughput Units (PTU) are pre-paid capacity, the policy is configured to always try PTU endpoints first before falling back to Pay-as-you-go (PayGo) endpoints. This ensures maximum utilization of already-paid resources.

- **Model Consistency**: In practice, you typically maintain the same model (GPT-4 in this example) across different deployment types rather than switching between model families (like GPT-4 and GPT-3.5 Turbo). This ensures consistent response quality while varying the deployment type (PTU vs PayGo) based on priority.

- **Retry-After Strategy**: The retry-after mechanism functions as a deliberate offloading technique. When a request is sent back to the proxy with a retry-after value, it allows other higher-priority requests to be processed first. This creates an intelligent queuing system where lower-priority items yield to higher-priority ones during high traffic periods.

- **Cost Management Considerations:**

  - **Maximize PTU Usage**: Since PTU capacity is already paid for, it's configured to accept all priority levels to maximize utilization.
  
  - **Restrict PayGo Access**: For cost control, PayGo instances are restricted from handling low-priority traffic. This prevents incurring additional costs for non-critical workloads.
  
  - **Dedicated Low-Priority Backend**: Low-priority workloads are directed to internal capacity or free-tier resources, ensuring they don't compete with critical workloads and don't incur additional costs.
  
  - **Graduated Retry Times**: Lower priority workloads have longer retry-after times (30 seconds vs 5 seconds for high priority), further ensuring that during high demand, the system naturally prioritizes critical operations.

## Frequently Asked Questions

### Implementation Questions
1. **How do I install this policy in my Azure API Management instance?**
   The policy XML can be added to any API operation in your APIM instance. Navigate to your API in the Azure portal, select an operation, and paste the policy XML in the policy editor.

2. **What are the prerequisites for using this policy?**
   You need an Azure API Management instance (any tier) and at least one Azure OpenAI service instance or other backend service.

3. **Which versions of Azure API Management support this policy?**
   This policy is compatible with all current versions of Azure API Management.

### Configuration Questions
1. **How many backends should I configure for optimal performance?**
   We recommend at least 2-3 backends for redundancy, but the policy can handle dozens of backends if needed.

2. **How should I determine priority levels for my workloads?**
   Map your business-critical operations to priority 1, standard operations to priority 2, and background/non-critical operations to priority 3.

3. **Is there a way to test the configuration before deploying to production?**
   Yes, you can use the test capabilities in APIM and set the S7PDEBUG header to true to see detailed diagnostic information.

### Security Questions
1. **Is it safe to store API keys in the policy?**
   For production environments, we recommend using Azure Key Vault references instead of hardcoding API keys in the policy.

2. **How can I secure access to the priority header to prevent users from setting their own priority?**
   Use APIM validation policies to validate or override the priority header based on authentication, subscription, or other criteria.

### Troubleshooting Questions
1. **What should I do if all backends are consistently throttling?**
   Consider increasing capacity of your backends, reviewing your retry strategy, or implementing client-side throttling.

2. **How can I monitor the effectiveness of this policy?**
   Monitor the backendLog values in response headers and consider exporting this data to Application Insights or Log Analytics.

3. **What are common issues users encounter?**
   Common issues include misconfigured priorities, inappropriate retry counts, and backend authentication problems.

### For More Information
For detailed questions, implementation assistance, or to report issues, please file an issue in the GitHub repository or contact your Azure support representative.