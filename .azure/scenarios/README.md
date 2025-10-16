# SimpleL7Proxy Deployment Scenarios

This directory contains predefined environment variable files for different deployment scenarios of the SimpleL7Proxy service. These scenarios can be selected during setup to quickly configure the deployment for common use cases.

## Available Scenarios

### 1. Local Proxy with Public APIM
**File:** `local-proxy-public-apim.env`

This scenario is designed for local development and testing where:
- SimpleL7Proxy runs locally on a developer machine
- Backend services are accessed via a public Azure API Management (APIM) instance
- Minimal Azure resources are created
- Local development environment settings are configured

### 2. Azure Container App with Public APIM
**File:** `aca-proxy-public-apim.env`

This scenario deploys SimpleL7Proxy as an Azure Container App (ACA) where:
- SimpleL7Proxy runs as a managed container in Azure Container Apps
- The container app connects to a public APIM instance
- Full Azure monitoring is enabled
- Auto-scaling is configured based on traffic

### 3. VNET Proxy Deployment
**File:** `vnet-proxy-deployment.env`

This scenario deploys SimpleL7Proxy within a Virtual Network for enhanced security:
- SimpleL7Proxy runs in a private Virtual Network (VNET)
- Network security is enhanced with private endpoints
- Advanced authentication options are enabled
- Higher scalability settings are configured

## Using Scenarios

When running the setup script (either `setup.ps1` or `setup.sh`), you will be asked if you want to use a predefined scenario. If you select "yes", you'll be presented with the list of available scenarios.

### Customizing Scenarios

You can customize any scenario by:

1. Creating a copy of an existing scenario file
2. Modifying the environment variables in the file
3. Placing the file in this directory with a descriptive name

For example:
```
cp aca-proxy-public-apim.env custom-high-performance.env
# Edit custom-high-performance.env to adjust settings
```

## Environment Variable Structure

Each scenario file contains environment variables grouped by category:

- **Azure Environment** - Basic Azure deployment settings
- **Container Configuration** - Container App and Registry settings
- **Network Configuration** - VNET and networking settings
- **Authentication Configuration** - Auth settings and security
- **Storage Configuration** - Storage account and async storage settings
- **Backend Services** - Backend URLs and timeout configurations
- **Monitoring Configuration** - App Insights and Log Analytics settings
- **Advanced Configuration** - Queue priorities, scaling rules, etc.

## Creating New Scenarios

To create a new scenario:

1. Create a new `.env` file in this directory
2. Follow the structure of the existing scenario files
3. Include all necessary environment variables
4. Add descriptive comments at the top of the file

The setup scripts will automatically detect and use your new scenario file.