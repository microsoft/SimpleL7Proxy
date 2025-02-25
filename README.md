# SimpleL7Proxy

The SimpleL7Proxy is a lightweight, performance based proxy designed to route network traffic between clients and servers. It operates at the application layer (Layer 7) of the OSI model, enabling it to route HTTP(s) requests. It can be used as a regional internal proxy to manage traffic across specific regions. The server calculates the latency for each backend on a regular interval so that when a request comes in, it will connect to the lowest latency host first. Optionally, higher priority transactions can preempt lower priority requests.



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

## Usage
SimpleL7Proxy can be run standalone on the commandline or it can be deployed as a container.  All configuration is passed to it via environment variables.  When running, the server will periodically display the current back-end latencies.

### Environment Variables:

| Variable | Description | Default |
| -------- | ----------- | ------- |
| **AcceptableStatusCodes** | 	The list of HTTP status codes considered successful. If a host returns a code not in this list, it's deemed a failure.	 | 200, 401, 403, 404, 408, 410, 412, 417, 400 |
| |
| **APPENDHOSTSFILE** | 	When running in a container and DNS does not resolve, this option can append entries to the hosts file, using pairs like Host1 and IP1.	 | None |
| |
| **APPINSIGHTS_CONNECTIONSTRING** | 	Specifies the connection string for Azure Application Insights. If set, the service sends logs to the configured Application Insights instance.	 | None |
| |
| **CBErrorThreshold** | 	The error threshold percentage for the circuit breaker. If the error rate surpasses this, the circuit breaks.	 | 50 |
| |
| **CBTimeslice** | 	The duration (in seconds) of the sampling window for the circuit breaker’s error rate.	 | 60 |
| |
| **DefaultPriority** | 	The default request priority when none other is specified.	 | 2 |
| |
| **DefaultTTLSecs** | 	The default time-to-live for a request in seconds.	 | 300 |
| |
| **DisallowedHeaders** | 	A comma-separated list of headers that should be removed or disallowed when forwarding requests.	 | None |
| |
| **DnsRefreshTimeout** | 	The number of milliseconds to force a DNS refresh, useful for making services fail over more quickly.	 | 120000 |
| |
| **EVENTHUB_CONNECTIONSTRING** | 	The connection string for EventHub logging. Must also set EVENTHUB_NAME.	 | None |
| |
| **EVENTHUB_NAME** | 	The EventHub namespace for logging. Must also set EVENTHUB_CONNECTIONSTRING.	 | None |
| |
| **Host1, Host2, ...** | 	Up to 9 backend servers can be specified. Each Host should be in the form “http(s)://fqdnhostname” for proper DNS resolution.	 | None |
| |
| **HostName** | 	The logical name part of the backend host, used for logging and identification.	 | Default |
| |
| **IgnoreSSLCert** | 	Toggles SSL certificate validation. If true, accepts self-signed certificates.	 | false |
| |
| **IP1, IP2, ...** | 	IP addresses corresponding to the Host entries if DNS is unavailable. Must define Host, IP, and APPENDHOSTSFILE.	 | None |
| |
| **LogHeaders** | 	A comma-separated list of headers to log for debugging.	 | None |
| |
| **LogProbes** | 	When true, adds additional logging for backend health checks.	 | false |
| |
| **MaxQueueLength** | 	Sets the maximum number of requests that can be queued.	 | 10 |
| |
| **OAuthAudience** | 	The audience to request when fetching OAuth tokens. Requires UseOAuth.	 | None |
| |
| **PollInterval** | 	The polling interval (in milliseconds) for backend servers.	 | 15000 |
| |
| **PollTimeout** | 	The timeout (in milliseconds) for each poll request.	 | 3000 |
| |
| **Port** | 	The port number the server listens on.	 | 80 |
| |
| **PriorityKeys** | 	Comma-separated keys matching the header 'S7PPriorityKey'. If matched, the service uses the corresponding PriorityValues.	 | 12345,234 |
| |
| **PriorityValues** | 	A comma-separated list of priorities that correspond to PriorityKeys.	 | 1,3 |
| |
| **PriorityWorkers** | 	A comma-separated list (e.g., “2:1,3:1”) specifying the number of workers for each priority.	 | 2:1,3:1 |
| |
| **Probe_path1, Probe_path2, ...** | 	Probe paths for corresponding hosts. Each path is checked when the host is first created.	 | echo/resource?param1=sample |
| |
| **RequestIDPrefix** | 	The prefix appended to every request ID.	 | S7P |
| |
| **RequiredHeaders** | 	A comma-separated list of headers that are required for incoming requests.	 | None |
| |
| **SuccessRate** | 	The minimum success rate (in percent) required for hosts to remain in rotation.	 | 80 |
| |
| **TERMINATION_GRACE_PERIOD_SECONDS** | 	Number of seconds the service waits before forcing shutdown.	 | 30 |
| |
| **Timeout** | 	The connection timeout (in milliseconds) for backend requests. If exceeded, the proxy tries the next host.	 | 3000 |
| |
| **UniqueUserHeaders** | 	A list of header names that uniquely identify the caller or user.	 | X-UserID |
| |
| **UseOAuth** | 	Enables or disables OAuth token fetching for requests.	 | false |
| |
| **UseProfiles** | 	Enables or disables additional user profile processing (e.g., reading from UserConfigUrl).	 | false |
| |
| **UserConfigUrl** | 	The path or URL for a user configuration file, if profiles are in use.	 | file:config.json |
| |
| **UserPriorityThreshold** | 	Threshold for elevating user priority.	 | 0.1f |
| |
| **ValidateHeaders** | 	A comma-separated list of key: value pairs for validating incoming headers.	 | None |
| |
| **Workers** | 	The total number of worker threads handling proxy requests.	 | 10 |
| |


### Proxy response codes:

| Code | Description |
|-|-|
| 200 | Success |
| 400 | Bad Request, there was an issue with the http request format |
| 408 | Request Timed Out, the request to the specific backend host timed out. | 
| 410 | The time specified in the S7PTTL header is older than the time when the request was processed |
| 500 | Internal Server Error, Check application insights for details |
| 502 | Bad Gateway, The request could not be completed.  Are the backend hosts overloaded?  |
| |

### Headers Used : 

| Header | Description |
|-|-|
| **S7PDEBUG** | Set to true to enable tracing at the request level. |
| **S7PPriorityKey** | If the incoming request has this header and it matches one of the values in the 'PriorityKeys' environment variable, than the request will use the associated priority. |
| **S7PREQUEUE** | If the remote host returns a 429 and sets this header with a value of true, the request will be requeued using the original enqueue time. |
| **S7PTTL** | The time after which the message will be considered to be expired.  In this case, the proxy will respond with a 410 status code. |
| **x-Request-Queue-Duration** | The amount of time the message was enqueue'd.  | 
| **x-Request-Process-Duration** | The amount of time it took to process it. |
| **x-Request-Worker** | The worker ID | 
| |

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
- Dotnet SDK 8.0

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
# FILL IN THE NAME OF YOUR ACR HERE ( without the azurecr.io part )
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
az containerapp create --name $ACANAME --resource-group $GROUP --environment $ACENV --image $ACR.azurecr.io/myproxy:v1 --target-port 443 --ingress external --registry-server $ACR.azurecr.io --query properties.configuration.ingress.fqdn --registry-username $ACR_USERNAME --registry-password $ACR_PASSWORD --env-vars Host1=https://localhost:3000 Host2=http://localhost:5000

```
## Deploy to Container Apps via a Github Action

You can create a github workflow to deploy this code to an Azure container app.  You can follow the step by step instruction from a similar project in the following video:

[![Video Title](https://i.ytimg.com/vi/-KojzBMM2ic/hqdefault.jpg)](https://www.youtube.com/watch?v=-KojzBMM2ic "How to Create a Github Action to Deploy to Azure Container Apps")
