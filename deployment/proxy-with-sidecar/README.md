# HealthProbe Container App Deployment

This directory contains the infrastructure as code for deploying a multi-container Azure Container App with a health probe sidecar pattern.

## Files

- **script.bicep** - Main Bicep template defining the Container App with web and health sidecar containers
- **deploy.sh** - Bash script to deploy the Container App using Azure CLI
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

4. Container images built and pushed to a registry

## Quick Start

### Option 1: Edit deploy.sh directly

1. Open `deploy.sh` and update the configuration variables at the top:
   - `RESOURCE_GROUP`
   - `CONTAINER_APP_NAME`
   - `WEB_IMAGE` and `HEALTH_IMAGE`
   - Registry credentials (if using private registry)

2. Make the script executable:
   ```bash
   chmod +x deploy.sh
   ```

3. Run the deployment:
   ```bash
   ./deploy.sh
   ```

### Option 2: Use parameters file

1. Copy the example parameters file:
   ```bash
   cp deploy.parameters.example.sh deploy.parameters.sh
   ```

2. Edit `deploy.parameters.sh` with your actual values

3. Add to .gitignore (to avoid committing secrets):
   ```bash
   echo "deploy.parameters.sh" >> .gitignore
   ```

4. Source the parameters and deploy:
   ```bash
   source deploy.parameters.sh && ./deploy.sh
   ```

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

## Private Registry

If using Azure Container Registry or another private registry:

1. Set registry credentials:
   ```bash
   REGISTRY_SERVER="myregistry.azurecr.io"
   REGISTRY_USERNAME="myregistry"
   REGISTRY_PASSWORD="your-password"
   ```

2. Or use Azure CLI to get ACR credentials:
   ```bash
   az acr credential show --name myregistry --query "passwords[0].value" -o tsv
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
