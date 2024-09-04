# SimpleL7Proxy

The SimpleL7Proxy is a lightweight, performance based proxy designed to route network traffic between clients and servers. It operates at the application layer (Layer 7) of the OSI model, enabling it to route HTTP(s) requests. It can be used as a regional internal proxy to manage traffic across specific regions. The server calculates the latency for each backend on a regular interval so that when a request comes in, it will connect to the lowest latency host first.

In the diagram below, a client connected to the proxy which has 3 backend hosts. The proxy identified the host with the lowest latency (Host 2) to make the request to.

![image](https://github.com/nagendramishr/SimpleL7Proxy/assets/81572024/d2b09ebb-1bce-41a7-879a-ac90aa5ae227)


## Features
- HTTP/HTTPS traffic proxy
- Failover
- Load balance based on latency
- SSL termination
- Cross-platform compatibility (Windows, Linux, macOS)
- Logging to Application Insights
- Logging to EventHub

## Usage
SimpleL7Proxy can be run standalone on the commandline or it can be deployed as a container.  All configuration is passed to it via environment variables.  When running, the server will periodically display the current back-end latencies.


### Environment Variables:

| Variable | Description | Default |
| -------- | ----------- | ------- |
|**APPENDHOSTSFILE** | When running as a container and DNS does not resolve you can have the service append to the hosts file. You will need to specify Host1, IP1 as the host and ip combination.  When the container starts up, this will add an entry to the hosts file for each combination specified. |
| |
|**APPINSIGHTS_CONNECTIONSTRING** | This variable is used to specify the connection string for Azure Application Insights. If it's set, the application will send logs to the application insights instance. |  None |
| |
| **DnsRefreshTimeout** | The number of ms to force a dns refresh.  Useful to have a small value when testing failover. | 120000 |
| |
|**EVENTHUB_CONNECTIONSTRING** | The connection for the eventhub to log into. Both the connection string and namespace are needed for logging to work.  | None |
| |
|**EVENTHUB_NAME** | The eventhub namesapce.  Both the connection string and namespace are needed for logging to work. | None |
| |
|**Host1, Host2, ...** | The hostnames of the backend servers. Up to 9 backend hosts can be specified. If a hostname is provided, the application creates a new BackendHost instance and adds it to the hosts list.  The hostname should be in the form http(s)://fqdnhostname and DNS should resolve these to an IP address.  | None |
| |
| **IgnoreSSLCert** | Toggles if the server should validate certificates.  If your hosts are using self-signed certs, set this value to true. |  false | 
| |
| **IP1, IP2, ...** | Used to specify the IP address of hosts if DNS is unavailable.  Must define Host, IP and APPENDHOSTSFILE and run as container for this to work. |
| |
|**OAuthAudience** | The audience to fetch the Oauth token for.  Used in combination with UseOauth.  | |
| |
|**PollInterval** | This variable is used to specify the interval (in milliseconds) at which the application will poll the backend servers. | 15000 |
| |
|**Port** | Specifies the port number that the server will listen on. | 443 |
| |
|**PriorityKey1** | If the incoming request has the header 'S7PPriorityKey' set to this value,  use the value of S7PPriority as the priority. | |
| |
|**PriorityKey2** | This is the secondary key.  If the incoming request has the header 'S7PPriorityKey' set to this value,  use the value of S7PPriority as the priority.| |
| |
|**Probe_path1, Probe_path2, ...** | Specifies the probe paths for the corresponding backend hosts. If a Host variable is set, the application will attempt to read the corresponding Probe_path variable when creating the BackendHost instance. | echo/resource?param1=sample |
| |
|**Success-rate** | The percentage success rate required to be used for proxying.  Any host whose success rate is lower will not be in rotation. | 80 |
| |
|**Timeout** | The connection timeout for each backend.  If the proxy times out, it will try the next host. | 3000 |
| |
|**UseOauth** | Enable the Oauth token fetch.  Should be used in combination with OAuthAudience. | false |
| |
|**Workers** | The number of proxy worker threads. | 10 |
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
export PORT=8000
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


