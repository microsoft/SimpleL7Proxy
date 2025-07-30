# Environment Variables

SimpleL7Proxy is designed to be highly configurable through environment variables, which are organized into functional categories. This approach allows for easy deployment and configuration without code changes.

## Table of Contents

- [Quick Start Configuration](#quick-start-configuration)
- [Core Configuration Variables](#core-configuration-variables)
- [Request Processing Variables](#request-processing-variables)
- [Logging & Monitoring Variables](#logging--monitoring-variables)
- [Async Processing Variables](#async-processing-variables)
- [Connection Management Variables](#connection-management-variables)
- [Backend Configuration Variables](#backend-configuration-variables)
- [User Profile Configuration](#user-profile-configuration)
- [Additional Configuration Notes](#additional-configuration-notes)

## Quick Start Configuration

To get started with minimal configuration, you need to set:
1. **Port**: The port on which the proxy listens
2. **Host1, Host2, ...**: At least one backend host to proxy requests to
3. **Probe_path1, Probe_Path2, ...**: Corresponding probe URLs

For production deployments, consider also configuring:
- **Workers**: Number of worker threads (increase for higher throughput)
- **MaxQueueLength**: Maximum queue size based on expected traffic
- **LogAllRequestHeaders**: Set to `true` for debugging
- **APPINSIGHTS_CONNECTIONSTRING**: For monitoring in Azure

## Core Configuration Variables

| Variable                       | Type | Description                                                                                                                                                                                        | Default                                  |
| ----------------------------- | ---- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **MaxQueueLength**             | int | Sets the maximum number of requests allowed in the queue.                                                                                                                        | 10                                       |
| **Port**                      | int | The port on which SimpleL7Proxy listens for incoming traffic.                                                                                                                                    | 80                                       |
| **SuspendedUserConfigUrl**      | string | URL or file path to fetch the list of suspended users.                                                                                                | file:config.json                         |
| **TERMINATION_GRACE_PERIOD_SECONDS** | int | The number of seconds SimpleL7Proxy waits before forcing itself to shut down.                                                                                                             | 30                                       |
| **UseProfiles**                | bool | If true, enables user profile functionality for custom handling based on user profiles.                                                               | false                                    |
| **UserConfigUrl**             | string | URL or file path to fetch user configuration data.                                                                                                     | file:config.json                         |
| **UserPriorityThreshold**     | float | Floating point threshold value for user priority calculations (lower values make priority promotion more likely). If a user owns more than this percentage of the requests, its priority is lowered. This prevents greedy users from using all the resources.                                    | 0.1                                      |
| **ValidateAuthAppFieldName**    | string | Name of the field in the authentication payload to validate as the App ID.                                                                            | authAppID                                |
| **ValidateAuthAppID**           | bool | If true, enables validation of an application ID in the request for authentication. Entra has a limit of 13 application IDs, use this setting to make the check in the proxy code.                                                                  | false                                    |
| **ValidateAuthAppIDHeader**     | string | Name of the header containing the App ID to validate.                                                                                                 | X-MS-CLIENT-PRINCIPAL-ID                 |
| **ValidateAuthAppIDUrl**        | string | URL or file path to fetch the list of valid App IDs for authentication.                                                                               | file:auth.json                           |
| **Workers**                   | int | The number of worker threads used to process incoming proxy requests.                                                                                                                            | 10                                       |

## Request Processing Variables

| Variable                       | Type | Description                                                                                                         | Default                                  |
| ----------------------------- | ---- | ------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **DefaultPriority**           | int | The default request priority when none other is specified.                                                                                                                                        | 2                                        |
| **DefaultTTLSecs**            | int | The default time-to-live for a request in seconds.                                                                                                                                               | 300                                      |
| **DisallowedHeaders**         | string | A comma-separated list of headers that should be removed or disallowed when forwarding requests.                                                                                                  | None                                     |
| **UserIDFieldName**          | string | The header name used to look up user information in configuration files.                                                                               | userId                                   |
| **PriorityKeyHeader**          | string | Name of the header that contains the priority key for determining request priority.                                                                     | S7PPriorityKey                           |
| **PriorityKeys**              | int array | A comma-separated list of keys that correspond to the header 'S7PPriorityKey'. Both **PriorityKeys** and **PriorityValues** should have the same number of elements.  | "12345,234"                                |
| **PriorityValues**            | int array | A comma-separated list of priorities that map to the **PriorityKeys**. Both **PriorityKeys** and **PriorityValues** should have the same number of elements.   | "1,3"                                      |
| **PriorityWorkers**           | string | A comma-separated list (e.g., "2:1,3:1") specifying how many worker threads are assigned to each priority. The first number in the tuple is the priority and the second is the number of dedicated workers for that priority level.                                                                                       | 2:1,3:1                                  |
| **RequiredHeaders**           | string | A comma-separated list of headers required for incoming requests to be deemed valid.                                                                                                             | None                                     |
| **TimeoutHeader**               | string | Name of the header used to specify per-request timeout (in ms).                                                                                       | S7PTimeout                               |
| **TTLHeader**                  | string | Name of the header used to specify time-to-live for requests.                                                                                         | S7PTTL                                   |
| **UniqueUserHeaders**         | string | A list of header names that uniquely identify the caller or user.                                                                                                                               | X-UserID                                 |
| **UserProfileHeader**         | string | Name of the header that contains user profile information when UseProfiles is enabled.                                                                 | X-UserProfile                            |
| **ValidateHeaders**           | string | Comma-separated list of key:value pairs for header validation.                                                     | (empty)                                  |

## Logging & Monitoring Variables

| Variable                       | Type | Description                                                                                                         | Default                                  |
| ----------------------------- | ---- | ------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **APPINSIGHTS_CONNECTIONSTRING** | string | Specifies the connection string for Azure Application Insights. If set, the service sends logs to the configured Application Insights instance.                                                 | None                                     |
| **CONTAINER_APP_NAME**         | string | The name of the container application to be used in logs and telemetry. This is automatically defined by the ACA environment.                                                                           | ContainerAppName                         |
| **CONTAINER_APP_REPLICA_NAME**  | string | Name/ID of the current container app replica (used for logging and request IDs). This is automatically defined by the ACA environment.                                                                           | ContainerAppName                         |
| **CONTAINER_APP_REVISION**      | string | Revision identifier for the current container app deployment. This is automatically defined by the ACA environment.                                                                           | ContainerAppName                         |
| **EVENTHUB_CONNECTIONSTRING** | string | The connection string for EventHub logging. Must also set **EVENTHUB_NAME**.                                                                                                                      | None                                     |
| **EVENTHUB_NAME**             | string | The EventHub namespace for logging. Must also set **EVENTHUB_CONNECTIONSTRING**.                                                                                                                  | None                                     |
| **LogAllRequestHeaders**        | bool | If true, logs all request headers for each proxied request.                                                                                           | false                                    |
| **LogAllRequestHeadersExcept**  | string | Comma-separated list of request headers to exclude from logging, even if LogAllRequestHeaders is true.                                                | Authorization                            |
| **LogAllResponseHeaders**       | bool | If true, logs all response headers for each proxied request.                                                                                          | false                                    |
| **LogAllResponseHeadersExcept** | string | Comma-separated list of response headers to exclude from logging, even if LogAllResponseHeaders is true.                                               | Api-Key                                  |
| **LOGFILE**                     | string | If set, logs events to the specified file instead of EventHub (for debugging/testing only; not for production use).                                   | events.log (if enabled in code)          |
| **LogHeaders**                  | string | Comma-separated list of specific headers to log for debugging.                                                                                        | (empty)                                  |
| **LogProbes**                  | bool | If true, logs details about health probe requests to backends.                                                                                        | false                                    |
| **LogConsoleEvent**           | bool | If true, logs events to the console output.                                                                      | true                                     |
| **RequestIDPrefix**           | string | The prefix appended to every request ID.                                                                                                                                                         | S7P                                      |

## Async Processing Variables

| Variable                       | Type | Description                                                                                                         | Default                                  |
| ----------------------------- | ---- | ------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **AsyncModeEnabled**          | bool | If true, enables asynchronous request processing. This triggers the server to connect to blob storage and the service bus.                            | false                                    |
| **AsyncTimeout**              | int | Timeout in milliseconds for async operations. The maximum amount of time async request will run for.       | 1800000 (30 min)                        |
| **AsyncClientBlobTimeoutFieldName** | string | User profile field name that contains number of seconds the blob will have access for. After this number of seconds, the blob will no longer be accessible (but not deleted).  | async-blobaccess-timeout | 
| **AsyncBlobStorageConnectionString** | string | Connection string for Azure Blob Storage used in async mode.                                             | example-connection-string                |
| **AsyncClientBlobFieldname** | string | User profile field name that contain the client's blob container name. Request responses will be created here.                                 | async-blobname                           |
| **AsyncClientAllowedFieldName**  | string | User profile field name that designates if the client is allowed to use async mode. Set to "true" in the user profile to enable.                         | async-allowed                            |
| **AsyncSBConnectionString**   | string | Azure Service Bus connection string for async operations.                                                          | example-sb-connection-string             |
| **AsyncSBTopicFieldName**        | string | User profile field name for Service Bus topic that is specific to the client. The user profile should contain the topic that the client has access to.                                                                   | async-topic                              |

## Connection Management Variables

| Variable                       | Type | Description                                                                                                         | Default                                  |
| ----------------------------- | ---- | ------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **EnableMultipleHttp2Connections** | bool | Enables multiple HTTP/2 connections per server.                                                               | false                                    |
| **IgnoreSSLCert**             | bool | Toggles SSL certificate validation. If true, accepts self-signed certificates.                                     | false                                    |
| **KeepAliveIdleTimeoutSecs**  | int | The idle timeout (in seconds) for pooled HTTP connections before they are closed.                                  | 1200 (20 minutes)                        |
| **KeepAliveInitialDelaySecs** | int | Initial delay in seconds before sending TCP keep-alive probes.                                                    | 60                                       |
| **KeepAlivePingDelaySecs**    | int | The delay (in seconds) before sending a TCP keep-alive probe on an idle connection.                                | 30                                       |
| **KeepAlivePingIntervalSecs** | int | Interval in seconds between TCP keep-alive probes.                                                                | 60                                       |
| **KeepAlivePingTimeoutSecs**  | int | The timeout (in seconds) to wait for a response to a TCP keep-alive probe before closing the connection.           | 30                                       |
| **MultiConnIdleTimeoutSecs**  | int | Idle timeout in seconds for pooled HTTP/2 connections.                                                            | 300                                      |
| **MultiConnLifetimeSecs**     | int | Lifetime in seconds for pooled HTTP/2 connections.                                                                | 3600                                     |
| **MultiConnMaxConns**         | int | Maximum number of HTTP/2 connections per server.                                                                  | 4000                                     |

## Backend Configuration Variables

| Variable                       | Type | Description                                                                                                         | Default                                  |
| ----------------------------- | ---- | ------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **AcceptableStatusCodes**     | int array | The list of HTTP status codes considered successful. If a host returns a code not in this list, it's deemed a failure. | 200, 401, 403, 404, 408, 410, 412, 417, 400 |
| **APPENDHOSTSFILE / AppendHostsFile** | bool | If true, appends host/IP pairs to /etc/hosts for DNS resolution. Both case variants are supported.      | false                                    |
| **CBErrorThreshold**          | int | The error threshold percentage for the circuit breaker. If the error rate surpasses this value in **CBTimeslice** time period, the circuit breaks.     | 50                                       |
| **CBTimeslice**               | int | The duration (in seconds) of the sampling window for the circuit breaker's error rate.                             | 60                                       |
| **DnsRefreshTimeout**         | int | The number of milliseconds to force a DNS refresh, useful for making services fail over more quickly.             | 120000                                   |
| **Host1, Host2, ...**         | string | Up to 9 backend servers can be specified. Each Host should be in the form "http(s)://fqdnhostname" for proper DNS resolution. | None                                     |
| **HostName**                  | string | A logical name for the backend host used for identification and logging.                                          | Default                                  |
| **IP1, IP2, ...**             | string | IP addresses that map to corresponding Host entries if DNS is unavailable. Must define Host, IP, and APPENDHOSTSFILE. | None                                     |
| **OAuthAudience**             | string | The audience used for OAuth token requests, if **UseOAuth** is enabled.                                                                                                           | None                                     |
| **PollInterval**              | int | The interval (in milliseconds) at which SimpleL7Proxy polls the backend servers.                                  | 15000                                    |
| **PollTimeout**               | int | The timeout (in milliseconds) for each server poll request.                                                       | 3000                                     |
| **Probe_path1, Probe_path2, ...** | string | Path(s) to health check endpoints for each backend host (e.g., /health, /readiness).                         | echo/resource?param1=sample              |
| **SuccessRate**               | int | The minimum success rate (percentage) a backend must maintain to stay active.                                    | 80                                       |
| **Timeout**                   | int | Connection timeout (in milliseconds) for each backend request. If exceeded, SimpleL7Proxy tries the next available host. | 1200000 (20 mins)                        |
| **UseOAuth**                  | bool | Enables or disables OAuth token fetching for outgoing requests.                                                  | false                                    |
| **UseOAuthGov**               | bool | If true, uses the government cloud OAuth endpoint for token acquisition.                                         | false                                    |

## User Profile Configuration

This is a JSON formatted file that gets read every hour. It can be fetched from a URL or a file location, depending on the configuration. Here is an example file:

```json
[
    {
        "userId": "123456",
        "S7PPriorityKey": "12345",
        "Header1": "Value1",
        "Header2": "Value2",
        "async-blobname": "data",
        "async-topic": "status",
        "async-allowed": true
    },
    {
        "userId": "123455",
        "S7PPriorityKey": "12345",
        "Header1": "Value1",
        "Header2": "Value2",
        "async-blobname": "data-12355",
        "async-topic": "status-12355",
        "async-allowed": true
    },
    {
        "userId": "123457",
        "Header1": "Value1",
        "Header2": "Value2",
        "AsyncEnabled": false
    }
]
```

## Additional Configuration Notes

- **Environment Variables vs Configuration File**: While most settings can be provided via environment variables, you can also use appsettings.json in development mode.

- **Priority Configuration**: When setting up priorities, ensure the number of values in `PriorityKeys` and `PriorityValues` match, and that `PriorityWorkers` references valid priority levels.

- **DNS Refresh**: If you're experiencing issues with DNS resolution in dynamic environments, adjust the `DnsRefreshTimeout` value to force more frequent DNS lookups.
