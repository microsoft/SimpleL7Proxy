## README: Priority-with-retry.xml APIM Policy ##

This document explains the Priority-with-retry.xml Azure API Management (APIM) policy. It aims to provide clarity on its purpose, structure, and functionality, along with guidance on how to customize it. The policy is designed to handle requests to multiple backends based on priority, with mechanisms to retry and requeue requests when necessary.

## Overview ##

The Priority-with-retry.xml policy manages backend requests while adding the following capabilities:

* *Priority-Based Backend Selection:* Routes requests to backends based on their priority and availability. Ths incoming request specifies its priority by adding a header.  This policy reads the priority header and only uses backends that accept that priority. 
* *Retry Mechanism:* Retries requests to backends that fail due to throttling or other errors. The **retry** element is configured to handle specific error codes like 429, 408, and 500. Retry count and interval are customizable.
* *Requeueing:* Requeues requests when no backends are available, returning a 429 response with a retry-after header. If all backends are throttling, the request is requeued with a 429 response, and the retry-after duration is enforced.
* *Throttling Management:* Tracks backend throttling status and enforces retry-after durations. A customizable default value is used when the retry-after header is missing from the backend response.
* *Logging:* Captures detailed logs for debugging and monitoring purposes. The backendLog includes backend selection, retries, throttling status, and custom log entries. You can optionally comment these out once you are satisfied.

This policy is particularly helpful in scenarios where requests need to be prioritized and routed efficiently while ensuring high availability.

## Policy Structure ##

The policy is organized into four sections:

* *Inbound:* Prepares variables, validates requests, and sets up configurations.
* *Backend:* Contains the main logic for backend selection, retries, and requeueing.
* *Outbound:* Customizes the response before sending it back to the client.
* *On-Error:* Handles exceptions and customizes error responses.

## Key Variables and Their Purpose ##

* *listBackends:* A cached list of backend configurations, including their URLs, priorities, throttling status, and retry-after durations. Backends can be added or removed by modifying the initialization of the listBackends variable in the <inbound> section.
* *priorityCfg:* Defines retry counts and requeue behavior for each priority level. Customize the priorityCfg variable to define custom retry counts and requeue behavior for each priority.
* *RequestPriority:* Determines the priority of the current request from the headers. A default priority of 3 is assigned if the incoming request is missing the priority header.
* *PriBackendIndxs:* A list of backend indices that match the request's priority.
* *RetryCount:* Tracks the number of retries allowed for the current request.
* *ShouldRequeue:* Indicates whether the request should be requeued.
* *backendLog:* Logs details about backend selection and request processing.

## Detailed Explanation of Each Section ##

## Inbound Section ##
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

## Backend Section ##
This section contains the main logic for backend selection, retries, and requeueing.

*Retry Logic:*

The <retry> element retries requests to backends that fail due to throttling or other errors (e.g., 429, 408, 500).
Retry count and interval are configurable.

*Backend Selection:*

The *backendIndex* variable identifies the most suitable backend based on priority and availability.
If multiple backends are available at the same priority level, one is selected randomly.

*Throttling Management:*

Backends that return a 429 response are marked as throttling, and their retryAfter value is updated based on the retry-after header or a default value.

*Requeueing:*

If all backends are throttling, the request is requeued by returning a 429 response with a retry-after header.

*Logging:*

The *backendLog* variable is updated with details about backend selection, retries, and throttling status.

## Outbound Section ##
This section customizes the response before sending it back to the client.

*Response Headers:*

The *backendLog* variable is included in the response headers for debugging and monitoring purposes.

*Response Customization:*

Additional response customization can be implemented as needed.

## On-Error Section ##
This section handles exceptions and customizes error responses.

*Error Handling:*
Exceptions are logged, and custom error responses can be returned to the client. Exceptions are logged in the backendLog, and custom error messages can be defined in this section.


## Customization Instructions ##

Adding New Backends
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
        { "defaultRetryAfter", 15 }
    });
    return backends;
}" />
```

Adjusting Priority Configuration
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

Modifying Retry Logic
To adjust the retry condition or count, modify the <retry> element in the <backend> section:

``` XML
<retry condition="@(context.Variables.GetValueOrDefault<bool>("ShouldRetry", true##" count="100" interval="500">
```

*condition:* Specifies when retries should occur.
*count:* Sets the maximum number of retries.
*interval:* Defines the delay (in milliseconds) between retries.

*Customizing Logging*
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

The Priority-with-retry.xml policy is a practical tool for managing priority-based request routing and retries in Azure API Management. By customizing the variables and logic described above, you can adapt the policy to meet your specific needs. For further assistance, refer to the Azure API Management documentation or consult your team. [[[/markdown]]]