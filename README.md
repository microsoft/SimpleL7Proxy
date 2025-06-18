# SimpleL7Proxy

SimpleL7Proxy is a lightweight yet powerful proxy service designed to direct network traffic between clients and servers at the application layer (Layer 7) of the OSI model. It manages HTTP(s) requests and can also function as a regional internal proxy, ensuring that traffic remains within a specified region. By continuously measuring and comparing backend latencies, SimpleL7Proxy always selects the fastest host to serve incoming requests. Additionally, higher priority tasks can interrupt and preempt lower priority ones.

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

| Variable                       | Description                                                                                                                                                                                        | Default                                  |
| ----------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **MaxQueueLength**             | Sets the maximum number of requests allowed in the queue.                                                                                                                        | 10                                       |
| **Port**                      | The port on which SimpleL7Proxy listens for incoming traffic.                                                                                                                                    | 80                                       |
| **SuspendedUserConfigUrl**      | URL or file path to fetch the list of suspended users.                                                                                                | file:config.json                         |
| **TERMINATION_GRACE_PERIOD_SECONDS** | The number of seconds SimpleL7Proxy waits before forcing itself to shut down.                                                                                                             | 30                                       |
| **UseProfiles**                | If true, enables user profile functionality for custom handling based on user profiles.                                                               | false                                    |
| **UserConfigUrl**             | URL or file path to fetch user configuration data.                                                                                                     | file:config.json                         |
| **UserPriorityThreshold**     | Floating point threshold value for user priority calculations (lower values make priority promotion more likely). Us a user owns more than this percentage of the requests , its priority is lowered.  This prevents greedy users from using all the resources.                                    | 0.1                                      |
| **ValidateAuthAppFieldName**    | Name of the field in the authentication payload to validate as the App ID.                                                                            | authAppID                                |
| **ValidateAuthAppID**           | If true, enables validation of an application ID in the request for authentication.  Entra has a limit of 13 application id's, use this setting to make the check in the proxy code.                                                                  | false                                    |
| **ValidateAuthAppIDHeader**     | Name of the header containing the App ID to validate.                                                                                                 | X-MS-CLIENT-PRINCIPAL-ID                 |
| **ValidateAuthAppIDUrl**        | URL or file path to fetch the list of valid App IDs for authentication.                                                                               | file:auth.json                           |
| **Workers**                   | The number of worker threads used to process incoming proxy requests.                                                                                                                            | 10                                       |

### Request Related Variables

| Variable                       | Description                                                                                                         | Default                                  |
| ----------------------------- | ------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **DefaultPriority**           | The default request priority when none other is specified.                                                                                                                                        | 2                                        |
| **DefaultTTLSecs**            | The default time-to-live for a request in seconds.                                                                                                                                               | 300                                      |
| **DisallowedHeaders**         | A comma-separated list of headers that should be removed or disallowed when forwarding requests.                                                                                                  | None                                     |
| **UserIDFieldName**          | The header name used to look up user information in configuration files.                                                                               | userId                                   |
| **PriorityKeyHeader**          | Name of the header that contains the priority key for determining request priority.                                                                     | S7PPriorityKey                           |
| **PriorityKeys**              | A comma-separated list of keys that correspond to the header 'S7PPriorityKey'.                                                                                                                   | 12345,234                                |
| **PriorityValues**            | A comma-separated list of priorities that map to the **PriorityKeys**.                                                                                                                           | 1,3                                      |
| **PriorityWorkers**           | A comma-separated list (e.g., “2:1,3:1”) specifying how many worker threads are assigned to each priority.  The first number in the tuple is the priority and the second is the number of dedicated workers for that priority level.                                                                                       | 2:1,3:1                                  |
| **RequiredHeaders**           | A comma-separated list of headers required for incoming requests to be deemed valid.                                                                                                             | None                                     |
| **TimeoutHeader**               | Name of the header used to specify per-request timeout (in ms).                                                                                       | S7PTimeout                               |
| **TTLHeader**                  | Name of the header used to specify time-to-live for requests.                                                                                         | S7PTTL                                   |
| **UniqueUserHeaders**         | A list of header names that uniquely identify the caller or user.                                                                                                                               | X-UserID                                 |
| **UserProfileHeader**         | Name of the header that contains user profile information when UseProfiles is enabled.                                                                 | X-UserProfile                            |
| **ValidateHeaders**           | Comma-separated list of key:value pairs for header validation.                                                     | (empty)                                  |

### Logging Related Variables

| Variable                       | Description                                                                                                         | Default                                  |
| ----------------------------- | ------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **APPINSIGHTS_CONNECTIONSTRING** | Specifies the connection string for Azure Application Insights. If set, the service sends logs to the configured Application Insights instance.                                                 | None                                     |
| **CONTAINER_APP_NAME**         | The name of the container application to be used in logs and telemetry. This is automatically defined by the ACA environment.                                                                           | ContainerAppName                         |
| **CONTAINER_APP_REPLICA_NAME**  | Name/ID of the current container app replica (used for logging and request IDs).   This is automatically defined by the ACA environment.                                                                           | ContainerAppName                         |                                                                   | 01                                       |
| **CONTAINER_APP_REVISION**      | Revision identifier for the current container app deployment.    This is automatically defined by the ACA environment.                                                                           | ContainerAppName                         |                                                                                     | revisionID                               |
| **EVENTHUB_CONNECTIONSTRING** | The connection string for EventHub logging. Must also set **EVENTHUB_NAME**.                                                                                                                      | None                                     |
| **EVENTHUB_NAME**             | The EventHub namespace for logging. Must also set **EVENTHUB_CONNECTIONSTRING**.                                                                                                                  | None                                     |
| **LogAllRequestHeaders**        | If true, logs all request headers for each proxied request.                                                                                           | false                                    |
| **LogAllRequestHeadersExcept**  | Comma-separated list of request headers to exclude from logging, even if LogAllRequestHeaders is true.                                                | Authorization                            |
| **LogAllResponseHeaders**       | If true, logs all response headers for each proxied request.                                                                                          | false                                    |
| **LogAllResponseHeadersExcept** | Comma-separated list of response headers to exclude from logging, even if LogAllResponseHeaders is true.                                               | Api-Key                                  |
| **LOGFILE**                     | If set, logs events to the specified file instead of EventHub (for debugging/testing only; not for production use).                                   | events.log (if enabled in code)          |
| **LogHeaders**                  | Comma-separated list of specific headers to log for debugging.                                                                                        | (empty)                                  |
| **LogProbes**                  | If true, logs details about health probe requests to backends.                                                                                        | false                                    |
| **RequestIDPrefix**           | The prefix appended to every request ID.                                                                                                                                                         | S7P                                      |

### Async Processing Variables

| Variable                       | Description                                                                                                         | Default                                  |
| ----------------------------- | ------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **AsyncModeEnabled**          | If true, enables asynchronous request processing.  This triggers the server to connect to blob storage and the service bus.                            | false                                    |
| **AsyncTimeout**              | Timeout in milliseconds for async operations. The maximum amount of time async request will run for.       | 1800000 (30 min)                        |
| **AsyncBlobAccessTimeoutSecs** | The number of seconds the blob will have access for. After this number of seconds, the blob will no longer be accessible ( but not deleted. ) | 3600 | 
| **AsyncBlobStorageConnectionString** | Connection string for Azure Blob Storage used in async mode.                                             | example-connection-string                |
| **AsyncClientBlobFieldname** | User profile field name that contain the clients blob container name.  Request responses will be created here.                                 | async-blobname                           |
| **AsyncClientAllowedFieldName**  | User profile field name that designates if the client is allowed to use async mode. Set to "true" in the user pforile to enable.                         | async-allowed                            |
| **AsyncSBConnectionString**   | Azure Service Bus connection string for async operations.                                                          | example-sb-connection-string             |
| **AsyncSBTopicFieldName**        | User profile field name for Service Bus topic that is specific to the client. The Userprofile should contain the topic that the client has access to.                                                                   | async-topic                              |

__NOTE__: Async is enabled at three levels:  The server, the profile and the request.  All three must be enabled to trigger an async request.  First, the service needs to have acess to  **AsyncBlobStorageConnectionString**, and **AsyncSBConnectionString**.  Second each client needs their own Service Topic & Subscription to receive events on.  It also needs to be designated as allowed to enter async mode.  Third, the request has to have a header that enables async for that request. 

1. The service needs to be able to create blobs in the storage blob as well as send messages into the service bus namespace.  
2. Each client profile needs to have three fields defined: **async-blobname**, **async-topic** and **async-allowed** set to true.  The field names can be customized.
3. Each request wanting to enable async mode will need to have **"AsyncEnabled"** set to **"true"**.

Incoming requests will trigger async operation if all three are enabled.  This allows async to be turned on and off for some clients.  And at the client level, they can enable async mode for specific requests.

### Connection Management Variables

| Variable                       | Description                                                                                                         | Default                                  |
| ----------------------------- | ------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **EnableMultipleHttp2Connections** | Enables multiple HTTP/2 connections per server.                                                               | false                                    |
| **IgnoreSSLCert**             | Toggles SSL certificate validation. If true, accepts self-signed certificates.                                     | false                                    |
| **KeepAliveIdleTimeoutSecs**  | The idle timeout (in seconds) for pooled HTTP connections before they are closed.                                  | 1200 (20 minutes)                        |
| **KeepAliveInitialDelaySecs** | Initial delay in seconds before sending TCP keep-alive probes.                                                    | 60                                       |
| **KeepAlivePingDelaySecs**    | The delay (in seconds) before sending a TCP keep-alive probe on an idle connection.                                | 30                                       |
| **KeepAlivePingIntervalSecs** | Interval in seconds between TCP keep-alive probes.                                                                | 60                                       |
| **KeepAlivePingTimeoutSecs**  | The timeout (in seconds) to wait for a response to a TCP keep-alive probe before closing the connection.           | 30                                       |
| **MultiConnIdleTimeoutSecs**  | Idle timeout in seconds for pooled HTTP/2 connections.                                                            | 300                                      |
| **MultiConnLifetimeSecs**     | Lifetime in seconds for pooled HTTP/2 connections.                                                                | 3600                                     |
| **MultiConnMaxConns**         | Maximum number of HTTP/2 connections per server.                                                                  | 4000                                     |

### Backend Configuration Variables

| Variable                       | Description                                                                                                         | Default                                  |
| ----------------------------- | ------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **AcceptableStatusCodes**     | The list of HTTP status codes considered successful. If a host returns a code not in this list, it's deemed a failure. | 200, 401, 403, 404, 408, 410, 412, 417, 400 |
| **APPENDHOSTSFILE / AppendHostsFile** | If true, appends host/IP pairs to /etc/hosts for DNS resolution. Both case variants are supported.      | false                                    |
| **CBErrorThreshold**          | The error threshold percentage for the circuit breaker. If the error rate surpasses this, the circuit breaks.     | 50                                       |
| **CBTimeslice**               | The duration (in seconds) of the sampling window for the circuit breaker's error rate.                             | 60                                       |
| **DnsRefreshTimeout**         | The number of milliseconds to force a DNS refresh, useful for making services fail over more quickly.             | 120000                                   |
| **Host1, Host2, ...**         | Up to 9 backend servers can be specified. Each Host should be in the form "http(s)://fqdnhostname" for proper DNS resolution. | None                                     |
| **HostName**                  | A logical name for the backend host used for identification and logging.                                          | Default                                  |
| **IP1, IP2, ...**             | IP addresses that map to corresponding Host entries if DNS is unavailable. Must define Host, IP, and APPENDHOSTSFILE. | None                                     |
| **OAuthAudience**             | The audience used for OAuth token requests, if **UseOAuth** is enabled.                                                                                                           | None                                     |
| **PollInterval**              | The interval (in milliseconds) at which SimpleL7Proxy polls the backend servers.                                  | 15000                                    |
| **PollTimeout**               | The timeout (in milliseconds) for each server poll request.                                                       | 3000                                     |
| **Probe_path1, Probe_path2, ...** | Path(s) to health check endpoints for each backend host (e.g., /health, /readiness).                         | echo/resource?param1=sample              |
| **SuccessRate**               | The minimum success rate (percentage) a backend must maintain to stay active.                                    | 80                                       |
| **Timeout**                   | Connection timeout (in milliseconds) for each backend request. If exceeded, SimpleL7Proxy tries the next available host. | 3000                                     |
| **UseOAuth**                  | Enables or disables OAuth token fetching for outgoing  requests.                                                                                                                                 | false                                    |
| **UseOAuthGov**                 | If true, uses the government cloud OAuth endpoint for token acquisition.                                                                              | false                                    |

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
