# Environment Variables

SimpleL7Proxy is designed to be highly configurable through environment variables, which are organized into functional categories. This approach allows for easy deployment and configuration without code changes.

## Table of Contents

- [Environment Variables](#environment-variables)
  - [Table of Contents](#table-of-contents)
  - [Quick Start Configuration](#quick-start-configuration)
  - [Core Configuration Variables](#core-configuration-variables)
  - [Request Processing Variables](#request-processing-variables)
  - [Logging \& Monitoring Variables](#logging--monitoring-variables)
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

## Basic Configuration

| Variable                       | Type | Description                                                                                                                                                                                        | Default                                  |
| ----------------------------- | ---- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **MaxQueueLength**             | int | Sets the maximum number of requests allowed in the queue.                                                                                                                        | 10                                       |
| **Port**                      | int | The port on which SimpleL7Proxy listens for incoming traffic.                                                                                                                                    | 80                                       |
| **TERMINATION_GRACE_PERIOD_SECONDS** | int | The number of seconds SimpleL7Proxy waits before forcing itself to shut down.                                                                                                             | 30                                       |
| **Workers**                   | int | The number of worker threads used to process incoming proxy requests.                                                                                                                            | 10                                       |

## Security & Access Control

| Variable                       | Type | Description                                                                                                                                                                                        | Default                                  |
| ----------------------------- | ---- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **SuspendedUserConfigUrl**      | string | URL or file path to fetch the list of suspended users.                                                                                                | file:config.json                         |
| **UseProfiles**                | bool | If true, enables user profile functionality for custom handling based on user profiles.                                                               | false                                    |
| **UserConfigUrl**             | string | URL or file path to fetch user configuration data.                                                                                                     | file:config.json                         |
| **UserPriorityThreshold**     | float | Floating point threshold (0.0-1.0) for user priority calculations. If a user owns more than this percentage of requests, their priority is lowered to prevent monopolization. For details, see [Advanced Configuration](ADVANCED_CONFIGURATION.md#user-governance). | 0.1                                      |
| **ValidateAuthAppFieldName**    | string | Name of the field in the authentication payload to validate as the App ID.                                                                            | authAppID                                |
| **ValidateAuthAppID**           | bool | If true, enables validation of an application ID in the request for authentication. Entra has a limit of 13 application IDs, use this setting to make the check in the proxy code.                                                                  | false                                    |
| **ValidateAuthAppIDHeader**     | string | Name of the header containing the App ID to validate.                                                                                                 | X-MS-CLIENT-PRINCIPAL-ID                 |
| **ValidateAuthAppIDUrl**        | string | URL or file path to fetch the list of valid App IDs for authentication.                                                                               | file:auth.json                           |

## Request Processing Variables

| Variable                       | Type | Description                                                                                                         | Default                                  |
| ----------------------------- | ---- | ------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **DefaultPriority**           | int | The default request priority when none other is specified.                                                                                                                                        | 2                                        |
| **DefaultTTLSecs**            | int | The default time-to-live for a request in seconds.                                                                                                                                               | 300                                      |
| **DependancyHeaders**         | string | Comma-separated list of headers to track dependency information.                                                                                                      | "Backend-Host, Host-URL..."              |
| **DisallowedHeaders**         | string | A comma-separated list of headers that should be removed or disallowed when forwarding requests.                                                                                                  | None                                     |
| **UserIDFieldName**          | string | The header name used to look up user information in configuration files.                                                                               | userId                                   |
| **PriorityKeyHeader**          | string | Name of the header that contains the priority key for determining request priority.                                                                     | S7PPriorityKey                           |
| **PriorityKeys**              | int array | Comma-separated list of keys for the header 'S7PPriorityKey'. See [Advanced Configuration](ADVANCED_CONFIGURATION.md#priority-management) for examples.  | "12345,234"                                |
| **PriorityValues**            | int array | Comma-separated list of priorities mapping to **PriorityKeys**. See [Advanced Configuration](ADVANCED_CONFIGURATION.md#priority-management) for examples.   | "1,3"                                      |
| **PriorityWorkers**           | string | Comma-separated list (e.g., "2:1,3:1") specifying worker threads per priority. See [Advanced Configuration](ADVANCED_CONFIGURATION.md#priority-management) for examples.                                                                                       | 2:1,3:1                                  |
| **RequiredHeaders**           | string | A comma-separated list of headers required for incoming requests to be deemed valid.                                                                                                             | None                                     |
| **StripRequestHeaders**       | string | Comma-separated list of headers to remove from the request before forwarding.                                                                         | (empty)                                  |
| **StripResponseHeaders**      | string | Comma-separated list of headers to remove from the response before returning to client.                                                               | (empty)                                  |
| **TimeoutHeader**               | string | Name of the header used to specify per-request timeout (in ms).                                                                                       | S7PTimeout                               |
| **TTLHeader**                  | string | Name of the header used to specify time-to-live for requests.                                                                                         | S7PTTL                                   |
| **UniqueUserHeaders**         | string | A list of header names that uniquely identify the caller or user.                                                                                                                               | X-UserID                                 |
| **UserProfileHeader**         | string | Name of the header that contains user profile information when UseProfiles is enabled.                                                                 | X-UserProfile                            |
| **ValidateHeaders**           | string | Comma-separated list of key:value pairs for header validation. See [Advanced Configuration](ADVANCED_CONFIGURATION.md#header-validation) for examples.                                                     | (empty)                                  |

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
| **LogConsole**                | bool | Enables general console logging.                                                                                 | true                                     |
| **LogPoller**                 | bool | Enables logging for the backend poller.                                                                          | true                                     |
| **StorageDbContainerName**    | string | Container name for request storage if enabled.                                                                   | Requests                                 |
| **StorageDbEnabled**          | bool | Enables archiving requests to storage.                                                                           | false                                    |
| **RequestIDPrefix**           | string | The prefix appended to every request ID.                                                                                                                                                         | S7P                                      |

## Async Processing Variables

| Variable                       | Type | Description                                                                                                         | Default                                  |
| -------BlobStorageAccountUri**| string | Uri for Blob Storage (overrides AsyncBlobStorageConfig).                                                 | (empty)                                  |
| **AsyncBlobStorageUseMI**     | bool | Use Managed Identity for Blob Storage.                                                                   | false                                    |
| **AsyncBlobWorkerCount**      | int | Number of workers for async blob processing.                                                                     | 2                                        |
| **AsyncClientConfigFieldName**  | string | User profile field name that designates if the client configuration. It contains enabled, containername, topic, timeout.                         | async-config                            |
| **AsyncClientRequestHeader**  | string | Header indicating async mode is requested.                                                               | AsyncMode                                |
| **AsyncSBConnectionString**   | string | Azure Service Bus connection string for async operations.                                                          | example-sb-connection-string             |
| **AsyncSBNamespace**          | string | Service Bus namespace (overrides AsyncSBConfig).                                                         | (empty)                                  |
| **AsyncSBQueue**              | string | Service Bus queue name (overrides AsyncSBConfig).                                                        | (empty)                                  |
| **AsyncSBUseMI**              | bool | Use Managed Identity for Service Bus.                                                                    | false                                    |
| **AsyncTTLSecs**              | int | TTL for async requests in seconds.                                                                       | 86400 (24 hours)                         |
| **AsyncTriggerTimeout**       | int | Timeout for async trigger operations in ms.                                                              | 10000                          | false                                    |
| **AsyncTimeout**              | int | Timeout in milliseconds for async operations. The maximum amount of time async request will run for.       | 1800000 (30 min)                        |
| **AsyncBlobStorageConnectionString** | string | Connection string for Azure Blob Storage used in async mode.                                             | example-connection-string                |
| **AsyncClientConfigFieldName**  | string | User profile field name that designates if the client configuration. It contains enabled, containername, topic, timeout.                         | async-config                            |
| **AsyncSBConnectionString**   | string | Azure Service Bus connection string for async operations.                                                          | example-sb-connection-string             |

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

| **HealthProbeSidecar**        | string | Configuration for sidecar health probes (format: "Enabled=true;url=...").                               | Enabled=false;url=http://localhost:9000  |
| **IterationMode**             | string | Controls how the proxy iterates through backends (SinglePass).                                           | SinglePass                               |
| **LoadBalanceMode**           | string | Load balancing strategy: 'latency', 'roundrobin', or 'random'.                                          | latency                                  |
| **MaxAttempts**               | int | Maximum number of retry attempts for a request.                                                                   | 10                                       |
| **riable                       | Type | Description                                                                                                         | Default                                  |
| ----------------------------- | ---- | ------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **AcceptableStatusCodes**     | int array | The list of HTTP status codes considered successful. If a host returns a code not in this list, it's deemed a failure. | 200, 401, 403, 404, 408, 410, 412, 417, 400 |
| **APPENDHOSTSFILE / AppendHostsFile** | bool | If true, appends host/IP pairs to /etc/hosts for DNS resolution. Both case variants are supported.      | false                                    |
| **CBErrorThreshold**          | int | The error threshold percentage for the circuit breaker. If the error rate surpasses this value in **CBTimeslice** time period, the circuit breaks.     | 50                                       |
| **CBTimeslice**               | int | The duration (in seconds) of the sampling window for the circuit breaker's error rate.                             | 60                                       |
| **DnsRefreshTimeout**         | int | The number of milliseconds to force a DNS refresh, useful for making services fail over more quickly.             | 120000                                   |
| **Host1, Host2, ...**         | string | Up to 9 backend servers can be specified. Supports Connection Strings or Simple URLs. See [Backend Host Configuration](BACKEND_HOSTS.md) for full details. | None                                     |
| **HostName**                  | string | A logical name for the backend host used for identification and logging.                                          | Default                                  |
| **IP1, IP2, ...**             | string | IP addresses that map to corresponding Host entries if DNS is unavailable. Ignored if `ipaddress` is set in connection string. | None                                     |
| **OAuthAudience**             | string | The audience used for OAuth token requests, if **UseOAuth** is enabled.                                                                                                           | None                                     |
| **PollInterval**              | int | The interval (in milliseconds) at which SimpleL7Proxy polls the backend servers.                                  | 15000                                    |
| **PollTimeout**               | int | The timeout (in milliseconds) for each server poll request.                                                       | 3000                                     |
| **Probe_path1, Probe_path2, ...** | string | Path(s) to health check endpoints for each backend host. Ignored if `probe` is set in connection string.                         | echo/resource?param1=sample              |
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
        "async-config": "enabled=true, containername=data, topic=status"
    },
    {
        "userId": "123455",
        "S7PPriorityKey": "12345",
        "Header1": "Value1",
        "Header2": "Value2",
        "async-config": "enabled=true, containername=data-12355, topic=status-12355"
    },
    {
        "userId": "123457",
        "Header1": "Value1",
        "Header2": "Value2",
        "async-config": false
    }
]
```

## Additional Configuration Notes

- **Environment Variables vs Configuration File**: While most settings can be provided via environment variables, you can also use appsettings.json in development mode.

- **Priority Configuration**: When setting up priorities, ensure the number of values in `PriorityKeys` and `PriorityValues` match, and that `PriorityWorkers` references valid priority levels.

- **DNS Refresh**: If you're experiencing issues with DNS resolution in dynamic environments, adjust the `DnsRefreshTimeout` value to force more frequent DNS lookups.
