# How the Priority-with-retry Policy Works

This document provides a detailed technical explanation of the inner workings of the Priority-with-retry APIM policy. For a high-level overview and configuration instructions, see the [main README](./readme.md).

## Technical Overview

When a backend indicates that it is throttling, the policy caches this status and ensures that the backend is not retried until its **retry-after** time has elapsed. Subsequent requests automatically bypass the throttled backend until it becomes available again. Each cache update lasts for 120 seconds, and any updates to the list of backends are incorporated during this period. Additionally, each API maintains its own independent copy of the cache, as different deployments may experience throttling at varying rates.

If no suitable backends are found, the policy responds with a **429 Too Many Requests** status and includes a **retry-after-ms** header. In scenarios where no backends can handle the specified priority level, the policy returns a maximum **retry-after-ms** value of 120,000 milliseconds.

Backends can define the list of priorities they support, and the policy dynamically identifies the appropriate backends to use based on this configuration. Furthermore, each priority level can be customized to specify the number of retries to perform and whether a **retry-after-ms** response should be returned in cases of throttling. Each backend can also be configured to use either an API key or Azure Managed Identity.

## Policy Structure

The policy is organized into four sections:

* *Inbound:* Prepares variables, validates requests, and sets up configurations.
* *Backend:* Contains the main logic for backend selection, retries, and requeueing.
* *Outbound:* Customizes the response before sending it back to the client.
* *On-Error:* Handles exceptions and customizes error responses.

## Key Variables and Their Purpose

* *listBackends:* A cached list of backend configurations, including their URLs, priorities, throttling status, and retry-after-ms durations. Backends can be added or removed by modifying the initialization of the listBackends variable in the <inbound> section.
* *LimitConcurrency:* (per-backend) Specifies the concurrency limit for each backend. Accepts values `"high"`, `"medium"`, `"low"`, or `"off"`. The policy uses this value to control how many concurrent requests can be sent to each backend, helping to avoid overloading or throttling. If omitted, the policy applies its default concurrency behavior.
* *priorityCfg:* Defines retry counts and requeue behavior for each priority level. Customize the priorityCfg variable to define custom retry counts and requeue behavior for each priority.
* *RequestPriority:* Determines the priority of the current request from the headers. A default priority of 3 is assigned if the incoming request is missing the priority header.
* *PriBackendIndxs:* A list of backend indices that match the request's priority.
* *RetryCount:* Tracks the number of retries allowed for the current request.
* *ShouldRequeue:* Indicates whether the request should be requeued.
* *backendLog:* Logs details about backend selection and request processing.

## Detailed Explanation of Each Section

### Inbound Section
This section initializes variables and prepares configurations for backend selection and retry logic.

*Backend Initialization:*

The *listBackends* variable is initialized with backend configurations, including their URLs, priorities, and throttling status.
If the *listBackends* variable is not found in the cache, it is created and stored for future use.

*Priority Configuration:*

The *priorityCfg* variable defines retry counts and requeue behavior for each priority level.
Example:
* *Priority 1:* Retry 50 times, no requeue.
* *Priority 2:* Retry 5 times, requeue if retries fail.
* *Priority 3:* Retry 1 time, requeue if retry fails.

*Request Priority:*

The *RequestPriority* variable determines the priority of the current request based on the *llm_proxy_priority* header. If the header is missing, a default priority of 3 is assigned.

*Backend Filtering:*

The *PriBackendIndxs* variable identifies backends that support the request's priority and are not throttling.

*Logging:*

The *backendLog* variable captures details about the request, including its priority and headers.

### Backend Section
This section contains the main logic for backend selection, retries, and requeueing.

*Retry Logic:*

The <retry> element retries requests to backends that fail due to throttling or other errors (e.g., 429, 408, 500).
Retry count and interval are configurable.

*Backend Selection:*

The *backendIndex* variable identifies the most suitable backend based on priority and availability.
If multiple backends are available at the same priority level, one is selected randomly.

*Throttling Management:*

Backends that return a 429 response are marked as throttling, and their retryAfter value is updated based on the retry-after-ms header or a default value.

*Requeueing:*

If all backends are throttling, the request is requeued by returning a 429 response with a retry-after-ms header.

*Logging:*

The *backendLog* variable is updated with details about backend selection, retries, and throttling status.

### Outbound Section
This section customizes the response before sending it back to the client.

*Response Headers:*

The *backendLog* variable is included in the response headers for debugging and monitoring purposes.

*Response Customization:*

Additional response customization can be implemented as needed.

### On-Error Section
This section handles exceptions and customizes error responses.

*Error Handling:*
Exceptions are logged, and custom error responses can be returned to the client. Exceptions are logged in the backendLog, and custom error messages can be defined in this section.

## Backend Selection Algorithm

The policy follows these steps to select a backend:

1. Identifies all backends that accept the request's priority level
2. Filters out backends that are currently throttling
3. Groups the remaining backends by their priority level
4. Selects the highest priority group that has available backends
5. Randomly chooses one backend from the selected group to distribute load

If no backends are available that meet these criteria, the policy:

1. Identifies the backend that will become available soonest (based on retry-after times)
2. Returns a 429 response with a retry-after-ms header set to the wait time
3. Applies different retry strategies based on the request's priority level

## Caching Mechanism

The policy uses APIM's distributed cache to maintain the state of backends across requests:

1. The backend list is cached for 120 seconds
2. Throttling status and retry-after times are updated in real-time
3. Each API has its own independent cache
4. The cache is automatically refreshed when backends are added or removed

This caching approach provides high performance while ensuring accurate throttling management without external dependencies.

### How LimitConcurrency Works

Each backend in the `listBackends` configuration can specify a `LimitConcurrency` property. This property determines the maximum number of concurrent requests allowed for that backend. The policy uses this value to apply adaptive concurrency control:

- `"high"`: Allows the most concurrent requests (suitable for robust or PTU-backed endpoints).
- `"medium"`: Allows a moderate number of concurrent requests (default for most PayGo endpoints).
- `"low"`: Restricts concurrency (useful for fragile or rate-limited endpoints).
- `"off"`: Disables concurrency limiting for this backend.

When processing requests, the policy checks the current concurrency for each backend and only selects backends that have not reached their concurrency limit. This helps prevent overloading a backend and reduces the likelihood of throttling errors. If all suitable backends have reached their concurrency limits, the policy will either retry, requeue, or return a `429 Too Many Requests` response with an appropriate `retry-after-ms` header.
