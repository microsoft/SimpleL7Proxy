# Quick Start

Run SimpleL7Proxy against an LLM endpoint or Azure API Management backend.

## TL;DR

- Clone the repository.
- Set `Host1` and `Port`, then run from source or deploy to Azure Container Apps.
- Call `/health`, then send a request through the proxy. A healthy proxy returns `200`.

| Setting | Value used here | Unit | Reload |
|---------|-----------------|------|--------|
| `Host1` | Backend connection string | N/A | Startup |
| `Port` | `8000` | TCP port | Startup |
| Azure App Configuration refresh | `30` | seconds | Automatic |

Units used in this document: seconds for refresh intervals and TCP port numbers for listeners.

---

# Prepare the Environment
<details>
<summary><strong>Prerequisites</strong></summary>

**Provide an LLM endpoint or Azure API Management instance as the backend.**

- LLM endpoint(s)
- Azure API Management

Choose one runtime:

## Container

- [Azure CLI](https://docs.microsoft.com/en-us/cli/azure/install-azure-cli) — Interacts with Azure
- [Azure subscription with Container Apps enabled](https://learn.microsoft.com/en-us/azure/container-apps/overview) — Owner RBAC of a resource group
- [Azure Container Apps](https://learn.microsoft.com/en-us/azure/container-apps/overview) — Runs the container in Azure
- [Azure Container Registry](https://learn.microsoft.com/en-us/azure/container-registry/container-registry-intro) — Stores the container image
- **Optional:** [Docker](https://docs.docker.com/get-docker/) — Builds or runs the container locally

## Run as code

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

<details>
<summary><strong>Optional Scenarios</strong></summary>

| Item | Notes |
|------|-------|
| [Application Insights](https://learn.microsoft.com/en-us/azure/azure-monitor/app/app-insights-overview) | Tracks telemetry and activity |
| [Azure API Management](https://learn.microsoft.com/en-us/azure/api-management/api-management-key-concepts) | Governance and compliance |
| [Azure CosmosDB](https://learn.microsoft.com/en-us/azure/cosmos-db/introduction) | User Profiles |
| [Azure Event Hub](https://learn.microsoft.com/en-us/azure/event-hubs/event-hubs-about) | Integration with Stream Analytics, Datadog, or Splunk |
| [Azure Functions](https://learn.microsoft.com/en-us/azure/azure-functions/functions-overview) | Async mode, LLM Simulator, User Profiles |
| [Azure Service Bus](https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-messaging-overview) | Async mode |
| [Azure Storage Account](https://learn.microsoft.com/en-us/azure/storage/common/storage-account-overview) | Async mode |
</details>

</details>

---
# Clone the Repository

```bash
git clone https://github.com/microsoft/SimpleL7Proxy.git
```

## Deploy to Azure Container Apps

**Use the deployment script to configure and deploy Container Apps.**

- [Deployment instructions](../deployment/README.md)

---

## Run as Code

### Set the backend host

```bash
# LLM endpoint
export Host1="host=https://<endpoint>.openai.azure.com;mode=direct;path=/; processor=MultiLineAllUsage"

# LLM endpoint with API key
export Host1="host=https://<endpoint>.openai.azure.com;mode=direct;path=/; processor=MultiLineAllUsage; api-key=<your-api-key>"

# LLM endpoint with MI
export Host1="host=https://<endpoint>.openai.azure.com;mode=direct;path=/; processor=MultiLineAllUsage; usemi=true;audience=https://cognitiveservices.azure.com;"

# APIM
export Host1=""
```

### Set the listening port

```bash
# Port the proxy listens on
export Port=8000
```

### Run the proxy

```bash
cd SimpleL7Proxy/src/SimpleL7Proxy
dotnet run
```

Confirm that the startup message appears:
![alt text](./proxy-ready.png)

The proxy listens on port `8000` and writes `eventslog.json` in the current directory.

### Check the log file

```bash
tail -f eventslog.json
```

> [!NOTE]
> Managed Identity token acquisition can add several seconds to startup.
>
> Reduce console output by setting `LogToConsole` to a value other than `*`. See [Logging](CONFIGURATION_SETTINGS.md#logging).

---

# Check the health probe

**Call `/health` before sending traffic.** Replace `http://localhost:8000` with the Container Apps URL when deployed.

```bash
curl -i http://localhost:8000/health
```

![alt text](./helthprobe.png)

# Query the proxy

**Send an OpenAI-compatible request through the proxy.**

Set the proxy URL:
```bash
export PROXYHOST="http://localhost:8000"
```

Set the request path and body:
```bash
export URL="openai/v1/chat/completions"
export BODY='{"model":"gpt-4o","messages":[{"role":"user","content":"hello"}],"stream":true}'
```

Send the request:
```bash
curl -i -H "Content-Type: application/json" -d "$BODY" "$PROXYHOST/$URL"
```

Expected result: HTTP `200` with an LLM response.

![alt text](./llm-query.png)

# Configure Azure App Configuration

**Use Azure App Configuration to manage settings centrally.** The proxy checks it for changes every 30 seconds.

> [!NOTE]
> Create Azure App Configuration by completing step 7 of the [deployment instructions](../deployment/README.md).

```bash
export AZURE_APPCONFIG_ENDPOINT=https://your-appconfig.azconfig.io
export AZURE_APPCONFIG_LABEL=dev
```
