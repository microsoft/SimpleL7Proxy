# Sidecar Deployment (Proxy + HealthProbe)

Deploy SimpleL7Proxy as a two-container Azure Container App: the **proxy** container handles traffic on port 8000, and a separate **HealthProbe** sidecar handles liveness/readiness/startup probes on port 9000.

> **TL;DR**
> - **Two images, one Container App** — `myproxy:<ver>` and `healthprobe:<ver>` run in the same revision; they share `localhost` networking.
> - **One parameters file drives everything** — set values once in `deployment/proxy-with-sidecar/deploy.parameters.sh`; all build and deploy scripts read from it.
> - **Normal update cycle:** bump version in `Constants.cs` → run both `build.sh` scripts → run `deploy.sh`.

---

## Architecture

```
┌─────────────────────────────────────────────────────┐
│  Azure Container App revision                        │
│                                                      │
│  ┌──────────────────────┐  ┌──────────────────────┐ │
│  │  proxy               │  │  health (sidecar)    │ │
│  │  image: myproxy      │  │  image: healthprobe  │ │
│  │  port: 8000 (ACA)    │  │  port: 9000          │ │
│  │  CPU: 0.5 / Mem: 1Gi │  │  CPU: 0.25 / Mem:    │ │
│  │                      │  │        0.5Gi         │ │
│  │  HealthProbeSidecar= │  │  probes: /liveness   │ │
│  │  enabled=true;       │◄─┤         /readiness   │ │
│  │  url=localhost:9000  │  │         /startup     │ │
│  └──────────────────────┘  └──────────────────────┘ │
│            ▲                                         │
│     ACA ingress (external, port 8000)                │
└─────────────────────────────────────────────────────┘
```

The proxy is configured with `HealthProbeSidecar=enabled=true;url=http://localhost:9000` so it delegates its own health state to the sidecar. ACA probes hit the `health` container; traffic ingress hits the `proxy` container.

---

## Reference — deploy.parameters.sh

All scripts read from `deployment/proxy-with-sidecar/deploy.parameters.sh`. Set these values once.

| Variable | Default | Description |
|----------|---------|-------------|
| `ACR` | _(required)_ | ACR name without `.azurecr.io` |
| `REGISTRY_SERVER` | `${ACR}.azurecr.io` | Derived automatically |
| `RESOURCE_GROUP` | _(required)_ | Azure resource group |
| `LOCATION` | `eastus` | Azure region |
| `CONTAINER_APP_NAME` | _(required)_ | Container App name |
| `ENVIRONMENT_NAME` | _(required)_ | Container Apps environment name |
| `HOST1` | _(required)_ | Backend host connection string |
| `WEB_CPU` / `WEB_MEMORY` | `0.5` / `1.0` Gi | Proxy container resources |
| `HEALTH_CPU` / `HEALTH_MEMORY` | `0.25` / `0.5` Gi | Sidecar resources |
| `WEB_PORT` | `8000` | Proxy ingress port |
| `HEALTH_PORT` | `9000` | Sidecar probe port |
| `INGRESS_TYPE` | `external` | `external` or `internal` |
| `REVISION_MODE` | `single` | `single` (recommended) or `multiple` |

> [!NOTE]
> `PROXY_VERSION` and `HEALTHPROBE_VERSION` are **auto-extracted** from `src/SimpleL7Proxy/Constants.cs` and `src/HealthProbe/Constants.cs` — you do not need to set them manually.

---

## First-time Setup

### Step 1 — Configure parameters

```bash
cd deployment/proxy-with-sidecar
# deploy.parameters.sh is already present; edit it with your values
nano deploy.parameters.sh
```

Minimum required values:

```bash
export ACR="myregistry"
export RESOURCE_GROUP="my-resource-group"
export CONTAINER_APP_NAME="my-proxy-app"
export ENVIRONMENT_NAME="my-aca-environment"
export HOST1="host=https://my-api.azure-api.net;mode=apim;path=/;probe=/status-0123456789abcdef"
```

> [!WARNING]
> Do not commit `deploy.parameters.sh` to source control — it contains environment-specific values. It is listed in `deployment/.gitignore`.

### Step 2 — Build both images

Both build scripts read `ACR` from `deploy.parameters.sh` automatically.

```bash
# Build the proxy image
cd src/SimpleL7Proxy
./build.sh

# Build the health probe sidecar image
cd ../HealthProbe
./build.sh
```

Each script:
1. Extracts the version from its own `Constants.cs`
2. Logs in to ACR via `az acr login`
3. Builds from `src/` (includes `Shared/`)
4. Pushes `$ACR.azurecr.io/myproxy:<ver>` and `$ACR.azurecr.io/healthprobe:<ver>` respectively

### Step 3 — Create the Container App and assign RBAC

Run `setup.sh` **once** — it creates the Container App with a placeholder image, enables system-assigned managed identity, and grants `AcrPull` on the ACR. It waits 60 seconds for the role to propagate.

```bash
cd deployment/proxy-with-sidecar
chmod +x setup.sh deploy.sh
./setup.sh
```

> [!NOTE]
> If the Container App already exists, `setup.sh` enables managed identity on it and assigns `AcrPull` without recreating it.

### Step 4 — Deploy

```bash
./deploy.sh
```

`deploy.sh` invokes `script.bicep` which creates/updates the Container App with both containers, probes, scaling rules, and registry configuration in a single ARM deployment.

---

## Updating After a Code Change

```bash
# 1. Bump version in the relevant Constants.cs (if releasing a new version)
#    src/SimpleL7Proxy/Constants.cs  → VERSION = "2.x.x"
#    src/HealthProbe/Constants.cs    → VERSION = "2.x.x"

# 2. Rebuild whichever image changed
cd src/SimpleL7Proxy && ./build.sh   # proxy only
cd ../HealthProbe && ./build.sh       # sidecar only (if changed)

# 3. Redeploy
cd ../../deployment/proxy-with-sidecar
./deploy.sh
```

`deploy.sh` reads the current versions from `Constants.cs` on each run, so the new image tags are picked up automatically.

> [!TIP]
> If only the proxy changed, you still only need to re-run `deploy.sh` — the sidecar image tag is unchanged and Bicep will not restart it unnecessarily unless the revision suffix changes.

---

## Worked Example

> **Setup:** ACR=`myacr`, proxy version `v2.1.0`, sidecar version `v1.3.0`.

| Step | Command | Result |
|------|---------|--------|
| Edit params | `nano deploy.parameters.sh` | `ACR=myacr`, `CONTAINER_APP_NAME=myproxy` set |
| Build proxy | `cd src/SimpleL7Proxy && ./build.sh` | Pushes `myacr.azurecr.io/myproxy:v2.1.0` |
| Build sidecar | `cd src/HealthProbe && ./build.sh` | Pushes `myacr.azurecr.io/healthprobe:v1.3.0` |
| First-time setup | `./setup.sh` | Container App created, `AcrPull` assigned |
| Deploy | `./deploy.sh` | Bicep deploys both containers in one revision |
| Verify | `az containerapp revision list ...` | New revision active, probes passing |

---

## Monitoring and Troubleshooting

### View logs per container

```bash
# Proxy container
az containerapp logs show \
  --name $CONTAINER_APP_NAME \
  --resource-group $RESOURCE_GROUP \
  --container proxy --follow

# Health sidecar
az containerapp logs show \
  --name $CONTAINER_APP_NAME \
  --resource-group $RESOURCE_GROUP \
  --container health --follow
```

### Check revision and replica status

```bash
az containerapp revision list \
  --name $CONTAINER_APP_NAME \
  --resource-group $RESOURCE_GROUP \
  -o table

az containerapp replica list \
  --name $CONTAINER_APP_NAME \
  --resource-group $RESOURCE_GROUP \
  --revision <revision-name> -o table
```

### Inspect probe configuration

```bash
az containerapp show \
  --name $CONTAINER_APP_NAME \
  --resource-group $RESOURCE_GROUP \
  --query "properties.template.containers[].probes"
```

### Common issues

| Symptom | Likely cause | Fix |
|---------|-------------|-----|
| `deploy.sh` fails — `WEB_IMAGE` not set | `deploy.parameters.sh` not sourced | Ensure file exists and `ACR` is set; re-run `build.sh` first |
| Container App cycling — probes failing | Sidecar not ready yet | `failureThreshold: 30` gives 5 minutes at 10 s intervals; check `health` container logs |
| `AcrPull` permission denied on first deploy | RBAC not propagated yet | Re-run `deploy.sh` after waiting ~60 s, or run `setup.sh` again |
| Proxy returns 503 on `/health` path | `HealthProbeSidecar` env var missing | Ensure `HealthProbeSidecar=enabled=true;url=http://localhost:9000` is set on proxy container |

---

## Related Documentation

- [CONTAINER_DEPLOYMENT.md](CONTAINER_DEPLOYMENT.md) — Single-container deployment (built-in probe server)
- [HEALTH_CHECKING.md](HEALTH_CHECKING.md) — Health probe internals and endpoints
- [BACKEND_HOSTS.md](BACKEND_HOSTS.md) — `HOST1` connection string format
- [CONFIGURATION_SETTINGS.md](CONFIGURATION_SETTINGS.md) — All proxy environment variables
