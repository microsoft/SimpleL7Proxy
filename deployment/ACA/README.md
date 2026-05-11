# Azure Container Apps (ACA) Deployment

Provisions an Azure Container Apps Environment and deploys SimpleL7Proxy as an internal-only Container App within the VNet created by `deployment/VNet`.

The Container App is configured with:

- **Internal ingress only** — no public IP or endpoint, traffic only from the VNet
- **VNet integration** — the ACA environment uses the ACA subnet from the VNet
- **Managed identity** — system-assigned identity for secure Azure resource access
- **Log Analytics integration** — optional Application Insights and Log Analytics

This folder follows the same deployment convention as `deployment/AppConfiguration` and `deployment/VNet`:

1. Copy `deploy.parameters.example.sh` to `deploy.parameters.sh`
2. Update values
3. Run `./deploy.sh`

## Prerequisites

| Requirement | Details |
|---|---|
| Azure CLI | `az` installed and authenticated |
| Bash | Linux/macOS shell, WSL, or Git Bash |
| VNet deployed | Run `deployment/VNet/deploy.sh` first |
| Container image | Pre-built in ACR or ready to reference |
| Azure permissions | Permission to create ACA environments and Container Apps |

## Quick Start

```bash
cd deployment/ACA

# 1. Create your parameters file
cp deploy.parameters.example.sh deploy.parameters.sh

# 2. Edit deploy.parameters.sh with your values
#    (ensure RESOURCE_GROUP, VNET_NAME, and image names match your VNet deployment)

# 3. Run
./deploy.sh
```

## Parameters

All parameters are set in `deploy.parameters.sh`.

| Parameter | Description |
|---|---|
| `RESOURCE_GROUP` | Must match the VNet resource group |
| `LOCATION` | Azure region (must match VNet) |
| `VNET_NAME` | VNet created by `deployment/VNet` |
| `SUBNET_ACA_NAME` | ACA subnet name from `deployment/VNet` |
| `ACA_ENVIRONMENT_NAME` | ACA environment name |
| `CONTAINER_APP_NAME` | Container App name |
| `REGISTRY_SERVER` | Azure Container Registry domain |
| `IMAGE_NAME` | Full container image URI |
| `CPU` | CPU cores per replica (e.g., `0.5`, `1.0`) |
| `MEMORY` | Memory per replica (e.g., `1.0Gi`, `2.0Gi`) |
| `MIN_REPLICAS` | Minimum replicas |
| `MAX_REPLICAS` | Maximum replicas |
| `INGRESS_VISIBILITY` | `Internal` for VNet-only or `External` for public |
| `INGRESS_PORT` | Listening port (default 8000) |
| `BACKEND_HOST` | Backend host connection string |
| `ENABLE_MANAGED_IDENTITY` | `true` to enable system-assigned identity |
| `ENABLE_APP_INSIGHTS` | `true` to enable Application Insights |
| `LOG_ANALYTICS_WORKSPACE_NAME` | Log Analytics workspace name |

> Do not commit `deploy.parameters.sh` with environment-specific values.

## What `deploy.sh` Does

1. Loads values from `deploy.parameters.sh`
2. Verifies Azure CLI and login
3. Retrieves the ACA subnet ID from the VNet
4. Creates Log Analytics workspace if enabled
5. Creates or reuses the Container Apps Environment, integrating it with the VNet
6. Creates or updates the Container App with the specified image and configuration
7. Enables system-assigned managed identity if requested
8. Prints a summary of the deployment

## Network Integration

The ACA environment is deployed into the ACA subnet from the VNet. This ensures:

- All traffic stays within the VNet
- Private DNS can be used for service discovery
- No public ingress endpoint is created (when `INGRESS_VISIBILITY=Internal`)
- Network policies and security groups can be applied

## Internal-Only Access

When `INGRESS_VISIBILITY=Internal`:

- The Container App is accessible only from within the VNet
- A private, internal FQDN is assigned (e.g., `ca-myapp-proxy.internal.region.azurecontainerapps.io`)
- External clients cannot reach the app without a bastion, client VM, or private network connection

Use DNS (see `deployment/DNS`) to register this internal FQDN for client discovery.

## Idempotency

The script is idempotent. Re-running it updates the Container App image, CPU, memory, and replica settings to match current parameter values.

## Next Steps

After deploying ACA:

1. Run `deployment/AppConfiguration/deploy.sh` to configure proxy settings
2. Run `deployment/DNS/deploy.sh` to register the internal FQDN in private DNS
3. Test connectivity from a client VM in the `ClientVM` subnet
