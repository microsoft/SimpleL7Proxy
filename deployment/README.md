# Deployment Guide

This folder contains deployment packages for SimpleL7Proxy on Azure. Each package automates provisioning and configuration of infrastructure and application settings.

## Recommended Deployment Path

Follow this sequence for a complete, production-ready deployment:

### Step 1: Prerequisites ⚙️
Start here to ensure your environment has everything needed.

```bash
cd Prereq
cat README.md       # Review all requirements
./validate.sh       # Run validation (if available)
```

**What you'll verify:**
- Azure CLI installed and authenticated
- Bash shell available
- Required tools (jq, Python)
- Azure subscription access

[→ Learn more](Prereq/README.md)

---

### Step 2: Virtual Network (VNet) 🌐
Creates the foundation network with subnets for all services.

```bash
cd VNet
cp deploy.parameters.example.sh deploy.parameters.sh
# Edit deploy.parameters.sh with your values
./deploy.sh
```

**What gets created:**
- Virtual Network (default: `vnet-myapp`, `10.40.0.0/16`)
- **ACA subnet** (`snet-aca`, `10.40.0.0/23`) — for Container Apps
- **ClientVM subnet** (`snet-clientvm`, `10.40.2.0/24`) — for testing/admin clients
- **Azure Functions subnet** (`snet-azurefunctions`, `10.40.3.0/24`) — optional backend
- **APIM subnet** (`snet-apim`, `10.40.4.0/24`) — optional API gateway
- **PrivateEndpoints subnet** (`snet-privateendpoints`, `10.40.5.0/24`) — for service integrations

**Deployment time:** ~2-3 minutes

[→ Learn more](VNet/README.md)

---

### Step 3: Build Container Image 🐳
Builds the SimpleL7Proxy container image and pushes it to Azure Container Registry.

```bash
cd ContainerImage
cp build.parameters.example.sh build.parameters.sh
# Edit build.parameters.sh (defaults use ACR remote build)
# - Set ACR_NAME to your registry
# - Leave BUILD_METHOD as "remote" or change to "local" if you have Docker
./build.sh
```

**What happens:**
- Extracts version from `src/SimpleL7Proxy/Constants.cs`
- Builds Docker image in Azure Container Registry (no Docker needed locally)
- Image URI: `myregistry.azurecr.io/simple-l7-proxy:v<VERSION>` (version auto-detected from Constants.cs)
- Ready for ACA deployment

**To see the actual image version:**
```bash
cd ContainerImage
./get-version.sh   # Shows the actual version (e.g., v1.2.3)
```

**Build Methods:**
- **Remote (Recommended)** — ACR builds, no Docker required (~3-5 min); works anywhere
- **Local** — Docker on your machine (~5-10 min); useful for development

**Prerequisites (Remote Build - Default):**
- Azure CLI installed and authenticated
- Azure Container Registry created

**Prerequisites (Local Build - Optional):**
- Docker installed and running
- Authenticated to ACR: `az acr login --name myregistry`

[→ Learn more](ContainerImage/README.md)

---

### Step 4: Azure Container Apps (ACA) 📦
Deploys SimpleL7Proxy as a containerized application within the VNet.

Choose one of two approaches:

#### Option 4a: Proxy Only (Simple)
Deploy just the SimpleL7Proxy container.

```bash
cd ACA
cp deploy.parameters.example.sh deploy.parameters.sh
# Edit deploy.parameters.sh
# - Reference VNET values from Step 2
# - Specify your container image from Step 3
./deploy.sh
```

**What gets created:**
- Container Apps Environment (integrated with the VNet)
- Container App running SimpleL7Proxy only
- Internal-only ingress (no public endpoint)
- System-assigned managed identity (optional)
- Log Analytics integration (optional)

**Deployment time:** ~5-10 minutes

**When to use:** Simple deployments, custom health probe handling in app code

[→ Learn more](ACA/README.md)

---

#### Option 4b: Proxy + HealthProbe Sidecar (Recommended)
Deploy SimpleL7Proxy with an integrated HealthProbe sidecar container for monitoring.

```bash
cd proxy-with-sidecar
cp deploy.parameters.example.sh deploy.parameters.sh
# Edit deploy.parameters.sh
# - Update ACR, resource group, app names
# - Set backend hosts and configuration
./deploy.sh
```

**What gets created:**
- Container Apps Environment (integrated with the VNet)
- Container App with two containers:
  - **SimpleL7Proxy** — Main reverse proxy (port 8000)
  - **HealthProbe** — Health check sidecar (port 9000)
- Internal-only ingress (no public endpoint)
- Both containers share network and resources
- System-assigned managed identity for ACR

**What happens automatically:**
- Extracts version from `src/SimpleL7Proxy/Constants.cs` (for proxy image)
- Extracts version from `src/HealthProbe/Constants.cs` (for health probe image)
- Deploys both containers in a single Container App

**Deployment time:** ~5-10 minutes

**When to use:** Production deployments, built-in health monitoring, multi-container patterns

**Key advantage:** Health probe and proxy run together; health checks are always available on port 9000

[→ Learn more](proxy-with-sidecar/README.md)

---

**After deployment (either option), note the internal FQDN:**
```
ca-myapp-proxy.internal.eastus.azurecontainerapps.io
```

---

### Step 5: DNS (Optional but Recommended) 🔍
Sets up private DNS for friendly internal service names.

```bash
cd DNS
cp deploy.parameters.example.sh deploy.parameters.sh
# Edit deploy.parameters.sh
# - Reference VNET from Step 2
# - Add the ACA internal FQDN from Step 4
./deploy.sh
```

**What gets created:**
- Private DNS zone (e.g., `internal.contoso.com`)
- CNAME record mapping short name to ACA FQDN
- Optional records for APIM or other services

**Why use this?**
- Clients can use readable names instead of Azure-generated FQDNs
- Stays within your VNet (no public exposure)
- Easy to add more records as you add services

**Deployment time:** ~2 minutes

[→ Learn more](DNS/README.md)

---

### Step 6: Application Configuration 🛠️
Configures SimpleL7Proxy settings (backend hosts, timeouts, load balancing, etc.).

```bash
cd AppConfiguration
cp deploy.parameters.example.sh deploy.parameters.sh
# Edit deploy.parameters.sh with proxy settings
./deploy.sh
```

**What gets created/configured:**
- Azure App Configuration store
- Key-value settings for SimpleL7Proxy (backend URLs, priorities, etc.)
- Configuration read permissions for the ACA managed identity

**Note:** SimpleL7Proxy pulls these settings at runtime.

**Deployment time:** ~3-5 minutes

[→ Learn more](AppConfiguration/README.md)

---

### Step 7 (Optional): Additional Services

Depending on your scenario, you may need:

#### BlobStorage
Enables async response storage or request/response logging.

```bash
cd BlobStorage
./deploy.sh
```

[→ Learn more](BlobStorage/README.md)

#### APIM Policy Deployment
Deploys API policies and routes through Azure API Management.

See [APIM-Policy/README.md](../APIM-Policy/README.md)

---

## Deployment Scenarios

Choose the path that matches your use case. All scenarios reference **Step 4**, where you select either:
- **Option 4a** — Proxy only (simple)
- **Option 4b** — Proxy + HealthProbe sidecar (recommended for production)

### Scenario: Single Proxy with Health Monitoring (Recommended)
**Packages needed:** VNet (Step 2) → ContainerImage (Step 3) → ACA with Sidecar (Step 4b) → DNS (Step 5) → AppConfiguration (Step 6)

Best for: Production deployments, built-in health probe, monitoring.

### Scenario: Multi-tenant with Async Processing
**Packages needed:** VNet (Step 2) → ContainerImage (Step 3) → ACA with Sidecar (Step 4b) → DNS (Step 5) → AppConfiguration (Step 6) → BlobStorage (Step 7)

Best for: Multiple clients, async response handling, audit logging.

### Scenario: API Gateway Pattern
**Packages needed:** VNet (Step 2) → ContainerImage (Step 3) → ACA with Sidecar (Step 4b) → DNS (Step 5) → AppConfiguration (Step 6) → APIM (Step 7)

Best for: API governance, rate limiting, policy enforcement.

### Scenario: Simple Testing/Development
**Packages needed:** VNet (Step 2) → ContainerImage (Step 3) → ACA Simple (Step 4a)

Best for: Quick testing, no health probe needed, minimal setup. Skip DNS (Step 5) for now; use internal FQDN directly.

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

## Common Tasks

### View Deployment Status
```bash
# List all resources in the resource group
az resource list --resource-group "rg-myapp-network" -o table

# View ACA details
az containerapp show --resource-group "rg-myapp-network" \
    --name "ca-myapp-proxy"
```

### Update Proxy Configuration
Re-run the AppConfiguration deployment with updated parameters:
```bash
cd AppConfiguration
./deploy.sh   # Reads updated deploy.parameters.sh
```

### Re-run a Deployment (Idempotent)
All scripts are safe to run multiple times. They update existing resources to match current parameters:
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

### "deploy.parameters.sh not found"
Copy the example file:
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

After deployment:

1. **Build and push container image** (if not already done)
   - Run `cd ContainerImage && ./build.sh`
   - Or use remote ACR build if Docker not available
   - See [ContainerImage/README.md](ContainerImage/README.md) for details
   
2. **Test connectivity**
   - Deploy a client VM in the ClientVM subnet
   - Test access to ACA using its internal FQDN or DNS name

3. **Configure backend hosts**
   - Update AppConfiguration with your backend URLs
   - Test end-to-end proxy functionality

4. **Enable monitoring**
   - Configure Application Insights in ACA deployment
   - Set up alerts in Azure Monitor

5. **Scale and optimize**
   - Monitor ACA metrics (CPU, memory, request count)
   - Adjust replica settings and resource allocation

6. **Document your deployment**
   - Save your parameter files (without secrets)
   - Document custom settings and decisions

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

For issues, questions, or contributions:
- Check the [troubleshooting guide](../docs/TroubleshootTOC.md)
- Review [configuration documentation](../docs/CONFIGURATION_SETTINGS.md)
- See [advanced deployment scenarios](../docs/ADVANCED_DEVELOPMENT.md)
