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
4. **Configuration via Environment Variables**: Most operational aspects—like port, host addresses, special headers, queue lengths—are managed through environment variables, making deployments flexible and easy.  
5. **Observability and Logging**: SimpleL7Proxy can log details to Azure Application Insights or EventHub, capturing key metrics around request success, timeouts, and failures for diagnostics.

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

| Variable                       | Description                                                                                                                                                                                        | Default                                  |
| ----------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **AcceptableStatusCodes**     | The list of HTTP status codes considered successful. If a host returns a code not in this list, it's deemed a failure.                                                                            | 200, 401, 403, 404, 408, 410, 412, 417, 400 |
| **APPENDHOSTSFILE**           | When running in a container and DNS does not resolve, this option can append entries to the hosts file using pairs like Host1 and IP1.                                                              | None                                     |
| **APPINSIGHTS_CONNECTIONSTRING** | Specifies the connection string for Azure Application Insights. If set, the service sends logs to the configured Application Insights instance.                                                 | None                                     |
| **CBErrorThreshold**          | The error threshold percentage for the circuit breaker. If the error rate surpasses this, the circuit breaks.                                                                                     | 50                                       |
| **CBTimeslice**               | The duration (in seconds) of the sampling window for the circuit breaker’s error rate.                                                                                                             | 60                                       |
| **DefaultPriority**           | The default request priority when none other is specified.                                                                                                                                        | 2                                        |
| **DefaultTTLSecs**            | The default time-to-live for a request in seconds.                                                                                                                                               | 300                                      |
| **DisallowedHeaders**         | A comma-separated list of headers that should be removed or disallowed when forwarding requests.                                                                                                  | None                                     |
| **DnsRefreshTimeout**         | The number of milliseconds to force a DNS refresh, useful for making services fail over more quickly.                                                                                             | 120000                                   |
| **EVENTHUB_CONNECTIONSTRING** | The connection string for EventHub logging. Must also set **EVENTHUB_NAME**.                                                                                                                      | None                                     |
| **EVENTHUB_NAME**             | The EventHub namespace for logging. Must also set **EVENTHUB_CONNECTIONSTRING**.                                                                                                                  | None                                     |
| **Host1, Host2, ...**         | Up to 9 backend servers can be specified. Each Host should be in the form “http(s)://fqdnhostname” for proper DNS resolution.                                                                     | None                                     |
| **HostName**                  | A logical name for the backend host used for identification and logging.                                                                                                                          | Default                                  |
| **IgnoreSSLCert**             | Toggles SSL certificate validation. If true, accepts self-signed certificates.                                                                                                                    | false                                    |
| **IP1, IP2, ...**             | IP addresses that map to corresponding Host entries if DNS is unavailable. Must define Host, IP, and APPENDHOSTSFILE.                                                                             | None                                     |
| **LogHeaders**                | A comma-separated list of headers to log for debugging.                                                                                                                                          | None                                     |
| **LogProbes**                 | When true, adds additional logging for backend health checks.                                                                                                                                    | false                                    |
| **MaxQueueLength**            | Sets the maximum number of requests allowed in the queue.                                                                                                                                       | 10                                       |
| **OAuthAudience**             | The audience used for OAuth token requests, if **UseOAuth** is enabled.                                                                                                                           | None                                     |
| **PollInterval**              | The interval (in milliseconds) at which SimpleL7Proxy polls the backend servers.                                                                                                                  | 15000                                    |
| **PollTimeout**               | The timeout (in milliseconds) for each server poll request.                                                                                                                                     | 3000                                     |
| **Port**                      | The port on which SimpleL7Proxy listens for incoming traffic.                                                                                                                                    | 80                                       |
| **PriorityKeys**              | A comma-separated list of keys that correspond to the header 'S7PPriorityKey'.                                                                                                                   | 12345,234                                |
| **PriorityValues**            | A comma-separated list of priorities that map to the **PriorityKeys**.                                                                                                                           | 1,3                                      |
| **PriorityWorkers**           | A comma-separated list (e.g., “2:1,3:1”) specifying how many worker threads are assigned to each priority.                                                                                        | 2:1,3:1                                  |
| **Probe_path1, Probe_path2, ...** | Paths to health check endpoints for each defined host. When a host is created, SimpleL7Proxy verifies its health using this path.                                                            | echo/resource?param1=sample             |
| **RequestIDPrefix**           | The prefix appended to every request ID.                                                                                                                                                         | S7P                                      |
| **RequiredHeaders**           | A comma-separated list of headers required for incoming requests to be deemed valid.                                                                                                             | None                                     |
| **SuccessRate**               | The minimum success rate (percentage) a backend must maintain to stay active.                                                                                                                    | 80                                       |
| **TERMINATION_GRACE_PERIOD_SECONDS** | The number of seconds SimpleL7Proxy waits before forcing itself to shut down.                                                                                                             | 30                                       |
| **Timeout**                   | Connection timeout (in milliseconds) for each backend request. If exceeded, SimpleL7Proxy tries the next available host.                                                                         | 3000                                     |
| **UniqueUserHeaders**         | A list of header names that uniquely identify the caller or user.                                                                                                                               | X-UserID                                 |
| **UseOAuth**                  | Enables or disables OAuth token fetching for incoming requests.                                                                                                                                 | false                                    |
| **UseProfiles**               | Enables or disables additional user profile processing (for example, reading from **UserConfigUrl**).                                                                                           | false                                    |
| **UserConfigUrl**             | Points to a user configuration file (or URL) from which additional profile data can be loaded (if **UseProfiles** is true).                                                                     | file:config.json                         |
| **UserPriorityThreshold**     | The threshold at which a user’s priority is elevated.                                                                                                                                           | 0.1f                                     |
| **ValidateHeaders**           | A comma-separated list of key:value pairs for validating headers in incoming requests.                                                                                                           | None                                     |
| **Workers**                   | The number of worker threads used to process incoming proxy requests.                                                                                                                            | 10                                       |

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
Port=8000
Host1=https://localhost:3000
Host2=http://localhost:5000
PollInterval=1500
Timeout=3000
```

This will create a listener on port 8000 and will check the health of the two hosts (https://localhost:3000 and http://localhost:5000) every 1.5 seconds.  Any incoming requests will be proxied to the server with the lowest latency ( as measured every PollInterval). The first request will timeout after 3 seconds. If the second request also timesout, the service will return error code 503.

### Running it on the command line

**Pre-requisutes:**
- Cloned repo
- Dotnet SDK 9.0

**Run it**
```
export Port=8000
export Host1=https://localhost:3000
export Host2=http://localhost:5000
export Timeout=2000

dotnet run
```

## Running it as a container

**Pre-requisites:**
- Cloned repo
- Docker

**Run it**
```
docker build -t proxy -f Dockerfile .
docker run -p 8000:443 -e "Host1=https://localhost:3000" -e "Host2=http://localhost:5000" -e "Timeout=2000" proxy
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
