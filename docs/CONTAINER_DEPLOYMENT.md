# Container Deployment

This document provides comprehensive instructions for running SimpleL7Proxy as a container, including local Docker deployment and Azure Container Apps deployment.

## Azure Container Apps Deployment with AZD

SimpleL7Proxy can be deployed to Azure Container Apps using the Azure Developer CLI (AZD), providing a streamlined deployment experience with predefined scenarios and environment templates.

### Prerequisites

- [Azure CLI](https://docs.microsoft.com/en-us/cli/azure/install-azure-cli)
- [Azure Developer CLI](https://learn.microsoft.com/en-us/azure/developer/azure-developer-cli/install-azd)
- [Docker](https://docs.docker.com/get-docker/)

### Deployment Steps

#### 1. Run the Setup Script

**Windows**
```powershell
./.azure/setup.ps1
```

**Linux/macOS**
```bash
chmod +x ./.azure/setup.sh
./.azure/setup.sh
```

#### 2. Configure Deployment

The setup script guides you through the configuration process:

1. **Select a Deployment Scenario**:
   - **Local proxy with public APIM**: Run the proxy locally while connecting to a public APIM instance
   - **ACA proxy with public APIM**: Deploy the proxy as an Azure Container App connecting to a public APIM instance
   - **VNET proxy deployment**: Deploy the proxy within a Virtual Network for enhanced security

2. **Choose an Environment Template** (optional):
   - **Standard Production**: Balanced configuration for most production workloads
   - **High Performance**: Optimized for maximum throughput and low latency
   - **Cost Optimized**: Designed to minimize resource usage and cost
   - **High Availability**: Maximized for resilience and uptime
   - **Development**: Configured for development and testing with additional debug information

3. Configure additional Azure resources as prompted.

#### 3. Provision Infrastructure

```bash
azd provision
```

#### 4. Deploy the Application

**Windows**
```powershell
./.azure/deploy.ps1
```

**Linux/macOS**
```bash
chmod +x ./.azure/deploy.sh
./.azure/deploy.sh
```

During deployment, you can apply an environment template if you didn't do so during setup.

### Environment Templates

SimpleL7Proxy provides predefined environment variable templates in the `.azure/env-templates/` directory. Each template is optimized for specific operational needs:

- **Standard Production**: Balanced performance and cost with multiple priority levels
- **High Performance**: Maximum concurrency and optimized settings for throughput
- **Cost Optimized**: Reduced worker counts and minimal resource usage
- **High Availability**: Multiple backend hosts with aggressive failover settings
- **Development**: Debug logging and extended timeouts for testing

For more details on environment variables, see [ENVIRONMENT_VARIABLES.md](ENVIRONMENT_VARIABLES.md).

## Running as a Docker Container

### Prerequisites
- Cloned repository
- Docker installed and running
- Basic understanding of Docker commands

### Building the Container

```bash
# Navigate to the project directory
cd SimpleL7Proxy

# Build the Docker image
docker build -t simplel7proxy -f Dockerfile .
```

### Running the Container Locally

#### Basic Configuration
```bash
# Run with minimal configuration
docker run -p 8000:443 \
  -e "Host1=https://localhost:3000" \
  -e "Host2=http://localhost:5000" \
  -e "Timeout=2000" \
  -e "LogAllRequestHeaders=true" \
  -e "Workers=15" \
  simplel7proxy
```

#### Production Configuration
```bash
# Run with production settings
docker run -p 8000:443 \
  -e "Host1=https://api1.example.com" \
  -e "Host2=https://api2.example.com" \
  -e "Host3=https://api3.example.com" \
  -e "Probe_path1=/health" \
  -e "Probe_path2=/health" \
  -e "Probe_path3=/health" \
  -e "Workers=20" \
  -e "MaxQueueLength=50" \
  -e "PollInterval=10000" \
  -e "Timeout=5000" \
  -e "LogAllRequestHeaders=false" \
  -e "APPINSIGHTS_CONNECTIONSTRING=your-connection-string" \
  simplel7proxy
```

#### With Environment File
Create a `.env` file:
```bash
# .env file
Host1=https://api1.example.com
Host2=https://api2.example.com
Workers=20
MaxQueueLength=50
LogAllRequestHeaders=true
APPINSIGHTS_CONNECTIONSTRING=your-connection-string
```

Run with environment file:
```bash
docker run -p 8000:443 --env-file .env simplel7proxy
```

### Docker Compose Setup

Create a `docker-compose.yml` file:

```yaml
version: '3.8'

services:
  proxy:
    build: .
    ports:
      - "8000:443"
    environment:
      - Host1=https://api1.example.com
      - Host2=https://api2.example.com
      - Probe_path1=/health
      - Probe_path2=/health
      - Workers=20
      - MaxQueueLength=50
      - LogAllRequestHeaders=true
    restart: unless-stopped
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:443/health"]
      interval: 30s
      timeout: 10s
      retries: 3

  # Example backend services for testing
  backend1:
    image: nginx:alpine
    ports:
      - "3000:80"
    
  backend2:
    image: nginx:alpine
    ports:
      - "5000:80"
```

Run with Docker Compose:
```bash
docker-compose up -d
```

## Deploy to Azure Container Apps

### Prerequisites
- Cloned repository
- Docker installed
- Azure CLI installed and logged in
- Azure subscription with appropriate permissions

### Step 1: Set Environment Variables

```bash
# Configure your deployment settings
export ACR=<your-acr-name>              # Without .azurecr.io
export GROUP=simplel7proxyg
export ACENV=simplel7proxyenv
export ACANAME=simplel7proxy
export LOCATION=eastus
```

### Step 2: Build and Push to Azure Container Registry

```bash
# Login to your Azure Container Registry
az acr login --name $ACR.azurecr.io

# Build and tag the image
docker build -t $ACR.azurecr.io/simplel7proxy:v1 -f Dockerfile .

# Push the image to ACR
docker push $ACR.azurecr.io/simplel7proxy:v1
```

### Step 3: Get ACR Credentials

```bash
# Retrieve ACR credentials
ACR_CREDENTIALS=$(az acr credential show --name $ACR)
export ACR_USERNAME=$(echo $ACR_CREDENTIALS | jq -r '.username')
export ACR_PASSWORD=$(echo $ACR_CREDENTIALS | jq -r '.passwords[0].value')
```

### Step 4: Create Azure Resources

```bash
# Create resource group
az group create --name $GROUP --location $LOCATION

# Create Container Apps environment
az containerapp env create \
  --name $ACENV \
  --resource-group $GROUP \
  --location $LOCATION
```

### Step 5: Deploy Container App

#### Basic Deployment
```bash
az containerapp create \
  --name $ACANAME \
  --resource-group $GROUP \
  --environment $ACENV \
  --image $ACR.azurecr.io/simplel7proxy:v1 \
  --target-port 443 \
  --ingress external \
  --registry-server $ACR.azurecr.io \
  --registry-username $ACR_USERNAME \
  --registry-password $ACR_PASSWORD \
  --env-vars \
    Host1=https://api1.example.com \
    Host2=https://api2.example.com \
    Workers=15 \
    MaxQueueLength=30 \
    LogAllRequestHeaders=true \
  --query properties.configuration.ingress.fqdn
```

#### Production Deployment with Scaling
```bash
az containerapp create \
  --name $ACANAME \
  --resource-group $GROUP \
  --environment $ACENV \
  --image $ACR.azurecr.io/simplel7proxy:v1 \
  --target-port 443 \
  --ingress external \
  --registry-server $ACR.azurecr.io \
  --registry-username $ACR_USERNAME \
  --registry-password $ACR_PASSWORD \
  --min-replicas 2 \
  --max-replicas 10 \
  --cpu 1.0 \
  --memory 2Gi \
  --env-vars \
    Host1=https://api1.example.com \
    Host2=https://api2.example.com \
    Host3=https://api3.example.com \
    Probe_path1=/health \
    Probe_path2=/health \
    Probe_path3=/health \
    Workers=20 \
    MaxQueueLength=50 \
    PollInterval=10000 \
    Timeout=5000 \
    APPINSIGHTS_CONNECTIONSTRING="your-connection-string" \
  --query properties.configuration.ingress.fqdn
```

### Step 6: Configure Custom Domain (Optional)

```bash
# Add custom domain
az containerapp hostname add \
  --hostname "proxy.yourdomain.com" \
  --name $ACANAME \
  --resource-group $GROUP

# Bind SSL certificate
az containerapp ssl upload \
  --hostname "proxy.yourdomain.com" \
  --name $ACANAME \
  --resource-group $GROUP \
  --certificate-file "path/to/certificate.pfx" \
  --password "certificate-password"
```

## Container Configuration Best Practices

### Environment Variables
- Use Azure Key Vault references for sensitive data
- Set appropriate resource limits based on expected load
- Enable health checks for better reliability

### Logging
- Configure Application Insights for production monitoring
- Use structured logging for better observability
- Set appropriate log levels to avoid noise

### Security
- Use managed identity when possible
- Limit container permissions
- Regular security updates for base images

### Performance
- Set appropriate CPU and memory limits
- Configure auto-scaling based on metrics
- Use multiple replicas for high availability

## Monitoring and Troubleshooting

### View Container Logs
```bash
# View real-time logs
az containerapp logs show \
  --name $ACANAME \
  --resource-group $GROUP \
  --follow

# View recent logs
az containerapp logs show \
  --name $ACANAME \
  --resource-group $GROUP \
  --tail 100
```

### Check Container Status
```bash
# Get container app details
az containerapp show \
  --name $ACANAME \
  --resource-group $GROUP \
  --query properties.configuration.ingress.fqdn

# Check revision status
az containerapp revision list \
  --name $ACANAME \
  --resource-group $GROUP
```

## Deploy to Container Apps via a GitHub Action

You can create a GitHub workflow to deploy this code to an Azure container app. You can follow the step by step instruction from a similar project in the following video:

[![Video Title](https://i.ytimg.com/vi/-KojzBMM2ic/hqdefault.jpg)](https://www.youtube.com/watch?v=-KojzBMM2ic "How to Create a Github Action to Deploy to Azure Container Apps")


### Common Issues and Solutions

1. **Container fails to start**: Check environment variables and image availability
2. **503 Service Unavailable**: Verify backend hosts are accessible from container
3. **SSL/TLS issues**: Ensure proper certificate configuration
4. **High memory usage**: Adjust worker count and queue length settings

## Updating the Container

### Rolling Update
```bash
# Build new version
docker build -t $ACR.azurecr.io/simplel7proxy:v2 -f Dockerfile .
docker push $ACR.azurecr.io/simplel7proxy:v2

# Update container app
az containerapp update \
  --name $ACANAME \
  --resource-group $GROUP \
  --image $ACR.azurecr.io/simplel7proxy:v2
```

### Blue-Green Deployment
```bash
# Create new revision without traffic
az containerapp revision copy \
  --name $ACANAME \
  --resource-group $GROUP \
  --from-revision latest \
  --image $ACR.azurecr.io/simplel7proxy:v2

# Gradually shift traffic
az containerapp ingress traffic set \
  --name $ACANAME \
  --resource-group $GROUP \
  --revision-weight latest=50 previous=50

# Complete the switch
az containerapp ingress traffic set \
  --name $ACANAME \
  --resource-group $GROUP \
  --revision-weight latest=100
```
