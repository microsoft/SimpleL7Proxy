# SimpleL7Proxy

SimpleL7Proxy is a lightweight yet powerful proxy service designed to direct network traffic between clients and servers at the application layer. It manages HTTP(s) requests and can also function as a regional internal proxy, ensuring that traffic gets sent to the best region. By continuously measuring and comparing backend latencies, SimpleL7Proxy always selects the fastest host to serve incoming requests. The service also implements a priority queue that lets higher priority tasks interrupt and preempt lower priority ones.

## Introduction

Use SimpleL7Proxy as an easy-to-set-up solution that intelligently balances or distributes traffic among multiple backend services. Since it operates at Layer 7, it can examine each request’s content or metadata to route it effectively, reroute failures, and automatically track host performance. This makes SimpleL7Proxy suitable for scenarios like:

1. Routing requests to hosts in different regions or datacenters.  
2. Handling failover quickly by selecting a responsive backend.  
3. Prioritizing business-critical requests while still serving standard requests efficiently.

## How It Works

1. **Latency Monitoring**: The proxy periodically checks each backend server, measuring response time to dynamically maintain a ranking of healthiest or fastest hosts.  
2. **Priority-based Request Handling**: SimpleL7Proxy supports multiple priority classes. If a request arrives with a higher priority, it can supersede other queued requests.  
3. **Expirable Requests**: The configurable TTL (time-to-live) ensures that requests that are too old receive an appropriate status code, preventing stale operations.  
4. **Async Processing**: Send requests and get notified over a servicebus subscription in real-tiem once the processing has completed.
5. **Configuration via Environment Variables**: Most operational aspects—like port, host addresses, special headers, queue lengths—are managed through environment variables, making deployments flexible and easy.  
6. **Observability and Logging**: SimpleL7Proxy can log details to Azure Application Insights or EventHub, capturing key metrics around request success, timeouts, and failures for diagnostics.

## Usage Scenarios

- **Data Center Failover**: In an environment where multiple datacenters exist, SimpleL7Proxy can quickly fail over to a healthy host if one datacenter experiences service interruptions.
- **Internal Regional Proxy**: Companies with multiple internal services across various geographic regions can route traffic locally using this proxy, ensuring low latency for internal applications.
- **Priority Routing**: Financial or mission-critical requests can be given a higher priority to reduce processing delays in busy environments.
- **Testing & Development**: Quickly spin up local or containerized instances to experiment with backend changes or new features, adjusting environment variables for test configurations.

---

In the diagram below, a client connected to the proxy which has 3 backend hosts. The proxy identified the host with the lowest latency (Host 2) to make the request to.

![image](https://github.com/nagendramishr/SimpleL7Proxy/assets/81572024/d2b09ebb-1bce-41a7-879a-ac90aa5ae227)

## Features
- HTTP/HTTPS traffic proxy
- Failover
- Load balance based on latency
- SSL termination
- Priority based processing
- Cross-platform compatibility (Windows, Linux, macOS)
- Logging to Application Insights
- Logging to EventHub
- Async mode for processing long running requests


## High Availability Configuration

The service continously monitors the backendpoints and routes traffic to the best endpoint.  To acheive high availability, configure multiple hosts, increase the threshold for selection:

```bash
# Backend configuration for high availability
Host1=https://primary.example.com
Host2=https://secondary.example.com
Host3=https://tertiary.example.com
PollInterval=5000
CBErrorThreshold=20
SuccessRate=95
```

## Secufity focused Configuration

The service can be configured to only accept requests from allowed senders. Configure your container app to accept OAuth and then let the proxy validate the application ID for the incoming connection:

```bash
# Security-focused configuration
DisallowedHeaders=X-Forwarded-For,X-Real-IP
RequiredHeaders=Authorization,X-API-Key
ValidateAuthAppID=true
ValidateAuthAppIDUrl=file:auth.json
LogAllRequestHeaders=false
LogAllRequestHeadersExcept=Authorization,X-API-Key,Cookie
````
## Async Mode Configuration

Async processing operates at three levels:

1. **Service Level**: Configure the proxy service with:
   - `AsyncModeEnabled=true`
   - `AsyncBlobStorageConnectionString=[connection-string]`
   - `AsyncSBConnectionString=[connection-string]`

2. **Client Level**: In the user profile configuration, each client needs:
   - `async-blobname=[container-name]` - Container name for client blob storage
   - `async-topic=[topic-name]` - Service Bus topic name for client
   - `async-allowed=true` - Permission flag for async processing

3. **Request Level**: Individual requests need:
   - `AsyncEnabled=true` header to trigger async processing

When all three levels are enabled, the request will be processed asynchronously:
1. Client receives a 202 Accepted response with blob URIs
2. Proxy processes the request asynchronously
3. Results are stored in the specified blob container
4. Notification is sent to the client's Service Bus topic



## Environment Variables

SimpleL7Proxy is designed to be highly configurable through environment variables, which are organized into functional categories. This approach allows for easy deployment and configuration without code changes.

### Environment Variable Categories

- **Core Configuration**: Essential settings for basic proxy operation (port, workers, queue length)
- **Request Processing**: Controls how incoming requests are handled, prioritized, and validated
- **Logging & Monitoring**: Options for logging to Application Insights, EventHub, or files
- **Backend Configuration**: Settings for backend servers, health checks, and failover behavior
- **Connection Management**: Controls for HTTP connections, keep-alive settings, and SSL
- **Async Processing**: Configuration for asynchronous request handling with Azure storage

### Quick Start Configuration

To get started with minimal configuration, you need to set:
1. **Port**: The port on which the proxy listens
2. **Host1, Host2, ...**: At least one backend host to proxy requests to
3. **Probe_path1, Probe_Path2, ...**: Corrisponding probe url's

For production deployments, consider also configuring:
- **Workers**: Number of worker threads (increase for higher throughput)
- **MaxQueueLength**: Maximum queue size based on expected traffic
- **LogAllRequestHeaders**: Set to `true` for debugging
- **APPINSIGHTS_CONNECTIONSTRING**: For monitoring in Azure

### Core Configuration Variables

| Variable                       | Type | Description                                                                                                                                                                                        | Default                                  |
| ----------------------------- | ---- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **MaxQueueLength**             | int | Sets the maximum number of requests allowed in the queue.                                                                                                                        | 10                                       |
| **Port**                      | int | The port on which SimpleL7Proxy listens for incoming traffic.                                                                                                                                    | 80                                       |
| **SuspendedUserConfigUrl**      | string | URL or file path to fetch the list of suspended users.                                                                                                | file:config.json                         |
| **TERMINATION_GRACE_PERIOD_SECONDS** | int | The number of seconds SimpleL7Proxy waits before forcing itself to shut down.                                                                                                             | 30                                       |
| **UseProfiles**                | bool | If true, enables user profile functionality for custom handling based on user profiles.                                                               | false                                    |
| **UserConfigUrl**             | string | URL or file path to fetch user configuration data.                                                                                                     | file:config.json                         |
| **UserPriorityThreshold**     | float | Floating point threshold value for user priority calculations (lower values make priority promotion more likely). Us a user owns more than this percentage of the requests , its priority is lowered.  This prevents greedy users from using all the resources.                                    | 0.1                                      |
| **ValidateAuthAppFieldName**    | string | Name of the field in the authentication payload to validate as the App ID.                                                                            | authAppID                                |
| **ValidateAuthAppID**           | bool | If true, enables validation of an application ID in the request for authentication.  Entra has a limit of 13 application id's, use this setting to make the check in the proxy code.                                                                  | false                                    |
| **ValidateAuthAppIDHeader**     | string | Name of the header containing the App ID to validate.                                                                                                 | X-MS-CLIENT-PRINCIPAL-ID                 |
| **ValidateAuthAppIDUrl**        | string | URL or file path to fetch the list of valid App IDs for authentication.                                                                               | file:auth.json                           |
| **Workers**                   | int | The number of worker threads used to process incoming proxy requests.                                                                                                                            | 10                                       |

### Request Related Variables

| Variable                       | Type | Description                                                                                                         | Default                                  |
| ----------------------------- | ---- | ------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **DefaultPriority**           | int | The default request priority when none other is specified.                                                                                                                                        | 2                                        |
| **DefaultTTLSecs**            | int | The default time-to-live for a request in seconds.                                                                                                                                               | 300                                      |
| **DisallowedHeaders**         | string | A comma-separated list of headers that should be removed or disallowed when forwarding requests.                                                                                                  | None                                     |
| **UserIDFieldName**          | string | The header name used to look up user information in configuration files.                                                                               | userId                                   |
| **PriorityKeyHeader**          | string | Name of the header that contains the priority key for determining request priority.                                                                     | S7PPriorityKey                           |
| **PriorityKeys**              | int array | A comma-separated list of keys that correspond to the header 'S7PPriorityKey'.                                                                                                                   | "12345,234"                                |
| **PriorityValues**            | int array | A comma-separated list of priorities that map to the **PriorityKeys**.                                                                                                                           | "1,3"                                      |
| **PriorityWorkers**           | string | A comma-separated list (e.g., "2:1,3:1") specifying how many worker threads are assigned to each priority.  The first number in the tuple is the priority and the second is the number of dedicated workers for that priority level.                                                                                       | 2:1,3:1                                  |
| **RequiredHeaders**           | string | A comma-separated list of headers required for incoming requests to be deemed valid.                                                                                                             | None                                     |
| **TimeoutHeader**               | string | Name of the header used to specify per-request timeout (in ms).                                                                                       | S7PTimeout                               |
| **TTLHeader**                  | string | Name of the header used to specify time-to-live for requests.                                                                                         | S7PTTL                                   |
| **UniqueUserHeaders**         | string | A list of header names that uniquely identify the caller or user.                                                                                                                               | X-UserID                                 |
| **UserProfileHeader**         | string | Name of the header that contains user profile information when UseProfiles is enabled.                                                                 | X-UserProfile                            |
| **ValidateHeaders**           | string | Comma-separated list of key:value pairs for header validation.                                                     | (empty)                                  |

### Logging Related Variables

| Variable                       | Type | Description                                                                                                         | Default                                  |
| ----------------------------- | ---- | ------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **APPINSIGHTS_CONNECTIONSTRING** | string | Specifies the connection string for Azure Application Insights. If set, the service sends logs to the configured Application Insights instance.                                                 | None                                     |
| **CONTAINER_APP_NAME**         | string | The name of the container application to be used in logs and telemetry. This is automatically defined by the ACA environment.                                                                           | ContainerAppName                         |
| **CONTAINER_APP_REPLICA_NAME**  | string | Name/ID of the current container app replica (used for logging and request IDs). This is automatically defined by the ACA environment.                                                                           | ContainerAppName                         |
| **CONTAINER_APP_REVISION**      | string | Revision identifier for the current container app deployment. This is automatically defined by the ACA environment.                                                                           | ContainerAppName                         |                                                                                     | revisionID                               |
| **EVENTHUB_CONNECTIONSTRING** | string | The connection string for EventHub logging. Must also set **EVENTHUB_NAME**.                                                                                                                      | None                                     |
| **EVENTHUB_NAME**             | string | The EventHub namespace for logging. Must also set **EVENTHUB_CONNECTIONSTRING**.                                                                                                                  | None                                     |
| **LogAllRequestHeaders**        | bool | If true, logs all request headers for each proxied request.                                                                                           | false                                    |
| **LogAllRequestHeadersExcept**  | string | Comma-separated list of request headers to exclude from logging, even if LogAllRequestHeaders is true.                                                | Authorization                            |
| **LogAllResponseHeaders**       | bool | If true, logs all response headers for each proxied request.                                                                                          | false                                    |
| **LogAllResponseHeadersExcept** | string | Comma-separated list of response headers to exclude from logging, even if LogAllResponseHeaders is true.                                               | Api-Key                                  |
| **LOGFILE**                     | string | If set, logs events to the specified file instead of EventHub (for debugging/testing only; not for production use).                                   | events.log (if enabled in code)          |
| **LogHeaders**                  | string | Comma-separated list of specific headers to log for debugging.                                                                                        | (empty)                                  |
| **LogProbes**                  | bool | If true, logs details about health probe requests to backends.                                                                                        | false                                    |
| **RequestIDPrefix**           | string | The prefix appended to every request ID.                                                                                                                                                         | S7P                                      |

### Async Processing Variables

| Variable                       | Type | Description                                                                                                         | Default                                  |
| ----------------------------- | ---- | ------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **AsyncModeEnabled**          | bool | If true, enables asynchronous request processing.  This triggers the server to connect to blob storage and the service bus.                            | false                                    |
| **AsyncTimeout**              | int | Timeout in milliseconds for async operations. The maximum amount of time async request will run for.       | 1800000 (30 min)                        |
| **AsyncClientBlobTimeoutFieldName** | string | User profile field name that contains number of seconds the blob will have access for. After this number of seconds, the blob will no longer be accessible ( but not deleted. )  | async-blobaccess-timeout | 
| **AsyncBlobStorageConnectionString** | string | Connection string for Azure Blob Storage used in async mode.                                             | example-connection-string                |
| **AsyncClientBlobFieldname** | string | User profile field name that contain the clients blob container name.  Request responses will be created here.                                 | async-blobname                           |
| **AsyncClientAllowedFieldName**  | string | User profile field name that designates if the client is allowed to use async mode. Set to "true" in the user pforile to enable.                         | async-allowed                            |
| **AsyncSBConnectionString**   | string | Azure Service Bus connection string for async operations.                                                          | example-sb-connection-string             |
| **AsyncSBTopicFieldName**        | string | User profile field name for Service Bus topic that is specific to the client. The Userprofile should contain the topic that the client has access to.                                                                   | async-topic                              |


### Connection Management Variables

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

### Backend Configuration Variables

| Variable                       | Type | Description                                                                                                         | Default                                  |
| ----------------------------- | ---- | ------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **AcceptableStatusCodes**     | int array | The list of HTTP status codes considered successful. If a host returns a code not in this list, it's deemed a failure. | 200, 401, 403, 404, 408, 410, 412, 417, 400 |
| **APPENDHOSTSFILE / AppendHostsFile** | bool | If true, appends host/IP pairs to /etc/hosts for DNS resolution. Both case variants are supported.      | false                                    |
| **CBErrorThreshold**          | int | The error threshold percentage for the circuit breaker. If the error rate surpasses this, the circuit breaks.     | 50                                       |
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
| **LogConsoleEvent**           | bool | If true, logs events to the console output.                                                                      | true                                     |

### Additional Configuration Notes

- **Timeout Settings**: The default timeout for backend requests is 20 minutes (1,200,000 ms), not 3 seconds as shown in some examples. Adjust this value based on your expected request duration.

- **Array Parameters**: Parameters like `AcceptableStatusCodes`, `PriorityKeys`, and `PriorityValues` accept comma-separated values that are parsed into arrays in the code.

- **Circuit Breaker Configuration**: The circuit breaker is controlled by both `CBErrorThreshold` and `CBTimeslice`. The error rate is calculated over the timeslice period.

- **Environment Variables vs Configuration File**: While most settings can be provided via environment variables, you can also use appsettings.json in development mode.

- **Priority Configuration**: When setting up priorities, ensure the number of values in `PriorityKeys` and `PriorityValues` match, and that `PriorityWorkers` references valid priority levels.

- **DNS Refresh**: If you're experiencing issues with DNS resolution in dynamic environments, adjust the `DnsRefreshTimeout` value to force more frequent DNS lookups.

### User Profile contents ###

This is a json formatted file that gets read every hour.  It can be fetched from a URL or a file location, depending on the configuration.  Here is an example file:

```json:
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
---

### Proxy Response Codes

| Code | Description                                                                                  |
|----- |----------------------------------------------------------------------------------------------|
| 200  | Success                                                                                      |
| 400  | Bad Request (Issue with the HTTP request format)                                             |
| 408  | Request Timed Out (The request to the backend host timed out)                                |
| 410  | Request Expired (S7PTTL indicates the request is too old to process)                         |
| 500  | Internal Server Error (Check Application Insights for more details)                          |
| 502  | Bad Gateway (Could not complete the request, possibly from overloaded backend hosts)         |

### Headers Used

| Header                 | Description                                                                                                       |
|------------------------|-------------------------------------------------------------------------------------------------------------------|
| **S7PDEBUG**           | Set to `true` to enable tracing at the request level.                                                             |
| **S7PPriorityKey**     | If this header matches a defined key in *PriorityKeys*, the request uses the associated priority from *PriorityValues*. |
| **S7PREQUEUE**         | If a remote host returns `429` and sets this header to `true`, the request will be requeued using its original enqueue time. |
| **S7PTTL**             | Time-to-live for a message. Once expired, the proxy returns `410` for that request.                                |
| **x-Request-Queue-Duration**  | Shows how long the message spent in the queue before being processed.                                       |
| **x-Request-Process-Duration**| Indicates the processing duration of the request.                                                           |
| **x-Request-Worker**          | Identifies which worker ID handled the request.                                                             |


### Example:
```
# Core configuration
Port=8000
Workers=10
MaxQueueLength=20

# Backend configuration
Host1=https://localhost:3000
Host2=http://localhost:5000
PollInterval=1500
Timeout=3000

# Logging configuration
LogAllRequestHeaders=true
LOGFILE=events.log
```

This will create a listener on port 8000 with 10 worker threads and a queue that can hold up to 20 requests. It will check the health of the two hosts (https://localhost:3000 and http://localhost:5000) every 1.5 seconds. Any incoming requests will be proxied to the server with the lowest latency (as measured every PollInterval). Requests will timeout after 3 seconds, and all request headers will be logged to the events.log file.

### Running it on the command line

**Pre-requisutes:**
- Cloned repo
- Dotnet SDK 9.0

**Run it**
```
# Set essential environment variables
export Port=8000
export Host1=https://localhost:3000
export Host2=http://localhost:5000
export Timeout=2000
export LogAllRequestHeaders=true

# Run the proxy
dotnet run
```

## Running it as a container

**Pre-requisites:**
- Cloned repo
- Docker

**Run it**
```
# Build the Docker image
docker build -t proxy -f Dockerfile .

# Run the container with environment variables
docker run -p 8000:443 \
  -e "Host1=https://localhost:3000" \
  -e "Host2=http://localhost:5000" \
  -e "Timeout=2000" \
  -e "LogAllRequestHeaders=true" \
  -e "Workers=15" \
  proxy
```

## Deploy the container to Azure Container Apps

**Pre-requisites:**
- Cloned repo
- Docker
- az cli ( logged into azure account )

**Deploy it**
```
# Fill in the name of your Azure Container Registry (ACR) here, without the azurecr.io part:
export ACR=<ACR>
export GROUP=simplel7proxyg
export ACENV=simplel7proxyenv
export ACANAME=simplel7proxy

az acr login --name $ACR.azurecr.io
docker build -t $ACR.azurecr.io/myproxy:v1 -f Dockerfile .
docker push $ACR.azurecr.io/myproxy:v1

ACR_CREDENTIALS=$(az acr credential show --name $ACR)
export ACR_USERNAME=$(echo $ACR_CREDENTIALS | jq -r '.username')
export ACR_PASSWORD=$(echo $ACR_CREDENTIALS | jq -r '.passwords[0].value')

az group create --name $GROUP --location eastus
az containerapp env create --name $ACENV --resource-group $GROUP --location eastus
az containerapp create --name $ACANAME \
  --resource-group $GROUP \
  --environment $ACENV \
  --image $ACR.azurecr.io/myproxy:v1 \
  --target-port 443 \
  --ingress external \
  --registry-server $ACR.azurecr.io \
  --query properties.configuration.ingress.fqdn \
  --registry-username $ACR_USERNAME \
  --registry-password $ACR_PASSWORD \
  --env-vars Host1=https://localhost:3000 Host2=http://localhost:5000

```
## Deploy to Container Apps via a Github Action

You can create a github workflow to deploy this code to an Azure container app.  You can follow the step by step instruction from a similar project in the following video:

[![Video Title](https://i.ytimg.com/vi/-KojzBMM2ic/hqdefault.jpg)](https://www.youtube.com/watch?v=-KojzBMM2ic "How to Create a Github Action to Deploy to Azure Container Apps")
