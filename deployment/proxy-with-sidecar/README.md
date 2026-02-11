# HealthProbe Container App Deployment

This directory contains the infrastructure as code for deploying a multi-container Azure Container App with a health probe sidecar pattern.

## Files

- **script.bicep** - Main Bicep template defining the Container App with web and health sidecar containers
- **setup.sh** - One-time setup script to create the Container App and configure ACR permissions
- **deploy.sh** - Bash script to deploy/update the Container App using Azure CLI
- **deploy.parameters.example.sh** - Example configuration file with deployment parameters

## Prerequisites

1. Azure CLI installed and logged in
   ```bash
   az login
   ```

2. Set your Azure subscription
   ```bash
   az account set --subscription "your-subscription-id"
   ```

3. Create a resource group (if it doesn't exist)
   ```bash
   az group create --name your-resource-group --location eastus
   ```

4. Container images built and pushed to a registry (see [Building Container Images](#building-container-images))

## Building Container Images

Both container images have build scripts that extract the version from `Constants.cs` and push to Azure Container Registry.

### Set Your ACR

```bash
export ACR="myregistry"
az acr login --name $ACR
```

### Build Both proxy and healthprobe Images

```bash
# From the repository root
cd  ../../src/SimpleL7Proxy && ./build.sh && cd ../HealthProbe && ./build.sh
```

This builds and pushes:
- `myregistry.azurecr.io/myproxy:<version>` - the proxy
- `myregistry.azurecr.io/healthprobe:<version>` - the health sidecar

### Update Parameters for Deployment

After building, update your `deploy.parameters.sh` to use the built images:

```bash
export WEB_IMAGE="$ACR.azurecr.io/myproxy:<version>"
export HEALTH_IMAGE="$ACR.azurecr.io/healthprobe:<version>"
export REGISTRY_SERVER="$ACR.azurecr.io"
```

> **Tip**: The version is extracted from `Constants.cs` in each project. Check the build output for the exact version tag.

## Quick Start

### First-Time Setup

For new deployments, run the setup script first to create the Container App and configure ACR permissions:

1. Copy the example parameters file:
   ```bash
   cp deploy.parameters.example.sh deploy.parameters.sh
   ```

2. Edit `deploy.parameters.sh` with your actual values (especially `WEB_IMAGE`, `HEALTH_IMAGE`, and `REGISTRY_SERVER`)

3. Add to .gitignore (to avoid committing secrets):
   ```bash
   echo "deploy.parameters.sh" >> .gitignore
   ```

4. Run the setup script (one-time only):
   ```bash
   chmod +x setup.sh deploy.sh
   ./setup.sh
   ```

   This creates the Container App with managed identity and grants ACR pull permissions.

5. Deploy your actual images:
   ```bash
   ./deploy.sh
   ```

### Subsequent Deployments

After the initial setup, just run:
```bash
./deploy.sh
```

> **Note**: `deploy.sh` automatically sources `deploy.parameters.sh` if it exists in the same directory.

## Configuration

### Container Images

Update the image references to point to your container registry:
- **WEB_IMAGE**: Your main application container
- **HEALTH_IMAGE**: Your health probe sidecar container

### Resource Allocation

Adjust CPU and memory based on your needs:
- **WEB_CPU**: 0.25 - 4.0 cores (default: 0.5)
- **WEB_MEMORY**: 0.5 - 8.0 Gi (default: 1.0)
- **HEALTH_CPU**: 0.25 - 2.0 cores (default: 0.25)
- **HEALTH_MEMORY**: 0.5 - 4.0 Gi (default: 0.5)

### Network Configuration

- **WEB_PORT**: Port where web container listens (default: 8000)
- **HEALTH_PORT**: Port where health sidecar listens (default: 9000)
- **INGRESS_TYPE**: `external` (public internet) or `internal` (VNET only)
- **ENABLE_HTTPS**: `true` or `false`
- **REVISION_MODE**: `single` (recommended for sidecars) or `multiple`

## Private Registry (Azure Container Registry)

The deployment uses **system-assigned managed identity** to pull images from ACR. No username/password required!

1. Set the registry server in `deploy.parameters.sh`:
   ```bash
   export REGISTRY_SERVER="myregistry.azurecr.io"
   ```

2. The deploy script automatically:
   - Creates the Container App with a system-assigned managed identity
   - Grants the `AcrPull` role to the managed identity on your ACR

> **Note**: For first-time deployments, the role assignment happens after the Container App is created.
> If the initial deployment fails due to image pull errors, simply run the script again.

### Manual Role Assignment (if needed)

If you need to manually assign the ACR role:
```bash
# Get the Container App's managed identity principal ID
PRINCIPAL_ID=$(az containerapp show --name your-app --resource-group your-rg --query "identity.principalId" -o tsv)

# Get the ACR resource ID
ACR_ID=$(az acr show --name myregistry --query id -o tsv)

# Assign AcrPull role
az role assignment create --assignee $PRINCIPAL_ID --role AcrPull --scope $ACR_ID
```

## Deployment Outputs

After successful deployment, the script outputs:
- **FQDN**: The fully qualified domain name to access your app
- **Resource ID**: Azure resource ID of the Container App
- **Latest Revision**: Name of the deployed revision

## Monitoring

View logs for your containers:
```bash
# Web container logs
az containerapp logs show \
  --name healthprobe-app \
  --resource-group your-resource-group \
  --container web \
  --follow

# Health sidecar logs
az containerapp logs show \
  --name healthprobe-app \
  --resource-group your-resource-group \
  --container health \
  --follow
```

## Troubleshooting

### View revision status
```bash
az containerapp revision list \
  --name healthprobe-app \
  --resource-group your-resource-group \
  -o table
```

### Check health probes
```bash
az containerapp show \
  --name healthprobe-app \
  --resource-group your-resource-group \
  --query "properties.template.containers[].probes"
```

### View replica status
```bash
az containerapp replica list \
  --name healthprobe-app \
  --resource-group your-resource-group \
  --revision <revision-name> \
  -o table
```

## Clean Up

To delete the Container App:
```bash
az containerapp delete \
  --name healthprobe-app \
  --resource-group your-resource-group \
  --yes
```

To delete the entire resource group:
```bash
az group delete --name your-resource-group --yes
```
