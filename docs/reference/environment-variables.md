# Environment Variables

| Attribute | Value |
|-----------|-------|
| **Version** | 1.1 |
| **Last Updated** | 2026-05-21 |
| **Owner** | SimpleL7Proxy maintainers |
| **Review Cycle** | Quarterly |

## Summary

This document is the exhaustive reference for every environment variable accepted by SimpleL7Proxy. Operators MUST use this document when configuring deployments. For Warm/Cold/Hidden reload classification and the complete defaults reference, see [CONFIGURATION_SETTINGS.md](CONFIGURATION_SETTINGS.md).

> **TL;DR**
> - Set `Port` and at least one `Host1` connection string to start the proxy. No other settings are REQUIRED.
> - The probe path MUST be embedded in the `Host1` connection string (`host=…;probe=/health`) — `Probe_path1` is deprecated and MUST NOT be used in new deployments.
> - All variable names are case-sensitive. Unknown variables are silently ignored at startup.

> [!NOTE]
> **Units:** timeout and interval values are in **milliseconds** unless the variable name ends in `Secs` (seconds) or `Minutes`.

---

## Table of Contents

- [Minimum Required Configuration](#minimum-required-configuration)
- [Basic Configuration](#basic-configuration)
- [Health Check Configuration](#health-check-configuration)
- [Security \& Access Control](#security--access-control)
- [Request Processing Variables](#request-processing-variables)
- [Logging \& Monitoring Variables](#logging--monitoring-variables)
- [Async Processing Variables](#async-processing-variables)
- [Connection Management Variables](#connection-management-variables)
- [Azure App Configuration Variables](#azure-app-configuration-variables)
- [Backend Configuration Variables](#backend-configuration-variables)
- [User Profile Configuration](#user-profile-configuration)
- [Additional Configuration Notes](#additional-configuration-notes)
- [Validation \& Compliance](#validation--compliance)
- [Version History](#version-history)

## Minimum Required Configuration

**Every deployment MUST set at minimum:**

```bash
Port=443
Host1="host=https://your-backend.example.com;probe=/health"
```

The `probe` path is embedded in the `Host1` connection string. The legacy `Probe_path1` variable is still accepted but MUST NOT be used in new deployments.

For production deployments, the following variables MUST also be set:

| Variable | Reason |
|----------|--------|
| `Workers` | Default of `10` is insufficient for production throughput |
| `MaxQueueLength` | Default of `1000` MUST be tuned to expected peak traffic |
| `APPINSIGHTS_CONNECTIONSTRING` | REQUIRED for production observability |

> [!TIP]
> For a full annotated walkthrough of minimum setup and local development options, see [BEGINNER_DEVELOPMENT.md](BEGINNER_DEVELOPMENT.md). For copy-paste production configurations, see [SCENARIOS.md](SCENARIOS.md).

## Basic Configuration

| Variable                       | Type | Description                                                                                                                                                                                        | Default                                  |
| ----------------------------- | ---- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **MaxQueueLength**             | int | Sets the maximum number of requests allowed in the queue.                                                                                                                        | 1000                                     |
| **Port**                      | int | The port on which SimpleL7Proxy listens for incoming traffic.                                                                                                                                    | 8000                                     |
| **TERMINATION_GRACE_PERIOD_SECONDS** | int | The number of seconds SimpleL7Proxy waits before forcing itself to shut down.                                                                                                             | 30                                       |
| **Workers**                   | int | The number of worker threads used to process incoming proxy requests.                                                                                                                            | 10                                       |

> [!TIP]
> `Workers=10` is the default and is appropriate for local testing only. Production deployments MUST increase `Workers` based on expected concurrent request volume. Start at `Workers=20` and increase until queue depth stabilizes under load.

## Health Check Configuration

| Variable                       | Type | Description                                                                                                                                                                                        | Default                                  |
| ----------------------------- | ---- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **HealthProbeSidecar**        | string | Single configuration string for the health probe sidecar. Format: `Enabled=[true/false];url=[url]`. | `Enabled=false;url=http://localhost:9000` |
| **HEALTHPROBE_PORT**          | int | (Sidecar Only) The port the sidecar listens on for Kubernetes probes (/liveness, /readiness). This must match the port in your K8s/ACA probe config. | 9000                                     |

## Security & Access Control

| Variable                       | Type | Description                                                                                                                                                                                        | Default                                  |
| ----------------------------- | ---- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **SuspendedUserConfigUrl**      | string | URL or file path to fetch the list of suspended users.                                                                                                | `""` (not set)                           |
| **UseProfiles**                | bool | If true, enables user profile functionality for custom handling based on user profiles.                                                               | false                                    |
| **UserConfigRequired**         | bool | If true, a valid user profile must be found for the request to proceed. Requires restart.                                                            | false                                    |
| **UserConfigRefreshIntervalSecs** | int | Interval in seconds between user configuration refreshes. Requires restart.                                                                       | 3600 (1 hour)                            |
| **UserSoftDeleteTTLMinutes**   | int  | Time in minutes before a soft-deleted user profile is permanently removed. Requires restart.                                                         | 360 (6 hours)                            |
| **UserConfigUrl**             | string | URL or file path to fetch user configuration data.                                                                                                     | `""` (not set)                           |
| **UserPriorityThreshold**     | float | Floating point threshold (0.0-1.0) for user priority calculations. If a user owns more than this percentage of requests, their priority is lowered to prevent monopolization. For details, see [Advanced Configuration](ADVANCED_CONFIGURATION.md#user-governance). | 0.1                                      |
| **ValidateAuthAppFieldName**    | string | Name of the field in the authentication payload to validate as the App ID.                                                                            | authAppID                                |
| **ValidateAuthAppID**           | bool | If true, enables validation of an application ID in the request for authentication. Entra has a limit of 13 application IDs, use this setting to make the check in the proxy code.                                                                  | false                                    |
| **ValidateAuthAppIDHeader**     | string | Name of the header containing the App ID to validate.                                                                                                 | X-MS-CLIENT-PRINCIPAL-ID                 |
| **ValidateAuthAppIDUrl**        | string | URL or file path to fetch the list of valid App IDs for authentication.                                                                               | `""`                                     |
| **ValidateAuthConfig**          | string | Inbound auth validation config. Use `enabled=true, mode=key, header=<HeaderName>` to require an inbound key header and return 403 on mismatch.        | enabled=false, mode=none, header=S7P-KEY |
| **ValidateAuthKey1**            | string | First accepted inbound key value when key mode is enabled.                                                                                            | `""`                                     |
| **ValidateAuthKey2**            | string | Second accepted inbound key value when key mode is enabled.                                                                                           | `""`                                     |

> [!TIP]
> `ValidateAuthAppID` is designed for scenarios where Entra’s built-in app ID checking is insufficient (the Entra limit is 13 application IDs). When enabled, the proxy performs this check before the request enters the queue, preventing unauthorized apps from consuming queue capacity.

## Request Processing Variables

| Variable                       | Type | Description                                                                                                         | Default                                  |
| ----------------------------- | ---- | ------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **DefaultPriority**           | int | The default request priority when none other is specified.                                                                                                                                        | 2                                        |
| **DefaultTTLSecs**            | int | The default time-to-live for a request in seconds.                                                                                                                                               | 300                                      |
| **DependancyHeaders**         | string | Comma-separated list of headers to track dependency information.                                                                                                      | "Backend-Host, Host-URL..."              |
| **DisallowedHeaders**         | string | A comma-separated list of headers that should be removed or disallowed when forwarding requests.                                                                                                  | None                                     |
| **UserIDFieldName**          | string | JSON field name in the user profile config file used as the unique user identifier. Also accepts the legacy alias **LookupHeaderName** (kept for backward compatibility). | userId                                   |
| **PriorityKeyHeader**          | string | Name of the header that contains the priority key for determining request priority.                                                                     | S7PPriorityKey                           |
| **PriorityKeys**              | string array | Comma-separated list of keys for the header 'S7PPriorityKey'. See [Advanced Configuration](ADVANCED_CONFIGURATION.md#priority-management) for examples.  | "high,medium,low"                         |
| **PriorityValues**            | int array | Comma-separated list of priorities mapping to **PriorityKeys**. See [Advanced Configuration](ADVANCED_CONFIGURATION.md#priority-management) for examples.   | "1,2,3"                                  |
| **PriorityWorkers**           | string | Comma-separated list (e.g., "2:1,3:1") specifying worker threads per priority. See [Advanced Configuration](ADVANCED_CONFIGURATION.md#priority-management) for examples.                                                                                       | 2:1,3:1                                  |
| **RequiredHeaders**           | string | A comma-separated list of headers required for incoming requests to be deemed valid.                                                                                                             | None                                     |
| **StripRequestHeaders**       | string | Comma-separated list of headers to remove from the request before forwarding.                                                                         | (empty)                                  |
| **StripResponseHeaders**      | string | Comma-separated list of headers to remove from the response before returning to client.                                                               | (empty)                                  |
| **TimeoutHeader**               | string | Name of the header used to specify per-request timeout (in ms).                                                                                       | S7PTimeout                               |
| **TTLHeader**                  | string | Name of the header used to specify time-to-live for requests.                                                                                         | S7PTTL                                   |
| **UniqueUserHeaders**         | string | A list of header names that uniquely identify the caller or user.                                                                                                                               | X-UserID                                 |
| **UserProfileHeader**         | string | Name of the header that contains user profile information when UseProfiles is enabled.                                                                 | X-UserProfile                            |
| **ValidateHeaders**           | string | Comma-separated `SourceHeader:AllowedValuesHeader` pairs. Validates that the source header value appears in the allow-list header. Supports trailing `*` for prefix matching. See [Advanced Configuration](ADVANCED_CONFIGURATION.md#header-validation). | (empty)                                  |

## Logging & Monitoring Variables

| Variable                       | Type | Description                                                                                                         | Default                                  |
| ----------------------------- | ---- | ------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **APPINSIGHTS_CONNECTIONSTRING** | string | Specifies the connection string for Azure Application Insights. If set, the service sends structured telemetry (requests, dependencies, exceptions) to the configured Application Insights instance. App Insights is handled directly by ProxyEvent — not through the event logger pipeline. | None                                     |
| **CONTAINER_APP_NAME**         | string | The name of the container application to be used in logs and telemetry. This is automatically defined by the ACA environment.                                                                           | ContainerAppName                         |
| **CONTAINER_APP_REPLICA_NAME**  | string | Name/ID of the current container app replica (used for logging and request IDs). This is automatically defined by the ACA environment.                                                                           | ContainerAppName                         |
| **CONTAINER_APP_REVISION**      | string | Revision identifier for the current container app deployment. This is automatically defined by the ACA environment.                                                                           | ContainerAppName                         |
| **EVENT_LOGGERS**              | string | Comma-separated list of event logger backends to enable. Built-in values: `file`, `eventhub`, and `none`. You can also specify a fully-qualified class name within the assembly. Multiple backends run simultaneously. | file |
| **EVENTHUB_CONNECTIONSTRING** | string | The connection string for EventHub logging. Required when `eventhub` is in **EVENT_LOGGERS** (unless using **EVENTHUB_NAMESPACE** with managed identity). Must also set **EVENTHUB_NAME**. | None                                     |
| **EVENTHUB_NAME**             | string | The EventHub name for logging. Required when `eventhub` is in **EVENT_LOGGERS**.                                                                                                                  | None                                     |
| **EVENTHUB_NAMESPACE**        | string | The EventHub namespace (e.g., `mynamespace` or `mynamespace.servicebus.windows.net`). Used with `DefaultAzureCredential` when **EVENTHUB_CONNECTIONSTRING** is not set. Must also set **EVENTHUB_NAME**. | None                                     |
| **EVENTHUB_STARTUP_SECONDS**  | int    | Timeout in seconds for the EventHub client to establish a connection during startup. If exceeded, EventHub logging is disabled gracefully (other loggers continue). | 10                                       |
| **EVENTHUB_MAX_RECONNECT_ATTEMPTS** | int | Maximum number of reconnection attempts for the EventHub client before giving up. | 5                                        |
| **EVENTHUB_MAX_UNDRAINED_EVENTS** | int   | Maximum number of undrained (buffered) events before the EventHub logger starts dropping. | 10000                                    |
| **LogAllRequestHeaders**        | bool | If true, logs all request headers for each proxied request.                                                                                           | false                                    |
| **LogAllRequestHeadersExcept**  | string | Comma-separated list of request headers to exclude from logging, even if LogAllRequestHeaders is true.                                                | Authorization                            |
| **LogAllResponseHeaders**       | bool | If true, logs all response headers for each proxied request.                                                                                          | false                                    |
| **LogAllResponseHeadersExcept** | string | Comma-separated list of response headers to exclude from logging, even if LogAllResponseHeaders is true.                                               | Api-Key                                  |
| **LOGDATETIME**                 | bool   | When true, prepends a timestamp to each console log line. Requires restart.                                                                           | false                                    |
| **LOGFILE_NAME**                | string | Filename for the local log file when `file` is in **EVENT_LOGGERS** (or when **LOGTOFILE**=true in legacy mode).                                      | eventslog.json                           |
| **LOGTOFILE**                   | bool   | **Legacy.** When **EVENT_LOGGERS** is not set: `true` enables file logging, `false` enables EventHub logging. Prefer **EVENT_LOGGERS** for new deployments. | false                                    |
| **LogHeaders**                  | string | Comma-separated list of specific headers to log for debugging.                                                                                        | (empty)                                  |
| **LogProbes**                  | bool | If true, logs details about health probe requests to backends.                                                                                        | false                                    |
| **LogToConsole**              | string list | Comma-separated list of log categories to write to the console. Use `*` for all categories and prefix exclusions with `-`. | *,-custom |
| **LogToEvents**               | string list | Comma-separated list of log categories to send to event loggers (EventHub, file). | async,backend,circuitbreaker,custom,exception,profile,proxy,enqueued,auth |
| **LogToAI**                   | string list | Comma-separated list of log categories to send to Application Insights. Use `*` for all categories. | * |
| **LOG_LEVEL**                 | string | Minimum logging level (e.g., `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`). | Information |
| **EVENT_HEADERS**             | string | Fully-qualified class name for event header enrichment. | SimpleL7Proxy.Events.CommonEventHeaders |
| **StorageDbContainerName**    | string | Container name for request storage if enabled.                                                                   | Requests                                 |
| **StorageDbEnabled**          | bool | Enables archiving requests to storage.                                                                           | false                                    |
| **RequestIDPrefix**           | string | The prefix appended to every request ID.                                                                                                                                                         | S7P                                      |
| **GC2InternalSecs**           | int | Garbage-collection interval in seconds. Requires restart. | 300 |
| **StreamFlushInterval**       | int | Interval in milliseconds used by `StreamFlusher` to flush active response streams. Requires restart. | 250 |

> [!TIP]
> `EVENT_LOGGERS` is the REQUIRED method for configuring event backends in all new deployments. The legacy `LOGTOFILE` variable MUST NOT be used in new configurations — it is preserved only for backward compatibility. Set `EVENT_LOGGERS=file,eventhub` to enable both sinks simultaneously.

## Async Processing Variables

| Variable                       | Type | Description                                                                                                         | Default                                  |
| ----------------------------- | ---- | ------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **AsyncBlobStorageConfig**    | string | Composite connection string for Azure Blob Storage. Format: `uri=<uri>,mi=<true/false>`. Parsed into `AsyncBlobStorageAccountUri` and `AsyncBlobStorageUseMI`. | uri=https://mystorageaccount.blob.core.windows.net,mi=true |
| **AsyncBlobStorageAccountUri**| string | URI for Blob Storage (parsed from AsyncBlobStorageConfig).                                                 | (empty)                                  |
| **AsyncBlobStorageConnectionString** | string | Connection string for Azure Blob Storage (parsed from AsyncBlobStorageConfig).                           | example-connection-string                |
| **AsyncBlobStorageUseMI**     | bool | Use Managed Identity for Blob Storage (parsed from AsyncBlobStorageConfig).                              | false                                    |
| **AsyncBlobWorkerCount**      | int | Number of workers for async blob processing.                                                                     | 2                                        |
| **AsyncClientConfigFieldName**  | string | User profile field name that designates the client configuration. It contains enabled, containername, topic, timeout.                         | async-config                            |
| **AsyncClientRequestHeader**  | string | Header indicating async mode is requested.                                                               | S7PAsyncMode                             |
| **AsyncModeEnabled**          | bool | Enables or disables async processing mode. Requires restart.                                             | false                                    |
| **AsyncSBConfig**             | string | Composite connection string for Azure Service Bus. Format: `cs=<conn-string>,ns=<namespace>,q=<queue>,mi=<true/false>`. Parsed into individual SB settings. | cs=example-sb-connection-string,ns=example-namespace,q=requeststatus,mi=false |
| **AsyncSBConnectionString**   | string | Azure Service Bus connection string (parsed from AsyncSBConfig).                                                   | example-sb-connection-string             |
| **AsyncSBNamespace**          | string | Service Bus namespace (parsed from AsyncSBConfig).                                                       | (empty)                                  |
| **AsyncSBQueue**              | string | Service Bus queue name (parsed from AsyncSBConfig).                                                      | (empty)                                  |
| **AsyncSBUseMI**              | bool | Use Managed Identity for Service Bus (parsed from AsyncSBConfig).                                        | false                                    |
| **AsyncTimeout**              | int | Timeout in milliseconds for async operations. The maximum amount of time an async request will run for.    | 1800000 (30 min)                        |
| **AsyncTriggerTimeout**       | int | Timeout for async trigger operations in ms.                                                              | 10000                                    |
| **AsyncTTLSecs**              | int | TTL for async requests in seconds.                                                                       | 86400 (24 hours)                         |

> [!WARNING]
> All async variables are **Cold** settings (see [CONFIGURATION_SETTINGS.md](CONFIGURATION_SETTINGS.md)). They MUST be changed by redeploying or restarting the container — updating Azure App Configuration alone has no effect on Cold variables.

## Connection Management Variables

| Variable                       | Type | Description                                                                                                         | Default                                  |
| ----------------------------- | ---- | ------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **EnableMultipleHttp2Connections** | bool | Enables multiple HTTP/2 connections per server.                                                               | false                                    |
| **IgnoreSSLCert**             | bool | Toggles SSL certificate validation. If true, accepts self-signed certificates.                                     | false                                    |
| **KeepAliveIdleTimeoutSecs**  | int | The idle timeout (in seconds) for pooled HTTP connections before they are closed.                                  | 1200 (20 minutes)                        |
| **KeepAliveInitialDelaySecs** | int | Initial delay in seconds before sending TCP keep-alive probes.                                                    | 60                                       |
| **KeepAlivePingIntervalSecs** | int | Interval in seconds between TCP keep-alive probes.                                                                | 60                                       |
| **MultiConnIdleTimeoutSecs**  | int | Idle timeout in seconds for pooled HTTP/2 connections.                                                            | 300                                      |
| **MultiConnLifetimeSecs**     | int | Lifetime in seconds for pooled HTTP/2 connections.                                                                | 3600                                     |
| **MultiConnMaxConns**         | int | Maximum number of HTTP/2 connections per server.                                                                  | 4000                                     |

## Azure App Configuration Variables

These variables connect SimpleL7Proxy to [Azure App Configuration](https://learn.microsoft.com/azure/azure-app-configuration/overview) for centralized, hot-reloadable configuration management.

| Variable                       | Type | Description                                                                                                         | Default                                  |
| ----------------------------- | ---- | ------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **AZURE_APPCONFIG_ENDPOINT**  | string | The endpoint URL of your Azure App Configuration store (e.g. `https://myappconfig.azconfig.io`). When set, the proxy uses `DefaultAzureCredential` to authenticate. Mutually exclusive with `AZURE_APPCONFIG_CONNECTION_STRING`. | (empty — App Config disabled)            |
| **AZURE_APPCONFIG_CONNECTION_STRING** | string | Connection string for the Azure App Configuration store. Use when managed identity is not available. Mutually exclusive with `AZURE_APPCONFIG_ENDPOINT`. | (empty — App Config disabled)            |
| **AZURE_APPCONFIG_LABEL**     | string | Label filter applied when reading settings from the App Configuration store (e.g. `production`). Leave empty to read unlabeled settings. | (empty — no label filter)                |
| **AZURE_APPCONFIG_REFRESH_INTERVAL_SECONDS** | int | How often (in seconds) the proxy polls App Configuration for setting changes. | 30                                       |

## Backend Configuration Variables

| Variable                       | Type | Description                                                                                                         | Default                                  |
| ----------------------------- | ---- | ------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **AcceptableStatusCodes**     | int array | The list of HTTP status codes considered successful. If a host returns a code not in this list, it's deemed a failure. | 200, 202, 401, 403, 404, 408, 410, 412, 417, 400 |
| **APPENDHOSTSFILE / AppendHostsFile** | bool | If true, appends host/IP pairs to /etc/hosts for DNS resolution. Both case variants are supported.      | false                                    |
| **CBErrorThreshold**          | int | Number of failures within the sliding window (`CBTimeslice` seconds) that opens the circuit.                                                                          | 50                                       |
| **CBTimeslice**               | int | The duration (in seconds) of the sampling window for the circuit breaker's error rate.                             | 60                                       |
| **DnsRefreshTimeout**         | int | The number of milliseconds to force a DNS refresh, useful for making services fail over more quickly.             | 120000                                   |
| **Host1, Host2, ...**         | string | Up to 9 backend servers can be specified. Supports Connection Strings or Simple URLs. See [Backend Host Configuration](BACKEND_HOSTS.md) for full details. | None                                     |
| **HostName**                  | string | A logical name for the backend host used for identification and logging.                                          | Default                                  |
| **IterationMode**             | string | Controls how the proxy iterates through backends (SinglePass).                                           | SinglePass                               |
| **IP1, IP2, ...**             | string | IP addresses that map to corresponding Host entries if DNS is unavailable. Ignored if `ipaddress` is set in connection string. | None                                     |
| **LoadBalanceMode**           | string | Load balancing strategy: 'latency', 'roundrobin', or 'random'.                                          | latency                                  |
| **MaxAttempts**               | int | Maximum number of retry attempts for a request.                                                                   | 10                                       |
| **OAuthAudience**             | string | Legacy global OAuth audience setting. Use per-host `audience=` in `Host1` connection strings for new deployments.                                                                                                           | None                                     |
| **PollInterval**              | int | The interval (in milliseconds) at which SimpleL7Proxy polls the backend servers.                                  | 15000                                    |
| **PollTimeout**               | int | The timeout (in milliseconds) for each server poll request.                                                       | 3000                                     |
| **Probe_path1, Probe_path2, ...** | string | Path(s) to health check endpoints for each backend host. Ignored if `probe` is set in connection string.                         | echo/resource?param1=sample              |
| **SuccessRate**               | int | The minimum success rate (percentage) a backend must maintain to stay active.                                    | 80                                       |
| **Timeout**                   | int | Connection timeout (in milliseconds) for each backend request. If exceeded, SimpleL7Proxy tries the next available host. | 1200000 (20 mins)                        |
| **UseOAuthGov**               | bool | If true, uses the government cloud OAuth endpoint for token acquisition.                                         | false                                    |
| **UseSharedIterators**        | bool | When true, requests to the same path share the same host iterator for fair round-robin distribution.             | true                                     |
| **SharedIteratorTTLSeconds**  | int  | How long (in seconds) an unused shared iterator lives before cleanup.                                            | 60                                       |
| **SharedIteratorCleanupIntervalSeconds** | int | How often (in seconds) to run cleanup of stale shared iterators.                                        | 30                                       |
| **MaxEvents**                 | int  | Maximum number of events the proxy can store in memory.                                                          | 100000                                   |

> [!TIP]
> The `Host1`–`Host9` connection string format (`host=…;probe=…;path=…`) MUST be used for all new deployments. The legacy per-variable format (`Probe_path1`, `IP1`) is deprecated. Per-host auth must be configured in `HostN` using `useoauth`/`usemi`, `audience`, `api-key`, and `api-key-header`. See [BACKEND_HOSTS.md](BACKEND_HOSTS.md) for the complete key reference.

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

- **Environment Variables vs Configuration File:** While most settings can be provided via environment variables, `appsettings.json` is supported in development mode only and MUST NOT be used in production deployments.
- **Priority Configuration:** The count of entries in `PriorityKeys` MUST equal the count in `PriorityValues`. `PriorityWorkers` MUST reference only priority levels defined in `PriorityValues`. See [ADVANCED_CONFIGURATION.md](ADVANCED_CONFIGURATION.md#priority-management) for a worked example.
- **DNS Refresh:** Set `DnsRefreshTimeout` to force DNS re-resolution at a frequency appropriate for your environment. The default is 120,000 ms (2 min). This setting is relevant in environments where backend IPs change on failover.

---

## Validation & Compliance

| Check | Method | Expected Result |
|-------|--------|-----------------|
| Proxy binds port | `curl http://localhost:{Port}/health` | `200 OK` |
| Backend host registered | Proxy startup log | `Host1` URL appears in active host list |
| App Insights receiving data | Azure Portal → Application Insights → Live Metrics | Custom events appear within 60 s of first request |
| Event Hub receiving data | Azure Portal → Event Hubs → Data Explorer | Events visible within 60 s of first request |
| Warm setting reloaded | Update `Warm:Sentinel` in App Configuration | New value applied within `AZURE_APPCONFIG_REFRESH_INTERVAL_SECONDS` (default 30 s) |
| Priority mapping active | Send request with header matching `PriorityKeyHeader` value | Request served at mapped priority (visible in proxy logs) |

> [!NOTE]
> Cold settings (per [CONFIGURATION_SETTINGS.md](CONFIGURATION_SETTINGS.md)) MUST be changed by redeploying or restarting the container. Updating Azure App Configuration alone is insufficient for Cold variables.

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 1.1 | 2026-05-21 | Removed legacy `Probe_path1` from minimum configuration; added metadata, TL;DR, Minimum Required Configuration section, [!TIP]/[!WARNING] per category, Validation & Compliance, Version History; tightened Additional Configuration Notes language | SimpleL7Proxy maintainers |
| 1.0 | — | Initial version | SimpleL7Proxy maintainers |
