# Azure VNet Deployment

Provisions a new Azure Virtual Network and five subnets used by the SimpleL7Proxy deployment topology:

- ACA
- ClientVM
- AzureFunctions
- APIM
- PrivateEndpoints

This folder follows the same deployment convention as `deployment/AppConfiguration`:

1. Copy `deploy.parameters.example.sh` to `deploy.parameters.sh`
2. Update values
3. Run `./deploy.sh`

## Prerequisites

| Requirement | Details |
|---|---|
| Azure CLI | `az` installed and authenticated |
| Bash | Linux/macOS shell, WSL, or Git Bash |
| Azure permissions | Permission to create/update VNets and subnets in the target resource group |

## Quick Start

```bash
cd deployment/VNet

# 1. Create your parameters file
cp deploy.parameters.example.sh deploy.parameters.sh

# 2. Edit deploy.parameters.sh with your values

# 3. Run
./deploy.sh
```

## Parameters

All parameters are set in `deploy.parameters.sh`.

| Parameter | Description |
|---|---|
| `RESOURCE_GROUP` | Resource group for the VNet (created if missing) |
| `LOCATION` | Azure region |
| `VNET_NAME` | VNet name |
| `VNET_ADDRESS_PREFIX` | VNet CIDR range |
| `SUBNET_ACA_NAME` / `SUBNET_ACA_PREFIX` | ACA subnet name and CIDR |
| `SUBNET_CLIENTVM_NAME` / `SUBNET_CLIENTVM_PREFIX` | Client VM subnet name and CIDR |
| `SUBNET_AZUREFUNCTIONS_NAME` / `SUBNET_AZUREFUNCTIONS_PREFIX` | Azure Functions subnet name and CIDR |
| `SUBNET_APIM_NAME` / `SUBNET_APIM_PREFIX` | APIM subnet name and CIDR |
| `SUBNET_PRIVATEENDPOINTS_NAME` / `SUBNET_PRIVATEENDPOINTS_PREFIX` | Private Endpoints subnet name and CIDR |
| `DISABLE_PRIVATE_ENDPOINT_NETWORK_POLICIES` | `true` to disable private endpoint network policies on the private endpoints subnet |

> Do not commit `deploy.parameters.sh` with environment-specific values.

## Subnet Purpose

### ACA subnet

Hosts Azure Container Apps infrastructure and app workloads. Keep this subnet sized for scaling headroom.

### ClientVM subnet

Hosts jumpbox/test VMs that call the proxy privately for validation and operations.

### AzureFunctions subnet

Reserved subnet for Azure Functions integration scenarios and private networking.

### APIM subnet

Dedicated subnet for API Management networking and private routing patterns.

### PrivateEndpoints subnet

Used for private endpoints (for example Storage, App Configuration, or Key Vault) to keep service traffic on private IPs.

## What `deploy.sh` Does

1. Loads values from `deploy.parameters.sh`
2. Verifies Azure CLI and login
3. Creates or reuses the resource group
4. Creates or updates the VNet address space
5. Creates or updates each required subnet
6. Optionally disables private endpoint network policies on the `PrivateEndpoints` subnet
7. Prints a summary of deployed network ranges

## Idempotency

The script is idempotent. Re-running it updates the VNet/subnet address prefixes to match current parameter values.
