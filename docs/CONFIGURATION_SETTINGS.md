# Configuration Settings by Goal

This page covers all configuration settings for the proxy. Every setting can be defined as an environment variable or stored in Azure App Configuration. If a variable is not defined, the proxy uses the default value shown.

Settings are loaded and applied in this order:
1. Environment variables from the ACA revision.
2. App Configuration **Cold** settings overlay the environment variables — only loaded at startup.
3. App Configuration **Warm** settings are reloaded on a configurable interval (default: 30 seconds).

App Configuration allows settings to be changed without redeploying the container. Expand to connect the proxy to an App Configuration store using managed identity or a connection string.

<details>
<summary><strong>App config setup</strong></summary>


**Auth option 1 — Managed identity** (recommended)
1. Set `AZURE_APPCONFIG_ENDPOINT`.
2. Assign the `App Configuration Data Reader` role to the container app's managed identity.

**Auth option 2 — Connection string**
1. Set `AZURE_APPCONFIG_CONNECTION_STRING`.

**Scoping and refresh** (both options)  
- `AZURE_APPCONFIG_LABEL` — filters which keys are loaded by label.  
- `AZURE_APPCONFIG_REFRESH_INTERVAL_SECONDS` — how often Warm settings are polled.

> Settings marked **Warm** reload automatically on the refresh interval; settings marked **Cold** require a restart.

![Warm](https://img.shields.io/badge/Warm-hot--reloaded%20(no%20restart)-2EA44F) ![Cold](https://img.shields.io/badge/Cold-restart%20required-E5534B) 
</details>

---

> Units used in this doc: timeout/interval values are milliseconds unless the property ends with `Secs` or `Minutes`.

# Server

Server settings define how the proxy starts, scales, and shuts down. Use this section to tune runtime capacity, stability, and health signaling behavior.

These settings determine how many requests the proxy can handle simultaneously and how it behaves on startup and shutdown. Expand to tune worker count, queue depth, and graceful drain behavior.

<details>
<summary><strong>Overall settings</strong></summary>

| App Configuration Key | Env Var | Default | Why It Matters |
|---|---|---|---|
| <small>`Server:Port`</small> | <small>`Port`</small> | `8000`</small> | The container port the proxy listens on. |
| <small>`Server:Workers`</small> | <small>`Workers`</small> | `10`</small> | The number of concurrent workers. |
| <small>`Server:MaxQueueLength`</small> | <small>`MaxQueueLength`</small> | `1000`</small> | Maximum number of backlogged requests. |
| <small>`Server:TerminationGracePeriodSeconds`</small> | <small>`TerminationGracePeriodSeconds`</small> | `30`</small> | Gives endpoints time to drain in-flight requests during shutdown. |
| <small>`Server:PriorityWorker`</small> | <small>`PriorityWorker`</small> | `2:1, 3:1`</small> | Dedicated workers per priority level (`priority:count` pairs). If no work exists at their assigned priority, these workers process the next available request. Prevents lower-priority requests from starving. |
| <small>`Server:GC2InternalSecs`</small> | <small>`GC2InternalSecs`</small> | `300`</small> | How often (in seconds) the proxy runs garbage collection. |
| <small>`Server:StreamFlushInterval`</small> | <small>`StreamFlushInterval`</small> | `250`</small> | Interval (in milliseconds) used by StreamFlusher to flush active response streams. |
| <small>`Security:UseOAuthGov`</small> | <small>`UseOAuthGov`</small> | `false`</small> | Switches OAuth endpoint logic to government cloud boundary. |
| <small>`Security:IgnoreSSLCert`</small> | <small>`IgnoreSSLCert`</small> | `false`</small> | TLS certificate validation bypass for explicit non-production scenarios. |
| <small>`Server:MaxUndrainedEvents`</small> | <small>`MaxUndrainedEvents`</small> | <small>`100`</small> | Backpressure cap for buffered Event Hub events. |
</details>

ACA monitors the container via liveness, readiness, and startup probes. By default these probes hit the proxy directly — expand to redirect them to a sidecar and reduce probe overhead on the proxy in high-volume deployments.

<details>
<summary><strong>Health Probe Sidecar</strong></summary>

| App Configuration Key | Env Var | Default | Why It Matters |
|---|---|---|---|
| <small>`HealthProbe:Sidecar`</small> | <small>`HealthProbeSidecar`</small> | <small>`Enabled=false`</small> | When enabled, redirects probe traffic to a sidecar container instead of the proxy. The `url` field sets the sidecar's listening address (default `http://localhost:9000`). |

> **High-volume deployments:** To offload probe traffic from the proxy, deploy a sidecar container listening on port `9000` and set `Enabled=true;url=http://localhost:9000`. Point the container app's health probes at `localhost:9000`. The proxy pushes its health status and stats to that address, so the sidecar handles all probe requests.

</details>

The circuit breaker prevents the proxy from sending requests to consistently failing backends. Expand to configure the error threshold and time window that triggers an endpoint to be taken out of rotation.

<details>
<summary><strong>Circuit Breaker</strong></summary>

| App Configuration Key | Env Var | Default | Why It Matters |
|---|---|---|---|
| <small>`CircuitBreaker:ErrorThreshold`</small> | <small>`CircuitBreakerErrorThreshold`</small> | <small>`50`</small> | Number of errors within the timeslice that triggers the circuit to open. With the defaults, more than 50 errors in 60 seconds opens the circuit. |
| <small>`CircuitBreaker:Timeslice`</small> | <small>`CircuitBreakerTimeslice`</small> | <small>`60`</small> | Sliding window in seconds over which errors are counted toward the threshold. |
| <small>`Response:AcceptableStatusCodes`</small> | <small>`AcceptableStatusCodes`</small> | <small>`200,202,401,403,404,408,410,412,417,400`</small> | Status codes that are **not** counted as errors. Any response code outside this list increments the error counter toward the circuit-breaker threshold. |
</details>

Connection pooling controls how long backend TCP connections stay alive and how many can exist at once. Expand to tune these values if you see connection exhaustion or stale connections under high load.

<details>
<summary><strong>Connection Management</strong></summary>

> These settings are not exported to App Configuration. Set them as environment variables, or manually add them with the `Cold:` prefix in App Configuration — changes take effect on restart.

| App Configuration Key | Env Var | Default | Why It Matters |
|---|---|---|---|
| <small>`Transport:KeepAliveInitialDelaySecs`</small> | <small>`KeepAliveInitialDelaySecs`</small> | <small>`60`</small> | Delay in seconds before the first TCP keep-alive probe is sent on a backend connection. |
| <small>`Transport:KeepAlivePingIntervalSecs`</small> | <small>`KeepAlivePingIntervalSecs`</small> | <small>`60`</small> | Interval in seconds between TCP keep-alive probes. |
| <small>`Transport:KeepAliveIdleTimeoutSecs`</small> | <small>`KeepAliveIdleTimeoutSecs`</small> | <small>`1200`</small> | How long a backend connection can be idle before it is closed. |
| <small>`Transport:EnableMultipleHttp2Connections`</small> | <small>`EnableMultipleHttp2Connections`</small> | <small>`false`</small> | Allows multiple HTTP/2 connections to the same backend endpoint. Useful when a single connection is saturated. |
| <small>`Transport:MultiConnLifetimeSecs`</small> | <small>`MultiConnLifetimeSecs`</small> | <small>`3600`</small> | Maximum lifetime of a pooled backend connection before it is recycled. |
| <small>`Transport:MultiConnIdleTimeoutSecs`</small> | <small>`MultiConnIdleTimeoutSecs`</small> | <small>`300`</small> | How long a pooled backend connection can sit idle before it is closed. |
| <small>`Transport:MultiConnMaxConns`</small> | <small>`MultiConnMaxConns`</small> | <small>`4000`</small> | Maximum number of pooled backend connections across all endpoints. |

</details>


---
# Profiles

Profile settings control how user-specific policy is loaded and applied at runtime. Use these settings to drive per-user routing, headers, and access behavior.

Use these settings to control what the proxy strips, forwards, or adds on behalf of callers — including queue priority assignment and per-caller timeout and TTL overrides.

<details>
<summary><strong>Request Header Policy</strong></summary>

| App Configuration Key | Env Var | Default | Why It Matters |
|---|---|---|---|
| <small>`Request:StripRequestHeaders`</small> | <small>`StripRequestHeaders`</small> | <small>`[]`</small> | Headers to remove from the inbound request before forwarding to the backend. |
| <small>`Response:StripResponseHeaders`</small> | <small>`StripResponseHeaders`</small> | <small>`[]`</small> | Headers to remove from the backend response before returning to the caller. |
| <small>`Request:Priority:DefaultPriority`</small> | <small>`DefaultPriority`</small> | <small>`2`</small> | Priority assigned to requests that don't supply a recognized priority key. |
| <small>`Request:Priority:PriorityKeys`</small> | <small>`PriorityKeys`</small> | <small>`12345,234`</small> | Priority key values callers can send in the priority header. Paired by position with `PriorityValues`. |
| <small>`Request:Priority:PriorityValues`</small> | <small>`PriorityValues`</small> | <small>`1,3`</small> | Priority level for each key in `PriorityKeys`. Key `12345` → priority `1`, key `234` → priority `3`. |
| <small>`Request:Headers:PriorityKeyHeader`</small> | <small>`PriorityKeyHeader`</small> | <small>`S7PPriorityKey`</small> | Header name callers use to send their priority key. |
| <small>`Request:Priority:GreedyUserThreshold`</small> | <small>`UserPriorityThreshold`</small> | <small>`0.1`</small> | Maximum queue share a single user can hold (0.1 = 10%). Prevents one caller from monopolizing the queue. |
| <small>`Request:DefaultTTLSecs`</small> | <small>`DefaultTTLSecs`</small> | <small>`300`</small> | How long (in seconds) a queued request is kept before it expires and is dropped. |
| <small>`Request:Headers:TimeoutHeader`</small> | <small>`TimeoutHeader`</small> | <small>`S7PTimeout`</small> | Header name callers can use to override the per-request backend timeout. |
| <small>`Request:Headers:TTLHeader`</small> | <small>`TTLHeader`</small> | <small>`S7PTTL`</small> | Header name callers can use to override the TTL for their queued request. |
</details>


Profiles allow the proxy to apply per-user policy — such as routing priority or blocking suspended users — loaded from a remote JSON file. Expand to configure the profile source URL, refresh interval, and how callers are matched to their profile.

<details>
<summary><strong>Profile Configuration</strong></summary>

| App Configuration Key | Env Var | Default | Why It Matters |
|---|---|---|---|
| <small>`Profiles:User:UseProfiles`</small> | <small>`UseProfiles`</small> | <small>`false`</small> | Master switch for the user profile system. Set to `true` to enable per-user policy behavior. |
| <small>`Profiles:User:UserConfigUrl`</small> | <small>`UserConfigUrl`</small> | <small>`""`</small> | URL of the JSON file containing per-user policy records. Required when `UseProfiles` is `true`. |
| <small>`Profiles:SuspendedUser:ConfigUrl`</small> | <small>`SuspendedUserConfigUrl`</small> | <small>`""`</small> | URL of the suspended-user list. Requests from users on this list are blocked. |
| <small>`Profiles:RefreshIntervalSecs`</small> | <small>`UserConfigRefreshIntervalSecs`</small> | <small>`3600`</small> | How often (in seconds) the proxy re-downloads the profiles from the URL. |
| <small>`Profiles:User:IDFieldName`</small> | <small>`UserIDFieldName`</small> | <small>`userId`</small> | Field name in each profile JSON record that holds the user ID. The proxy matches the value of `UserProfileHeader` against this field to find the caller's profile. Once found, all fields from that profile record are added to the forwarded request. |
| <small>`Request:Headers:UniqueUserHeaders`</small> | <small>`UniqueUserHeaders`</small> | <small>`X-UserID`</small> | Inbound header names whose values are concatenated to form a per-user key for chargeback and queue fairness (`UserPriorityThreshold`). |
| <small>`Profiles:User:ProfileHeader`</small> | <small>`UserProfileHeader`</small> | <small>`X-UserProfile`</small> | Inbound header whose value the proxy uses as the caller's user ID. This value is matched against the `UserIDFieldName` field in the profile records to find the caller's profile. |
| <small>`Profiles:SoftDeleteTTLMinutes`</small> | <small>`UserSoftDeleteTTLMinutes`</small> | <small>`360`</small> | How long (in minutes) the proxy retains a cached profile after a user is removed from the profile file. |

</details>

---
# Validation

The proxy can be configured to reject unauthorized callers using either **OAuth** or **Keys**. Key authentication requires the caller to send a header with a recognized key. OAuth requires Entra ID integration. See [POC: Securing the proxy](POC-Secure-the-proxy.md).

Key authentication blocks callers that don't supply a recognized key in a designated header. Expand to configure the header name and the accepted key values.

<details>
<summary><strong>Key Authentication</strong></summary>

| App Configuration Key | Env Var | Default | Why It Matters |
|---|---|---|---|
| <small>`Profiles:Auth:Config`</small> | <small>`ValidateAuthConfig`</small> | <small>`enabled=false,mode=key,header=S7P-KEY`</small> | Defines key-validation behavior (`enabled`, `mode`, `header`). |
| <small>`Profiles:Auth:Key1`</small> | <small>`ValidateAuthKey1`</small> | <small>`key1`</small> | First accepted inbound key. |
| <small>`Profiles:Auth:Key2`</small> | <small>`ValidateAuthKey2`</small> | <small>`key2`</small> | Second accepted inbound key for rotation support. |
</details>

When EasyAuth is enabled, the proxy can further restrict access to a specific list of application IDs. Expand to configure the allowlist source and the token field used to identify the caller's app ID.

<details>
<summary><strong>Application ID</strong></summary>

| App Configuration Key | Env Var | Default | Why It Matters |
|---|---|---|---|
| <small>`Profiles:Auth:ValidateAppIDEnabled`</small> | <small>`ValidateAuthAppID`</small> | <small>`false`</small> | Enables app ID validation. When `true`, the proxy checks the caller's app ID against the allowlist. |
| <small>`Profiles:Auth:ConfigUrl`</small> | <small>`ValidateAuthAppIDUrl`</small> | <small>`""`</small> | URL of the JSON file containing the list of approved app IDs. |
| <small>`Profiles:Auth:ValidateFieldName`</small> | <small>`ValidateAuthAppFieldName`</small> | <small>`authAppID`</small> | Field name in the EasyAuth token payload that contains the caller's app ID. |
| <small>`Profiles:Auth:ValidateAppIDHeader`</small> | <small>`ValidateAuthAppIDHeader`</small> | <small>`X-MS-CLIENT-PRINCIPAL-ID`</small> | Inbound header that carries the caller's app ID (injected by EasyAuth). |
</details>

Use these settings to enforce inbound request rules — reject calls missing required headers, block forbidden headers, or fail closed if the profile source is unavailable.

<details>
<summary><strong>Header validation</strong></summary>

These rules validate incoming request shape before processing and help enforce consistent caller behavior.

| App Configuration Key | Env Var | Default | Why It Matters |
|---|---|---|---|
| <small>`Request:RequiredHeaders`</small> | <small>`RequiredHeaders`</small> | <small>`[]`</small> | Rejects requests missing policy-required headers. |
| <small>`Request:Headers:ValidateHeaders`</small> | <small>`ValidateHeaders`</small> | <small>`{}`</small> | Enforces specific header key/value expectations. |
| <small>`Profiles:User:ConfigRequired`</small> | <small>`UserConfigRequired`</small> | <small>`false`</small> | Enables fail-closed behavior when profile source is unavailable. |
| <small>`Request:DisallowedHeaders`</small> | <small>`DisallowedHeaders`</small> | <small>`[]`</small> | Removes forbidden headers from incoming requests. |

</details>


---

# Endpoints

Define backend endpoints as sequential environment variables — `Host1`, `Host2`, … `HostN` — each as a semicolon-separated list of `field=value` pairs.

**Example:**
```
Host1=host=https://backend1.example.com;usemi=true
Host2=host=https://backend2.example.com;api-key=secret
```

Each host entry supports authentication (managed identity or API key), path rewriting, health-check paths, and custom processors. Expand for the full list of supported fields.

<details>
<summary><strong>Endpoint properties</strong></summary> 
Each host entry supports the following fields:

| Field | Description |
|---|---|
| <small>`host`</small> | Backend URL (required). |
| <small>`audience`</small> | OAuth audience claim for token requests. |
| <small>`api-key`</small> | API key value. Sets auth mode to `ApiKey`. |
| <small>`api-key-header`</small> | Header name used to send the API key to the backend. |
| <small>`ipaddress`</small> | Override IP address for the backend connection. |
| <small>`mode`</small> | Set to `direct` if the endpoints do not support probes. Set to `apim` if using an APIM. |
| <small>`path`</small> | Path prefix to append to forwarded requests. |
| <small>`probe`</small> | Health-check path for this endpoint. |
| <small>`processor`</small> | Custom processor class for this endpoint's responses. |
| <small>`stripprefix` / `strippathprefix`</small> | Set to `true` to strip the incoming path prefix before forwarding. |
| <small>`useoauth` / `usemi`</small> | Set to `true` to authenticate using managed identity (OAuth2). |
| <small>`useretryafter` / `retryafter`</small> | Set to `true` to honor `Retry-After` headers from this endpoint. |

> **Key Vault tip:** The `api-key` value can be set as a separate environment variable instead of inline in the host string. Use the naming pattern `HostN-api-key` (e.g., `Host1-api-key=<value>`) — this allows the value to be sourced from a Key Vault secret reference in Container Apps without embedding the key in the host string.


</details>


The proxy polls each backend to determine which endpoints are healthy before routing requests to them. Expand to configure poll frequency, load balancing strategy, retry behavior, and backend timeout.

<details>
<summary><strong>Health and routing</strong></summary>

| App Configuration Key | Env Var | Default | Why It Matters |
|---|---|---|---|
| <small>`Server:PollInterval`</small> | <small>`PollInterval`</small> | <small>`15000`</small> | How often (in ms) the proxy checks each backend endpoint's health. Default = every 15 seconds. |
| <small>`Server:PollTimeout`</small> | <small>`PollTimeout`</small> | <small>`3000`</small> | How long (in ms) the proxy waits for a health-check response before marking the endpoint unhealthy. Default = 3 seconds. |
| <small>`CircuitBreaker:SuccessRate`</small> | <small>`SuccessRate`</small> | <small>`80`</small> | Minimum percentage of successful responses required for an endpoint to receive traffic. Below 80%, the endpoint is skipped. |
| <small>`LoadBalancing:Mode`</small> | <small>`LoadBalanceMode`</small> | <small>`latency`</small> | Endpoint selection strategy. Options: `latency` (prefer fastest), `priority` (prefer lower-numbered hosts), `roundrobin`. |
| <small>`LoadBalancing:IterationMode`</small> | <small>`IterationMode`</small> | <small>`SinglePass`</small> | How endpoints are tried on retry. `SinglePass` tries each endpoint once per request. `MultiPass` allows repeated attempts up to `MaxAttempts`. |
| <small>`Request:DefaultTimeout`</small> | <small>`Timeout`</small> | <small>`1200000`</small> | How long (in ms) the proxy waits for a backend response before timing out. Default = 20 minutes. |
| <small>`LoadBalancing:MultiPass:MaxAttempts`</small> | <small>`MaxAttempts`</small> | <small>`10`</small> | Maximum total backend attempts per request when `IterationMode` is `MultiPass`. |

</details>

Under high concurrency, multiple requests selecting endpoints at the same time can all pick the same host and cause uneven load. Expand to configure the shared iterator state that distributes selection across concurrent requests.

<details>
<summary><strong>Concurrent endpoint selection</strong></summary>

| App Configuration Key | Env Var | Default | Why It Matters |
|---|---|---|---|
| <small>`Server:UseSharedIterators`</small> | <small>`UseSharedIterators`</small> | <small>`true`</small> | Allows concurrent requests to share endpoint selection state, preventing all requests from picking the same host at the same time. |
| <small>`Server:SharedIteratorTTLSeconds`</small> | <small>`SharedIteratorTTLSeconds`</small> | <small>`60`</small> | How long (in seconds) an idle shared iterator is kept before being discarded. |
| <small>`Server:SharedIteratorCleanupIntervalSeconds`</small> | <small>`SharedIteratorCleanupIntervalSeconds`</small> | <small>`30`</small> | How often (in seconds) the proxy removes expired shared iterators. |

</details>

---

# Logging

The proxy can send logs to Application Insights, an Event Hub, a log file, or a custom logger.

Each log sink requires its own connection credentials. Expand to connect Application Insights (connection string) or Event Hub (connection string or managed identity).

<details>
<summary><strong>Connect a log sink</strong></summary>

**Application Insights** — set `AppInsightsConnectionString` to enable.

| App Configuration Key | Env Var | Default | Why It Matters |
|---|---|---|---|
| <small>`Logging:AppInsightsConnectionString`</small> | <small>`AppInsightsConnectionString`</small> | <small>`""`</small> | Connection string for the Application Insights instance. Logging is enabled by setting this value. |

**Event Hub** — set `EventLoggers` to `eventhub` to activate the Event Hub sink, then provide connection details. Required: `EVENTHUB_NAME` and either `EVENTHUB_CONNECTIONSTRING` or `EVENTHUB_NAMESPACE` (managed identity).

| App Configuration Key | Env Var | Default | Why It Matters |
|---|---|---|---|
| <small>`Logging:EventHub:ConnectionString`</small> | <small>`EventHubConnectionString`</small> | <small>`""`</small> | Connection-string auth path for Event Hub sink. |
| <small>`Logging:EventHub:Name`</small> | <small>`EventHubName`</small> | <small>`""`</small> | Target Event Hub entity when `eventhub` is selected in `EventLoggers`. |
| <small>`Logging:EventHub:Namespace`</small> | <small>`EventHubNamespace`</small> | <small>`""`</small> | Managed-identity endpoint alternative to connection string. |
| <small>`Logging:EventHub:StartupSeconds`</small> | <small>`EventHubStartupSeconds`</small> | <small>`10`</small> | Startup timeout for Event Hub sender initialization. |
| <small>`Logging:EventHub:MaxReconnectAttempts`</small> | <small>`EventHubMaxReconnectAttempts`</small> | <small>`5`</small> | Reconnect cap for Event Hub sender failures. |
| <small>`Server:MaxUndrainedEvents`</small> | <small>`MaxUndrainedEvents`</small> | <small>`100`</small> | See the server section above. |
| <small>`Logging:ReuseEvents`</small> | <small>`ReuseEvents`</small> | <small>`false`</small> | Reduces allocation overhead in high-volume event paths. |

</details>

Log volume can be high under load. Use these settings to route specific event categories to specific sinks — for example, send only exceptions to the console while sending all categories to Application Insights.

<details>
<summary><strong>Sink selection and category filters</strong></summary>

**Log categories**

|   |   |   |   |   |   |
|---|---|---|---|---|---|
| <small>`async`</small> | <small>`auth`</small> | <small>`backend`</small> | <small>`circuitbreaker`</small> | <small>`console`</small> | <small>`custom`</small> |
| <small>`enqueued`</small> | <small>`exception`</small> | <small>`metric`</small> | <small>`poller`</small> | <small>`probe`</small> | <small>`profile`</small> |
| <small>`proxy`</small> | <small>`*` (all) | | | | |

| App Configuration Key | Env Var | Default | Why It Matters |
|---|---|---|---|
| <small>`Logging:EventLoggers`</small> | <small>`EventLoggers`</small> | <small>`file`</small> | Comma-delimited sink selector (`eventhub`, `file`, or custom class). See [OBSERVABILITY.md](OBSERVABILITY.md#custom-event-loggers). |
| <small>`Logging:LogToEvents`</small> | <small>`LogToEvents`</small> | <small>`async, backend, probe, circuitbreaker, custom, exception, profile, proxy, enqueued, auth`</small> | Categories sent through event logger sinks. |
| <small>`Logging:LogToAI`</small> | <small>`LogToAI`</small> | <small>`*`</small> | Categories sent to Application Insights. |
| <small>`Logging:LogToConsole`</small> | <small>`LogToConsole`</small> | <small>`*`</small> | Categories written to console output. |
| <small>`Logging:Level`</small> | <small>`LogLevel`</small> | <small>`Information`</small> | Minimum verbosity. Options: `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`. |
| <small>`Logging:LogDateTime`</small> | <small>`LogDateTime`</small> | <small>`false`</small> | Adds timestamps to console output. |
| <small>`Logging:LogFileName`</small> | <small>`LogFileName`</small> | <small>`eventslog.json`</small> | File output path when `file` is in `EventLoggers`. Mount a persistent volume at this path to retain logs beyond the container's lifetime — this is the recommended way to extract file logs from a containerized deployment. |
| <small>`Logging:LogToFile`</small> | <small>`LogToFile`</small> | <small>`false`</small> | Legacy fallback toggle when `EventLoggers` is not set. |

Example:
```
LogToEvents=enqueued, exception, poller, profile
LogToConsole=exception
LogToAI=*
```

</details>

## Request telemetry

For long term observability, it is necessary to capture relevant data from each request.  Use this section to customize the collection.

By default, headers are not captured in telemetry to avoid logging sensitive data. Expand to enable header capture for debugging or audit purposes, with options to exclude specific header values.

<details>
<summary><strong>Header capture</strong></summary>

| App Configuration Key | Env Var | Default | Why It Matters |
|---|---|---|---|
| <small>`Logging:LogHeaders`</small> | <small>`LogHeaders`</small> | <small>`[]`</small> | Captures only selected headers for focused diagnostics/audit needs. |
| <small>`Logging:LogAllRequestHeaders`</small> | <small>`LogAllRequestHeaders`</small> | <small>`false`</small> | Enables broad request-header capture during investigations. |
| <small>`Logging:LogAllRequestHeadersExcept`</small> | <small>`LogAllRequestHeadersExcept`</small> | <small>`Authorization`</small> | Protects sensitive request headers while broad capture is enabled. |
| <small>`Logging:LogAllResponseHeaders`</small> | <small>`LogAllResponseHeaders`</small> | <small>`false`</small> | Enables broad response-header capture during investigations. |
| <small>`Logging:LogAllResponseHeadersExcept`</small> | <small>`LogAllResponseHeadersExcept`</small> | <small>`Api-Key`</small> | Protects sensitive response headers while broad capture is enabled. |

</details>

Each backend attempt records host, status, duration, and error details. Expand to configure which backend response headers are preserved in event records for root-cause analysis.

<details>
<summary><strong>Backend attempt data</strong></summary>

| App Configuration Key | Env Var | Default | Why It Matters |
|---|---|---|---|
| <small>`Request:DependancyHeaders`</small> | <small>`DependancyHeaders`</small> | <small>`Backend-Host,Host-URL,Status,Duration,Error,Message,...`</small> | Preserves selected backend headers in event records for root-cause context. |
| <small>`Logging:EventData`</small> | <small>`EventHeaders`</small> | <small>`CommonEventHeaders`</small> | Sets event schema class used by emitted events. |

</details>

These fields are added to every telemetry event to identify which app, replica, and revision produced the log entry. They are injected by the ACA runtime at startup — do not set them manually.

<details>
<summary><strong>Runtime context</strong></summary>

| App Configuration Key | Env Var | Default | Why It Matters |
|---|---|---|---|
| <small>`Metadata:IDStr`</small> | <small>`IDStr`</small> | <small>`S7P`</small> | Makes generated request IDs identifiable by source. |
| <small>`Metadata:HostName`</small> | <small>`HostName`</small> | <small>`""`</small> | Preserves host-level context for troubleshooting.  |
| <small>`Metadata:ContainerApp`</small> | <small>`ContainerApp`</small> | <small>`ContainerAppName`</small> | Adds app identity to emitted logs and events. |
| <small>`Metadata:ReplicaName`</small> | <small>`ReplicaName`</small> | <small>`""`</small> | Adds replica context for scale-related analysis. |
| <small>`Metadata:Revision`</small> | <small>`Revision`</small> | <small>`revisionID`</small> | Adds deployment revision context to telemetry. |

</details>

---

# Async ( Work in progress )

When enabled, the proxy offloads long-running requests to a Blob Storage + Service Bus pipeline. Requires a storage account, a Service Bus queue, and the Request API service.

Async mode decouples the proxy from long-running backend calls — the caller receives a handle immediately and retrieves the result later. Expand to enable async mode and supply Blob Storage and Service Bus connection details.

<details>
<summary><strong>Enablement</strong></summary>

| App Configuration Key | Env Var | Default | Why It Matters |
|---|---|---|---|
| <small>`Async:Enabled`</small> | <small>`AsyncModeEnabled`</small> | <small>`false`</small> | Master switch for async mode. When `false`, all requests are processed synchronously. |
| <small>`Async:RequestAPIBaseUri`</small> | <small>`RequestAPIBaseUri`</small> | <small>`""`</small> | Base URL used when constructing async status callback URLs returned to callers. |
| <small>`Async:Storage:BlobConfig`</small> | <small>`AsyncBlobStorageConfig`</small> | <small>`""`</small> | Blob Storage connection config. Use `cs=<conn>` for connection-string auth, or `uri=https://account.blob.core.windows.net/,mi=true` for managed identity. Also accepts a raw Azure Storage connection string. |
| <small>`Async:Storage:ContainerName`</small> | <small>`StorageDbContainerName`</small> | <small>`Requests`</small> | Blob container name where async results are stored. |
| <small>`Async:ServiceBus:Config`</small> | <small>`AsyncSBConfig`</small> | <small>`""`</small> | Service Bus connection config. Use `cs=<conn>,q=<queue>` for connection-string auth, or `ns=<namespace>,q=<queue>,mi=true` for managed identity. Queue defaults to `requeststatus`. |

</details>

Expand to tune trigger timeouts, result retention, worker counts, and the header callers use to opt into async processing.
<details>
<summary><strong>Configuration</strong></summary>

| App Configuration Key | Env Var | Default | Why It Matters |
|---|---|---|---|
| <small>`Async:TriggerTimeout`</small> | <small>`AsyncTriggerTimeout`</small> | <small>`10000`</small> | How long (in ms) the proxy waits for a synchronous response before switching the request to async mode. Default = 10 seconds. |
| <small>`Async:Timeout`</small> | <small>`AsyncTimeout`</small> | <small>`1800000`</small> | Maximum time (in ms) the proxy waits for the async backend to complete. Default = 30 minutes. |
| <small>`Async:TTLSecs`</small> | <small>`AsyncTTLSecs`</small> | <small>`86400`</small> | How long (in seconds) async results are retained in storage for retrieval. Default = 24 hours. |
| <small>`Async:ClassNames`</small> | <small>`AsyncClassNames`</small> | <small>`""`</small> | Override the default storage and async handler classes with custom implementations. |
| <small>`Request:Headers:AsyncMode`</small> | <small>`AsyncClientRequestHeader`</small> | <small>`S7PAsyncMode`</small> | Header callers can send to explicitly request async processing for their request. |
| <small>`Async:ClientConfigFieldName`</small> | <small>`AsyncClientConfigFieldName`</small> | <small>`async-config`</small> | Field name in the client request body that carries per-request async configuration. |
| <small>`Async:Storage:Workers`</small> | <small>`AsyncBlobWorkerCount`</small> | <small>`2`</small> | Number of background workers writing async results to Blob Storage. |
| <small>`Async:Storage:MaxQueue`</small> | <small>`AsyncBlobMaxQueue`</small> | <small>`200`</small> | Maximum async results queued in memory waiting to be written. Requests are rejected when this limit is reached. |
| <small>`Async:Storage:StreamingBufferSizeBytes`</small> | <small>`AsyncStreamingBufferSizeBytes`</small> | <small>`0`</small> | Write buffer size for streaming blob uploads. `0` uses the SDK default. |

</details>



---

Expand for links to guides on App Configuration setup, local development, timeout behavior, and load balancing.

<details>
<summary><strong>Related Documentation</strong></summary>

- [AZURE_APP_CONFIGURATION.md](AZURE_APP_CONFIGURATION.md) - Hot-reload setup with App Configuration.
- [BEGINNER_DEVELOPMENT.md](BEGINNER_DEVELOPMENT.md) - Local development setup and minimum config.
- [TIMEOUTS.md](TIMEOUTS.md) - TTL, Timeout, and AsyncTimeout behavior.
- [LOAD_BALANCING.md](LOAD_BALANCING.md) - Load balancing and iteration behavior.

| Env Var | Property | Default | Description |
|---------|----------|---------|-------------|
| `AZURE_APPCONFIG_ENDPOINT` | `AppConfigEndpoint` | — | App Configuration endpoint (Managed Identity auth) |
| `AZURE_APPCONFIG_CONNECTION_STRING` | `AppConfigConnectionString` | — | App Configuration connection string (dev fallback) |
| `AZURE_APPCONFIG_LABEL` | `AppConfigLabel` | — | Label filter for settings |
| `AZURE_APPCONFIG_REFRESH_INTERVAL_SECONDS` | `AppConfigRefreshIntervalSeconds` | `30` s | Sentinel poll interval |

### Security

| Env Var | Property | Default | Description |
|---------|----------|---------|-------------|
| `UseOAuthGov` | `UseOAuthGov` | `false` | Use Azure Government OAuth endpoint |

### Async — parsed from `AsyncBlobStorageConfig`

| Property | Default | Description |
|----------|---------|-------------|
| `AsyncBlobStorageConnectionString` | `example-connection-string` | Parsed blob storage connection string |
| `AsyncBlobStorageUseMI` | `true` | Use Managed Identity for blob storage |
| `AsyncBlobStorageAccountUri` | `https://mystorageaccount.blob.core.windows.net` | Blob storage account URI |

### Async — parsed from `AsyncSBConfig`

| Property | Default | Description |
|----------|---------|-------------|
| `AsyncSBConnectionString` | `example-sb-connection-string` | Parsed Service Bus connection string |
| `AsyncSBQueue` | `requeststatus` | Service Bus queue name |
| `AsyncSBUseMI` | `false` | Use Managed Identity for Service Bus |
| `AsyncSBNamespace` | `example-namespace` | Service Bus namespace |

### Logging

| Env Var | Property | Default | Description |
|---------|----------|---------|-------------|
| `LOG_LEVEL` | `LogLevel` | `Information` | Minimum log level |
| `LOGTOFILE` | `LogToFile` | `false` | Write logs to file |

### Transport / Keep-Alive

| Env Var | Property | Default | Description |
|---------|----------|---------|-------------|
| `KeepAliveInitialDelaySecs` | `KeepAliveInitialDelaySecs` | `60` s | Delay before first keep-alive probe |
| `KeepAlivePingIntervalSecs` | `KeepAlivePingIntervalSecs` | `60` s | Interval between keep-alive pings |
| `KeepAliveIdleTimeoutSecs` | `KeepAliveIdleTimeoutSecs` | `1200` s | Idle connection timeout |
| `EnableMultipleHttp2Connections` | `EnableMultipleHttp2Connections` | `false` | Allow multiple HTTP/2 connections per host |
| `MultiConnLifetimeSecs` | `MultiConnLifetimeSecs` | `3600` s | Max lifetime of a pooled connection |
| `MultiConnIdleTimeoutSecs` | `MultiConnIdleTimeoutSecs` | `300` s | Idle timeout for pooled connections |
| `MultiConnMaxConns` | `MultiConnMaxConns` | `4000` | Max connections in the pool |

### Metadata (populated by Azure Container Apps runtime)

| Env Var | Property | Default | Description |
|---------|----------|---------|-------------|
| `CONTAINER_APP_NAME` | `ContainerApp` | `ContainerAppName` | Container App name injected by ACA |
| `Hostname` | `HostName` | `""` | Host name |
| `RequestIDPrefix` | `IDStr` | `S7P` | Prefix for generated request IDs |
| `CONTAINER_APP_REPLICA_NAME` | `ReplicaName` | `""` | Replica name injected by ACA |
| `CONTAINER_APP_REVISION` | `Revision` | `revisionID` | Revision name injected by ACA |

---

## Runtime-Derived Properties

These are never set via config — the proxy computes them at startup from other settings.

| Property | Description |
|----------|-------------|
| `HealthProbeSidecarEnabled` | Parsed from `HealthProbeSidecar` |
| `HealthProbeSidecarUrl` | Parsed from `HealthProbeSidecar` |
| `Hosts` | Populated from `Host1`…`HostN` environment variables |
| `PriorityWorkers` | Worker allocation map derived from `PriorityValues` |
| `TrackWorkers` | Internal worker tracking flag |
| `UseSharedIterators` | Whether to share iterator state across concurrent requests |

---

## Related Documentation

- [AZURE_APP_CONFIGURATION.md](AZURE_APP_CONFIGURATION.md) — Setting up hot-reload with App Configuration
- [BEGINNERDEVELOPMENT.md](BEGINNERDEVELOPMENT.md) — Local dev setup and minimal required config
- [TIMEOUTS.md](TIMEOUTS.md) — How TTL, Timeout, and AsyncTimeout interact
- [LOAD_BALANCING.md](LOAD_BALANCING.md) — LoadBalanceMode, IterationMode, and retry settings
- [BACKEND_HOSTS.md](BACKEND_HOSTS.md) — Per-host connection string keys including GCP Vertex AI (`usegcpauth`, `gcpproject`, etc.)
