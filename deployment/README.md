# SimpleL7Proxy — Production Reference Deployment on Azure

- No public ingress, managed identity only
- Bash + Bicep, idempotent scripts

## Audience

- Platform / infra engineers deploying on Azure
- App teams consuming SimpleL7Proxy in private VNets
- Not for public internet deployments

**Outputs:**

- Private, VNet-integrated L7 proxy on Azure Container Apps
- Private DNS name resolvable inside the VNet
- Bash + Bicep deployment, idempotent
- Health probe, async processing, and APIM integration (optional)

![Target architecture](arch.png)

---

## Architecture overview

**Request flow (simplified):**

1. A client inside the VNet sends a request to an internal DNS name.
2. Private DNS resolves the name to the internal ACA ingress.
3. The request lands in the SimpleL7Proxy container.
4. The proxy resolves backend configuration from **Azure App Configuration**.
5. Optional async payloads are stored in **Azure Blob Storage**.
6. The response is returned synchronously, or retrieved asynchronously by the client.

**Control plane vs data plane:**

- **Control plane** — App Configuration, ACR, deployment scripts, managed identity / RBAC
- **Data plane** — ACA environment, VNet, proxy traffic, Private DNS, backend calls

Backend configuration changes take effect without redeploying the proxy — see [Day-2 Operations](DAY2_OPERATIONS.md).

---

## Security model

**Built in:**

- ✅ No public ingress (internal-only ACA)
- ✅ Managed Identity (no secrets in scripts)
- ✅ Private DNS + VNet isolation
- ✅ App Configuration access scoped to identity
- ✅ ACR access via identity (not admin creds)

**Intentionally not covered (expected upstream or out of scope):**

- TLS termination — expected upstream (APIM, gateway, or client)
- Public exposure
- WAF

---

## Choose your path

**If this is production** → follow the full [Recommended Deployment Path](#recommended-deployment-path) (Steps 1–7).

**If this is dev/test** → run Steps 1–3, then Step 4a only. Use the internal FQDN directly; skip DNS, AppConfig, and Blob.

> **Dev/test only. Do not use in production.**

---

## Recommended Deployment Path

### Step 1: Prerequisites ⚙️

Verifies the local environment has the tools and access required by all subsequent steps.

**Creates:** nothing (validation only)

**Requires:**
- Bash shell
- Internet access to install or verify tools

**Breaks if misconfigured:**
- Missing `az` — all deployment scripts fail
- Missing `jq` — parameter extraction in scripts fails silently
- Unauthenticated Azure CLI — all `az` calls return 401

```bash
cd Prereq
./validate.sh
```

**If this fails:**
- `az: command not found` — install Azure CLI: `https://aka.ms/installazurecli`
- `jq: command not found` — install jq via your package manager (`apt install jq` / `brew install jq`)
- `az account show` returns error — run `az login` and set the correct subscription with `az account set -s <id>`

[→ Learn more](Prereq/README.md)

---

### Step 2: Virtual Network (VNet) 🌐

Creates the VNet and all subnets used by ACA, Functions, APIM, and private endpoints.

**Defaults:**
- VNet name: `vnet-myapp`, CIDR: `10.40.0.0/16`
- `snet-aca`: `10.40.0.0/23`
- `snet-clientvm`: `10.40.2.0/24`
- `snet-azurefunctions`: `10.40.3.0/24`
- `snet-apim`: `10.40.4.0/24`
- `snet-privateendpoints`: `10.40.5.0/24`

**Change only if:**
- The default CIDR overlaps an existing VNet in your subscription
- Your naming convention differs from `vnet-myapp` / `snet-*`

**Requires:**
- Azure CLI authenticated
- Resource group exists

**Breaks if misconfigured:**
- Wrong CIDR — subnet delegation for ACA fails
- Missing ACA subnet — Step 4 cannot attach the Container Apps environment to the VNet
- Overlapping address space — VNet peering or routing fails silently

```bash
cd VNet
cp deploy.parameters.example.sh deploy.parameters.sh
# Edit deploy.parameters.sh
./deploy.sh
```

**If this fails:**
- `Subnet address prefix overlaps` — choose a non-overlapping CIDR and re-run
- `ResourceGroupNotFound` — create the resource group first: `az group create -n <rg> -l <region>`
- Deployment succeeds but ACA subnet delegation is missing — check: `az network vnet subnet show -n snet-aca --vnet-name <vnet> -g <rg> --query delegations`

[→ Learn more](VNet/README.md)

---

### Step 3: Build Container Image 🐳

Builds the proxy image and pushes it to ACR with an immutable version tag from `Constants.cs`.

**Defaults:**
- `BUILD_METHOD=remote` (ACR performs the build; no Docker required)
- Version tag sourced from `src/SimpleL7Proxy/Constants.cs`
- Image URI: `<ACR_NAME>.azurecr.io/simple-l7-proxy:v<VERSION>`

**Change only if:**
- Dev/test and you need faster local iteration → set `BUILD_METHOD=local` (requires Docker)

  > **Dev/test only. Do not use in production.**

**Requires:**
- Azure CLI authenticated
- ACR instance exists with build permissions
- For local build: Docker running, `az acr login --name <registry>` completed

**Breaks if misconfigured:**
- Wrong `ACR_NAME` — push fails with 404
- Version not set in `Constants.cs` — tag extraction fails and build aborts
- Insufficient ACR role — push denied with 403

```bash
cd ContainerImage
cp build.parameters.example.sh build.parameters.sh
# Set ACR_NAME; set BUILD_METHOD to "remote" (default) or "local"
./build.sh
```

```bash
# Check the resolved version tag before deploying
./get-version.sh
```

**If this fails:**
- `repository does not exist` (404) — verify `ACR_NAME` in `build.parameters.sh` matches the registry: `az acr list -o table`
- `unauthorized` (403) — assign `AcrPush` role: `az role assignment create --role AcrPush --assignee <your-upn> --scope <acr-id>`
- `version tag is empty` — ensure `Constants.cs` contains a non-empty version string and re-run `./get-version.sh`

[→ Learn more](ContainerImage/README.md)

---

### Step 4: Azure Container Apps (ACA) 📦

Deploys the SimpleL7Proxy container into the VNet as an internal-only Container App.

**If this is production** → use **Option 4b** (proxy + HealthProbe sidecar). Continue below.

**If this is dev/test** → use **Option 4a** (proxy only). Skip Option 4b.

---

#### Option 4b: Production (proxy + HealthProbe sidecar)

Provides health probes, failure detection, and per-container observability on ports 8000 and 9000.

**Defaults:**
- `SimpleL7Proxy` on port 8000
- `HealthProbe` on port 9000
- Internal-only ingress
- System-assigned managed identity
- Version tags sourced from `src/SimpleL7Proxy/Constants.cs` and `src/HealthProbe/Constants.cs`

**Change only if:**
- Your ACR name, resource group, or app name differ from defaults in `deploy.parameters.example.sh`
- Backend hosts are not yet set in App Configuration (set them here as bootstrap values)

**Requires:**
- VNet and ACA subnet from Step 2
- Both proxy and health probe images in ACR from Step 3
- `src/HealthProbe/Constants.cs` with a version string

**Breaks if misconfigured:**
- Missing health probe image tag — container app fails to start
- Wrong ACR name — both image pulls fail
- Subnet not delegated — environment provisioning fails

```bash
cd proxy-with-sidecar
cp deploy.parameters.example.sh deploy.parameters.sh
# Set ACR, resource group, app names, and backend hosts
./deploy.sh
```

**If this fails:**
- Container app stuck in `Waiting` / image pull error — check: `az containerapp logs show -n <app> -g <rg> --type system`; verify both image tags exist in ACR
- `SubnetDelegationRequired` — confirm `snet-aca` is delegated: `az network vnet subnet show -n snet-aca --vnet-name <vnet> -g <rg> --query delegations`
- Identity cannot pull from ACR — assign `AcrPull` to the managed identity: `az role assignment create --role AcrPull --assignee <principal-id> --scope <acr-id>`

[→ Learn more](proxy-with-sidecar/README.md)

---

#### Option 4a: Dev/test (proxy only)

> **Dev/test only. Do not use in production.**

**Defaults:**
- `SimpleL7Proxy` on port 8000
- Internal-only ingress
- System-assigned managed identity
- Log Analytics workspace link

**Change only if:**
- Your VNet name, subnet name, or ACR image URI differ from defaults in `deploy.parameters.example.sh`

**Requires:**
- VNet and ACA subnet from Step 2
- Container image in ACR from Step 3
- ACA subnet delegated to `Microsoft.App/environments`

**Breaks if misconfigured:**
- Wrong subnet ID — environment creation fails
- Image tag not found in ACR — container fails to start
- Missing managed identity role on ACR — image pull denied

```bash
cd ACA
cp deploy.parameters.example.sh deploy.parameters.sh
# Set VNET_NAME, SUBNET_ACA_NAME, ACR image URI
./deploy.sh
```

**If this fails:**
- Container fails to start — check: `az containerapp logs show -n <app> -g <rg> --type system`
- Image not found — verify tag: `az acr repository show-tags -n <acr> --repository simple-l7-proxy`
- `AcrPull` denied — assign the role to the managed identity principal ID shown in the error

[→ Learn more](ACA/README.md)

---

**After Step 4, note the internal FQDN:**
```
ca-myapp-proxy.internal.eastus.azurecontainerapps.io
```

---

### Step 5: DNS 🔍

Creates a private DNS zone and maps a short name to the ACA internal FQDN.

**If this is production** → run this step. Clients must not depend on platform-generated FQDNs that change on redeploy.

**If this is dev/test** → skip this step and use the internal FQDN from Step 4 directly.

**Defaults:**
- DNS zone name: configurable (e.g., `internal.contoso.com`)
- One CNAME record: short name → ACA internal FQDN
- VNet link scoped to the VNet from Step 2

**Change only if:**
- You need additional A/CNAME records for APIM or other services
- Your DNS zone name conflicts with an existing private zone in the VNet

**Requires:**
- VNet from Step 2
- ACA internal FQDN from Step 4

**Breaks if misconfigured:**
- Missing VNet link — DNS zone exists but queries from inside the VNet do not resolve
- Wrong CNAME target — all clients get NXDOMAIN or resolve to stale endpoint
- Zone name collision with existing private zone — deployment fails

```bash
cd DNS
cp deploy.parameters.example.sh deploy.parameters.sh
# Set VNET_NAME, DNS_ZONE_NAME, ACA_FQDN
./deploy.sh
```

**If this fails:**
- Zone deploys but names don't resolve — check VNet link: `az network private-dns link vnet list -g <rg> -z <zone>`
- `ZoneAlreadyExists` — a private zone with the same name is already linked to the VNet; reuse it or choose a different zone name
- CNAME resolves to wrong host — verify target: `az network private-dns record-set cname show -g <rg> -z <zone> -n <record>`

[→ Learn more](DNS/README.md)

---

### Step 6: Application Configuration 🛠️

Provisions an Azure App Configuration store and loads the key-value settings that the proxy reads at runtime.

**Defaults:**
- New App Configuration store (Free or Standard tier)
- `App Configuration Data Reader` role assigned to the ACA managed identity
- Keys: backend URLs, priorities, timeouts, load-balancing weights

**Change only if:**
- You are reusing an existing App Configuration store → set the store name and skip store creation
- Backend URLs, priority order, or timeout values differ from the example parameters

**Requires:**
- ACA managed identity from Step 4
- Backend host URLs finalized

**Breaks if misconfigured:**
- Missing required keys — proxy fails to start (fail-fast on startup)
- Wrong identity role — proxy cannot read config; container exits on startup
- Invalid backend URL format — requests to that backend fail at the routing layer; other backends continue serving
- Config store in wrong region or subscription — managed identity token exchange fails

```bash
cd AppConfiguration
cp deploy.parameters.example.sh deploy.parameters.sh
# Set backend URLs, priorities, timeouts
./deploy.sh
```

**If this fails:**
- Proxy container exits immediately after deploy — missing required keys; check: `az containerapp logs show -n <app> -g <rg>` for `KeyNotFoundException` or config errors
- `Forbidden` reading config at runtime — verify role: `az role assignment list --assignee <principal-id> --scope <store-id>`
- Config changes not picked up — confirm the App Configuration endpoint in the container app environment variables matches the store URL

[→ Learn more](AppConfiguration/README.md)

---

### Step 7: Additional Services

#### BlobStorage

Provisions a Storage Account and container for async response payloads or request/response logging.

**If this is production with async workflows** → run this step before enabling async in App Configuration.

**If this is dev/test or sync-only** → skip.

**Defaults:**
- New Storage Account (LRS, Standard tier)
- Blob container name: configurable in `deploy.parameters.sh`
- `Storage Blob Data Contributor` role for the ACA managed identity

**Change only if:**
- You are reusing an existing storage account → set the account name and skip account creation
- Your container name differs from the default

**Requires:**
- ACA managed identity from Step 4

**Breaks if misconfigured:**
- Missing role assignment — proxy cannot write blobs; async requests return 500
- Wrong container name in App Configuration — blob writes fail silently

```bash
cd BlobStorage
./deploy.sh
```

**If this fails:**
- Async requests return 500 after deployment — missing `Storage Blob Data Contributor` role; check: `az role assignment list --assignee <principal-id> --scope <storage-id>`
- `BlobServiceProperties` error during deployment — storage account name already taken globally; change the name in `deploy.parameters.sh`
- Blobs not written — container name in App Configuration does not match the container created here

[→ Learn more](BlobStorage/README.md)

#### APIM Policy Deployment

Deploys API policies and routes to Azure API Management.

**If this is production behind an API gateway** → run this step after APIM is provisioned in `snet-apim`.

**If APIM is not in your topology** → skip.

**Defaults:**
- Policy XML applied at the API or operation scope
- No product / subscription scope unless explicitly set

**Change only if:**
- You need policy applied at the product or subscription scope
- Your APIM instance name or API path differs from the example parameters

**Requires:**
- APIM instance in `snet-apim` subnet (Step 2)
- ACA internal FQDN reachable from APIM subnet

**Breaks if misconfigured:**
- APIM cannot reach ACA FQDN — all proxied requests return 502 from APIM
- Policy XML invalid — deployment rejected by APIM control plane

See [APIM-Policy/README.md](../APIM-Policy/README.md)

---

## Deployment Scenarios

Each scenario maps to a well-known [Azure Architecture Center](https://learn.microsoft.com/azure/architecture/patterns/) pattern:

| Scenario | Architectural pattern |
|---|---|
| Async response handling | [Claim Check](https://learn.microsoft.com/azure/architecture/patterns/claim-check) / [Async Request-Reply](https://learn.microsoft.com/azure/architecture/patterns/async-request-reply) |
| APIM in front of the proxy | [Gateway Routing](https://learn.microsoft.com/azure/architecture/patterns/gateway-routing) / Policy enforcement |
| Multi-tenant deployments | Shared runtime, isolated config (per-tenant App Configuration keys) |

All scenarios use **Step 4b (production)** except the dev/test scenario, which uses **Step 4a**.

### Scenario: Single Proxy with Health Monitoring
**Packages needed:** VNet (Step 2) → ContainerImage (Step 3) → ACA with Sidecar (Step 4b) → DNS (Step 5) → AppConfiguration (Step 6)

### Scenario: Multi-tenant with Async Processing
**Packages needed:** VNet (Step 2) → ContainerImage (Step 3) → ACA with Sidecar (Step 4b) → DNS (Step 5) → AppConfiguration (Step 6) → BlobStorage (Step 7)

### Scenario: API Gateway Pattern
**Packages needed:** VNet (Step 2) → ContainerImage (Step 3) → ACA with Sidecar (Step 4b) → DNS (Step 5) → AppConfiguration (Step 6) → APIM (Step 7)

### Scenario: Simple Testing/Development

> **Dev/test only. Do not use in production.**

Use the internal FQDN from Step 4 directly.

---

## Quick Reference: File Organization

```
deployment/
├── Prereq/                    # Prerequisites verification
│   ├── README.md
│   └── validate.sh
│
├── VNet/                      # Virtual Network + Subnets
│   ├── deploy.parameters.example.sh
│   ├── deploy.sh
│   └── README.md
│
├── ContainerImage/            # Container Image Build
│   ├── build.parameters.example.sh
│   ├── build.sh
│   └── README.md
│
├── ACA/                       # Container Apps (Simple)
│   ├── deploy.parameters.example.sh
│   ├── deploy.sh
│   └── README.md
│
├── proxy-with-sidecar/        # Container Apps with HealthProbe Sidecar
│   ├── deploy.parameters.example.sh
│   ├── deploy.sh
│   ├── setup.sh
│   ├── script.bicep
│   ├── script.json
│   └── README.md
│
├── DNS/                       # Private DNS Zone
│   ├── deploy.parameters.example.sh
│   ├── deploy.sh
│   └── README.md
│
├── AppConfiguration/          # Proxy Configuration Store
│   ├── deploy.parameters.example.sh
│   ├── deploy.sh
│   └── README.md
│
├── BlobStorage/               # Async Storage (Optional)
│   ├── deploy.sh
│   └── README.md
│
└── README.md                  # This file
```

---

## Deployment Checklist

- [ ] **Prerequisites**
  - [ ] Azure CLI installed (`az --version`)
  - [ ] Authenticated to Azure (`az login`)
  - [ ] Bash shell available
  - [ ] Required tools: jq, Python3

- [ ] **Before VNet**
  - [ ] Decide on CIDR range (default: `10.40.0.0/16`)
  - [ ] Have resource group name ready
  - [ ] Choose Azure region

- [ ] **Before Build**
  - [ ] Source code ready (`src/SimpleL7Proxy/Constants.cs` has version)
  - [ ] Container Registry created in Azure
  - [ ] Azure CLI installed and authenticated
  - [ ] Docker installed (optional, only for local build method)

- [ ] **Before ACA (Choose Step 4a OR 4b)**
  - [ ] VNet deployed
  - [ ] Container image built and pushed to ACR
  - [ ] Image URI ready (e.g., `myregistry.azurecr.io/simple-l7-proxy:latest`)
  - [ ] For **Step 4b (Sidecar)** only:
    - [ ] Both `src/SimpleL7Proxy/Constants.cs` and `src/HealthProbe/Constants.cs` have versions
    - [ ] `deploy.sh` will auto-extract both versions for dual-container deployment

- [ ] **Before DNS**
  - [ ] ACA deployed (to get internal FQDN)
  - [ ] Decide on DNS zone name

- [ ] **Before AppConfiguration**
  - [ ] ACA deployed
  - [ ] Backend host URLs finalized
  - [ ] Proxy settings known (timeouts, priorities, etc.)

---

## Day-2 Operations and Common Tasks

See **[Day-2 Operations](DAY2_OPERATIONS.md)** for backend updates, version rollouts, scaling, failure modes, and logs.

### View Deployment Status
```bash
# List all resources in the resource group
az resource list --resource-group "rg-myapp-network" -o table

# View ACA details
az containerapp show --resource-group "rg-myapp-network" \
    --name "ca-myapp-proxy"
```

### Update Proxy Configuration
```bash
cd AppConfiguration
./deploy.sh
```

### Re-run a Deployment (Idempotent)
```bash
cd ACA
./deploy.sh   # Updates image, CPU, memory, replicas
```

### Delete Resources
```bash
# Delete entire resource group (all resources inside)
az group delete --resource-group "rg-myapp-network"
```

---

## Troubleshooting

> **Diagnostic ordering rule — always start in this order:**
>
> 1. **Container App logs** — is the proxy running and what is it saying?
> 2. **Image availability** — can ACA pull the tag from ACR?
> 3. **Network reachability** — can the proxy reach its backends from the ACA subnet?
> 4. **DNS resolution** — do internal names resolve correctly inside the VNet?
>
### "deploy.parameters.sh not found"
```bash
cp deploy.parameters.example.sh deploy.parameters.sh
```

### "Could not find subnet in VNet"
- Verify `VNET_NAME` and `SUBNET_ACA_NAME` match VNet deployment
- Check resource group is correct
- Confirm VNet was deployed successfully

### "Container App failed to start"
- Check image exists in ACR: `az acr repository list --name myregistry`
- Verify image name format: `registry.azurecr.io/repo:tag`
- Check ACA logs: `az containerapp logs show --resource-group ... --name ...`

### "DNS records not resolving"
- Confirm VNet is linked to DNS zone
- Test from inside the VNet (not from public internet)
- Check record names and types with: `az network private-dns record-set list --zone-name ... --resource-group ...`

---

## Next Steps

1. **Test connectivity** — deploy a client VM in `snet-clientvm`; curl the ACA FQDN or DNS name
2. **Configure backend hosts** — update AppConfiguration keys; proxy picks up changes without redeploy
3. **Enable monitoring** — configure Application Insights in the ACA deployment; add Azure Monitor alerts
4. **Scale** — adjust `minReplicas` / `maxReplicas` in `deploy.parameters.sh` and re-run `./deploy.sh`

---

## Need Help?

- [Deployment Prerequisites](Prereq/README.md) — Verify your environment
- [VNet Deployment](VNet/README.md) — Network setup
- [ACA Deployment](ACA/README.md) — Container Apps
- [DNS Deployment](DNS/README.md) — Private DNS setup
- [AppConfiguration Deployment](AppConfiguration/README.md) — Proxy settings
- [Main Documentation](../docs/) — Architecture, design, troubleshooting

---

## Support

- [Troubleshooting guide](../docs/TroubleshootTOC.md)
- [Configuration reference](../docs/CONFIGURATION_SETTINGS.md)
- [Advanced scenarios](../docs/ADVANCED_DEVELOPMENT.md)
