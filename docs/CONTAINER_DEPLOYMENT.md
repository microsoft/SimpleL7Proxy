# Container Deployment

Build a Docker image from the `src/` directory and run it locally or deploy it to Azure Container Apps — the proxy listens on port **443** (HTTP) and exposes a health probe server on port **9000**.

> **TL;DR**
> - **Build from `src/`** — the Dockerfile requires `Shared/` and `SimpleL7Proxy/` side-by-side; build context must be `src/`.
> - **Probe paths are in the Host connection string** — use `Host1=host=https://api.example.com;probe=/health` (not separate `Probe_path1=` variables).
> - **Fastest path to Azure:** (1) `.azure/setup.sh` (check prerequisites, choose scenario), (2) `azd provision` (create Container App + App Configuration + ACR), (3) `deployment/AppConfiguration/deploy.sh` (seed App Configuration from your config), (4) `.azure/deploy.sh` (build image, push to ACR, update Container App).


## Container Ports

| Port | Purpose |
|------|---------|
| `443` | Main proxy traffic (HTTP, not HTTPS — TLS is terminated by ACA ingress) |
| `9000` | Health probe server — serves `/liveness`, `/readiness`, `/startup` |

> [!NOTE]
> Port 443 carries plain HTTP inside the container. TLS termination is done by the Azure Container Apps ingress or an upstream load balancer.

---

## Deployment Workflow — From Code to Production

**Full end-to-end path with automated scripts:**

### Step 1: Run setup and provision infrastructure

```bash
# From repo root
.azure/setup.sh
```

**What it does:**
- Checks prerequisites (azd, Azure CLI)
- Authenticates to Azure and selects subscription
- Guides you through selecting a deployment scenario (local-with-cloud, full-cloud, secure-vnet)
- Initializes AZD environment

**Output:** `.azure/.env` file with resource names and subscription ID.

---

### Step 2: Provision Azure resources (Container App, App Configuration, ACR)

```bash
azd provision
```

**What it does:**
- Creates resource group, Container App, Azure Container Registry, App Configuration store
- Sets up managed identity for Container App
- Configures networking based on your scenario selection
- Exports deployment variables to `.azure/.env` for downstream scripts

**Time:** ~5–10 minutes.

---

### Step 3: Seed App Configuration with proxy settings

```bash
cd deployment/AppConfiguration
cp deploy.parameters.example.sh deploy.parameters.sh
# Edit deploy.parameters.sh with your resource names
./deploy.sh
```

**What it does:**
- Discovers all publishable settings from `src/SimpleL7Proxy/Config/ProxyConfig.cs` (marked with `[ConfigOption(...)]`)
- Reads current values from the running Container App (or falls back to local shell variables)
- Seeds App Configuration with all discovered keys in both **Warm** (hot-reload) and **Cold** (restart) modes
- Publishes them under prefixes `Warm:*` and `Cold:*` so you can toggle reload behavior instantly from the portal
- Sets up the `Warm:Sentinel` key as the refresh trigger

**Parameters required:**
- `CONTAINER_APP_NAME`: deployed Container App name
- `CONTAINER_APP_RESOURCE_GROUP`: resource group containing the Container App
- `APPCONFIG_NAME`: App Configuration store name
- `RESOURCE_GROUP`: resource group for App Configuration (usually same as Container App)
- `LOCATION`: Azure region

**Output:** All settings now visible in Azure Portal **App Configuration > Configuration Explorer**; operators can modify Warm settings without restarting.

---

### Step 4: Build, push image, and update Container App

```bash
.azure/deploy.sh
```

**What it does:**
- Extracts Docker image name and version from `src/SimpleL7Proxy/Constants.cs`
- Logs in to ACR (from AZD environment)
- Builds Docker image from `src/` directory (correct context)
- Pushes image to ACR with version tags (e.g., `simple-l7-proxy:1.0.0`, `simple-l7-proxy:latest`)
- **Optionally:** applies an environment template (Standard Production, High Performance, Cost Optimized, High Availability)
- Updates running Container App to the new image
- Restarts container with updated configuration

**Output:** Container App running latest image; all Warm settings hot-reloaded within ~30 seconds; Cold settings require a second restart.

---

## Workflow Summary (for quick reference)

```
.azure/setup.sh
     ↓ (authenticate, select scenario)
azd provision
     ↓ (create resources)
cd deployment/AppConfiguration && ./deploy.sh
     ↓ (seed all proxy settings into App Config)
.azure/deploy.sh
     ↓ (build, push, deploy)
✅ Proxy running in Azure with App Configuration hot-reload enabled
```

**Key takeaway:** After the first deployment, you can change Warm settings in the Azure Portal **without restarting**; just update `Warm:Sentinel` to trigger a refresh cycle.

---

## Building the Image

**Rule: Use `src/SimpleL7Proxy/build.sh` — it handles the correct build context, version extraction from `Constants.cs`, ACR login, and push automatically.**

```bash
export ACR=myregistry   # your ACR name, without .azurecr.io
cd src/SimpleL7Proxy
./build.sh
```

The script:
1. Reads the version from `Constants.cs` (`VERSION = "..."`) and prefixes `v` if needed
2. Logs in to ACR via `az acr login`
3. Runs `docker build` from `src/` (the correct context that includes `Shared/`)
4. Pushes `$ACR.azurecr.io/myproxy:<version>` to ACR
5. Prints the `PROXY_VERSION` export line to paste into `deploy.parameters.sh`

> [!NOTE]
> `ACR` can also be set in `deployment/proxy-with-sidecar/deploy.parameters.sh` — the script sources it automatically if that file exists.

### Updating after a code change

```bash
# 1. Bump the version in Constants.cs if needed
#    VERSION = "2.x.x"

# 2. Rebuild and push
export ACR=myregistry
cd src/SimpleL7Proxy
./build.sh

# 3. Update the running Container App to the new image
#    (the script prints the exact version — use it below)
az containerapp update \
  --name $ACANAME \
  --resource-group $GROUP \
  --image $ACR.azurecr.io/myproxy:<version>
```

> [!TIP]
> If you deployed with AZD, run `.azure/deploy.sh` instead of `az containerapp update` — it reads the version and environment from `azd env get-values` automatically.

### Manual build (without the script)

Only needed if you are not using the sidecar deployment or want a custom image name:

```bash
# Must run from src/ — Dockerfile references Shared/ at the same level
cd src
docker build -t simplel7proxy:latest -f SimpleL7Proxy/Dockerfile .
```

> [!WARNING]
> Running `docker build` from the repository root (not `src/`) will fail — `COPY Shared/` will not resolve.

---

## Building without Docker (Remote ACR Build)

If Docker is not available locally (corporate restrictions, CI/CD runners, etc.), build directly in Azure Container Registry:

```bash
# From repo root
export ACR=myregistry  # Your ACR name, without .azurecr.io
export VERSION=v2.2.11 # Or read from Constants.cs

az acr build
--registry $ACR
--image simple-l7-proxy:$VERSION
--file src/SimpleL7Proxy/Dockerfile
src
```

## Running Locally with Docker

### Minimal run

```bash
docker run -p 8000:443 \
  -e "Host1=host=https://api.example.com;probe=/health" \
  -e "Timeout=2000" \
  -e "Workers=10" \
  simplel7proxy:latest
```

### Using an environment file

Create `.env`:
```bash
Host1=host=https://api1.example.com;probe=/health
Host2=host=https://api2.example.com;probe=/health
Workers=20
MaxQueueLength=1000
Timeout=5000
APPINSIGHTS_CONNECTIONSTRING=your-connection-string
```

```bash
docker run -p 8000:443 --env-file .env simplel7proxy:latest
```

### Docker Compose

```yaml
services:
  proxy:
    build:
      context: ./src
      dockerfile: SimpleL7Proxy/Dockerfile
    ports:
      - "8000:443"
    environment:
      - Host1=host=https://api1.example.com;probe=/health
      - Host2=host=https://api2.example.com;probe=/health
      - Workers=20
      - MaxQueueLength=1000
    restart: unless-stopped
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:9000/liveness"]
      interval: 30s
      timeout: 10s
      retries: 3
```

```bash
docker compose up -d
```

> [!TIP]
> **Troubleshooting:** Health check failures using port 443 will always fail — the probe server runs on port **9000**. Use `http://localhost:9000/liveness`.

---

## Deploying to Azure Container Apps with AZD

This is the recommended path for provisioning all required Azure resources (ACR, Container Apps environment, managed identity) in one step.

### Prerequisites

- [Azure CLI](https://docs.microsoft.com/en-us/cli/azure/install-azure-cli)
- [Azure Developer CLI (azd)](https://learn.microsoft.com/en-us/azure/developer/azure-developer-cli/install-azd)
- Docker (optional; only needed for local image builds)
- For no-Docker environments, use the remote ACR build workflow in [Building without Docker (Remote ACR Build)](#building-without-docker-remote-acr-build)

### Step 1 — Setup

**Linux/macOS**
```bash
chmod +x ./.azure/setup.sh
./.azure/setup.sh
```

**Windows**
```powershell
.\.azure\setup.ps1
```

The setup script asks for:
1. **Deployment scenario** — choose one:
   - `local-proxy-public-apim` — proxy runs locally, backends on public APIM
   - `aca-proxy-public-apim` — proxy deployed as ACA, backends on public APIM
   - `vnet-proxy-deployment` — proxy inside a VNet
2. **Environment template** (optional, see table below)

### Step 2 — Provision

```bash
azd provision
```

### Step 3 — Build and Deploy

**Linux/macOS**
```bash
chmod +x ./.azure/deploy.sh
./.azure/deploy.sh
```

**Windows**
```powershell
.\.azure\deploy.ps1
```

The deploy script builds the image, pushes it to the provisioned ACR, and updates the Container App. It reads all variables from `azd env get-values`.

### Environment Templates

Templates live in `.azure/env-templates/`. Apply one during setup or when prompted by `deploy.sh`.

| Template | Best for |
|----------|----------|
| Standard Production | Balanced performance and cost — good default |
| High Performance | Maximum throughput, higher worker counts |
| Cost Optimized | Minimal resource usage |
| High Availability | Multiple backends, aggressive failover |
| Local Development | `dotnet run` on localhost backends |
| Container Development | Containerized dev/test scenarios |

---

## Deploying to Azure Container Apps Manually (CLI)

Use this path when you already have an ACR and Container Apps environment.

### Step 1 — Set variables

```bash
export ACR=<your-acr-name>      # Without .azurecr.io
export GROUP=<resource-group>
export ACENV=<containerapp-env>
export ACANAME=<containerapp-name>
export LOCATION=eastus
```

### Step 2 — Build and push

```bash
# Build from src/ directory
cd src
docker build -t $ACR.azurecr.io/simple-l7-proxy:latest -f SimpleL7Proxy/Dockerfile .
cd ..

az acr login --name $ACR
docker push $ACR.azurecr.io/simple-l7-proxy:latest
```

### Step 3 — Create resources

```bash
az group create --name $GROUP --location $LOCATION

az containerapp env create \
  --name $ACENV \
  --resource-group $GROUP \
  --location $LOCATION
```

### Step 4 — Deploy using YAML

A ready-to-use YAML template is at `deployment/containerapp-single.yaml`. Edit the placeholder values, then:

```bash
az containerapp create \
  --name $ACANAME \
  --resource-group $GROUP \
  --yaml deployment/containerapp-single.yaml
```

Or deploy inline with minimal settings:

```bash
az containerapp create \
  --name $ACANAME \
  --resource-group $GROUP \
  --environment $ACENV \
  --image $ACR.azurecr.io/simple-l7-proxy:latest \
  --target-port 443 \
  --ingress external \
  --registry-server $ACR.azurecr.io \
  --registry-identity system \
  --min-replicas 2 --max-replicas 10 \
  --cpu 1.0 --memory 2Gi \
  --env-vars \
    "Host1=host=https://api1.example.com;probe=/health" \
    "Host2=host=https://api2.example.com;probe=/health" \
    "Workers=20" \
    "MaxQueueLength=1000" \
    "Timeout=5000" \
    "APPINSIGHTS_CONNECTIONSTRING=your-connection-string" \
  --query properties.configuration.ingress.fqdn
```

> [!NOTE]
> Use `--registry-identity system` (managed identity) instead of `--registry-username`/`--registry-password` to avoid storing credentials. Grant the Container App's system identity `AcrPull` on the registry.

---

## Health Probe Configuration

The container exposes a built-in probe server on **port 9000**. Configure these in your Container App YAML:

```yaml
probes:
  - type: Liveness
    httpGet:
      path: /liveness
      port: 9000
      scheme: HTTP
    initialDelaySeconds: 10
    periodSeconds: 10
    failureThreshold: 3
  - type: Readiness
    httpGet:
      path: /readiness
      port: 9000
      scheme: HTTP
    initialDelaySeconds: 5
    periodSeconds: 5
    failureThreshold: 3
  - type: Startup
    httpGet:
      path: /startup
      port: 9000
      scheme: HTTP
    initialDelaySeconds: 0
    periodSeconds: 5
    failureThreshold: 30
```

---

## Sidecar Deployment (HealthProbe as separate container)

For deployments where the health probe runs as a dedicated sidecar container alongside the proxy in the same Container App revision, see [SIDECAR_DEPLOYMENT.md](SIDECAR_DEPLOYMENT.md).

---

## Updating the Container

### Rolling update

```bash
cd src
docker build -t $ACR.azurecr.io/simple-l7-proxy:v2 -f SimpleL7Proxy/Dockerfile .
docker push $ACR.azurecr.io/simple-l7-proxy:v2
cd ..

az containerapp update \
  --name $ACANAME \
  --resource-group $GROUP \
  --image $ACR.azurecr.io/simple-l7-proxy:v2
```

### Blue-green deployment

```bash
# Send 0% traffic to the new revision initially
az containerapp revision copy \
  --name $ACANAME \
  --resource-group $GROUP \
  --image $ACR.azurecr.io/simple-l7-proxy:v2

# Gradually shift traffic
az containerapp ingress traffic set \
  --name $ACANAME --resource-group $GROUP \
  --revision-weight latest=50 previous=50

# Complete the switch
az containerapp ingress traffic set \
  --name $ACANAME --resource-group $GROUP \
  --revision-weight latest=100
```

---

## Monitoring and Troubleshooting

### View logs

```bash
# Stream live logs
az containerapp logs show \
  --name $ACANAME --resource-group $GROUP --follow

# Recent logs
az containerapp logs show \
  --name $ACANAME --resource-group $GROUP --tail 100
```

### Check revision status

```bash
az containerapp revision list \
  --name $ACANAME --resource-group $GROUP -o table
```

### Common issues

| Symptom | Likely cause | Fix |
|---------|-------------|-----|
| Container fails to start | Missing required env var or bad image | Check `az containerapp logs show` |
| `503 Service Unavailable` | All circuit breakers OPEN | Verify backend hosts are reachable from inside ACA |
| Probe failures, container cycling | Wrong probe port | Ensure probes target port **9000**, not 443 |
| `docker build` fails with `COPY Shared/` error | Built from wrong directory | Run `docker build` from `src/`, not repo root |

---

## Deploy via GitHub Actions

[![Deploy to Azure Container Apps](https://i.ytimg.com/vi/-KojzBMM2ic/hqdefault.jpg)](https://www.youtube.com/watch?v=-KojzBMM2ic "How to Create a Github Action to Deploy to Azure Container Apps")

---

## Related Documentation

- [BACKEND_HOSTS.md](BACKEND_HOSTS.md) — Host connection string format including probe paths
- [CONFIGURATION_SETTINGS.md](CONFIGURATION_SETTINGS.md) — All environment variables
- [HEALTH_CHECKING.md](HEALTH_CHECKING.md) — Health probe internals
- [OBSERVABILITY.md](OBSERVABILITY.md) — Application Insights setup
