# Proxy Deployment with Healthprobe Sidecar

Deploy a multi-container Azure Container App with a health probe sidecar pattern.

## Prerequisites

- [Azure CLI](https://docs.microsoft.com/en-us/cli/azure/install-azure-cli) installed
- Access to an Azure Container Registry (ACR)
- Docker installed (for building images)

## Quick Start

### 1. Clone and Configure

```bash
git clone https://github.com/microsoft/SimpleL7Proxy.git
cd SimpleL7Proxy
```

> **Note**: This deployment may be on a feature branch. Switch to the appropriate branch if needed:
> ```bash
> git checkout Streaming-Release-TR
> ```

```bash
cd deployment/proxy-with-sidecar

# Copy and edit the parameters file
cp deploy.parameters.example.sh deploy.parameters.sh
```

Edit `deploy.parameters.sh` with your values:

```bash
export ACR="myregistry"                    # Your ACR name
export RESOURCE_GROUP="my-resource-group"  # Azure resource group
export CONTAINER_APP_NAME="my-app"         # Container App name
export ENVIRONMENT_NAME="my-environment"   # Container Apps Environment
export HOST1="host=https://your-api.azure-api.net;mode=apim;path=/;probe=/health"
```

### 2. Build Images

```bash
cd ../../src/SimpleL7Proxy && ./build.sh
cd ../HealthProbe && ./build.sh
```

### 3. Deploy

```bash
cd ../../deployment/proxy-with-sidecar
chmod +x setup.sh deploy.sh
./setup.sh   # First time only - creates Container App and configures ACR access
./deploy.sh  # Deploy (run this for all subsequent deployments)
```

## Configuration

| Parameter | Description |
|-----------|-------------|
| `ACR` | Azure Container Registry name |
| `RESOURCE_GROUP` | Azure resource group |
| `CONTAINER_APP_NAME` | Name of the Container App |
| `ENVIRONMENT_NAME` | Container Apps Environment name |
| `HOST1` | Backend host config: `host=<url>;mode=<mode>;path=<path>;probe=<probe_path>` |
| `WEB_CPU` / `WEB_MEMORY` | Proxy resources (default: 0.5 cores, 1.0 Gi) |
| `HEALTH_CPU` / `HEALTH_MEMORY` | Healthprobe resources (default: 0.25 cores, 0.5 Gi) |
| `INGRESS_TYPE` | `external` or `internal` (default: external) |

> **Note**: Image versions are automatically extracted from `Constants.cs` files.

## Troubleshooting

### View revision status
```bash
az containerapp revision list \
  --name $CONTAINER_APP_NAME \
  --resource-group $RESOURCE_GROUP \
  -o table
```

### View container logs
```bash
# Proxy container logs
az containerapp logs show \
  --name $CONTAINER_APP_NAME \
  --resource-group $RESOURCE_GROUP \
  --container proxy \
  --follow

# Health sidecar logs
az containerapp logs show \
  --name $CONTAINER_APP_NAME \
  --resource-group $RESOURCE_GROUP \
  --container health \
  --follow
```

### Check health probes
```bash
az containerapp show \
  --name $CONTAINER_APP_NAME \
  --resource-group $RESOURCE_GROUP \
  --query "properties.template.containers[].probes"
```

### View replica status
```bash
az containerapp replica list \
  --name $CONTAINER_APP_NAME \
  --resource-group $RESOURCE_GROUP \
  --revision <revision-name> \
  -o table
```
