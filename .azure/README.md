# SimpleL7Proxy Deployment with Azure Developer CLI (AZD)

This guide helps you deploy the SimpleL7Proxy service to Azure using the Azure Developer CLI (AZD).

## Prerequisites

1. [Azure Developer CLI (AZD)](https://learn.microsoft.com/en-us/azure/developer/azure-developer-cli/install-azd)
2. [Azure CLI](https://docs.microsoft.com/en-us/cli/azure/install-azure-cli)
3. [Docker](https://www.docker.com/products/docker-desktop/)

## Deployment Steps

### 1. Clone the Repository

```bash
git clone <repository-url>
cd SimpleL7Proxy
```

### 2. Run the Setup Script

#### For Windows:

```powershell
# Run in PowerShell
.\.azure\setup.ps1
```

#### For Linux/macOS:

```bash
# Make the script executable
chmod +x .azure/setup.sh

# Run the setup script
./.azure/setup.sh
```

The setup script will:
- Verify prerequisites
- Ask you for deployment configuration
- Create an AZD environment with your settings

### 3. Deploy the Infrastructure

```bash
azd provision
```

This command will deploy the Azure infrastructure defined in the Bicep templates:
- Azure Container Registry
- Azure Container App Environment
- Azure Container App
- Storage Account
- Log Analytics Workspace (if enabled)
- Application Insights (if enabled)
- Virtual Network (if enabled)

### 4. Build and Deploy the Application

#### For Windows:

```powershell
# Run in PowerShell
.\.azure\deploy.ps1
```

#### For Linux/macOS:

```bash
# Make the script executable
chmod +x .azure/deploy.sh

# Run the deployment script
./.azure/deploy.sh
```

The deployment script will:
- Build the Docker image
- Push it to Azure Container Registry
- Update the Container App with the new image

Alternatively, you can use the AZD all-in-one command:

```bash
azd up
```

## Configuration Options

For detailed configuration options, see the [questionnaire](questionnaire.md).

Key configuration areas include:
- Azure environment settings
- Container configuration
- Network configuration
- Authentication settings
- Storage configuration
- Backend services
- Monitoring configuration
- Advanced settings

## Deployment Scenarios

To simplify the deployment process, several predefined scenarios are available that configure common deployment patterns:

### Available Scenarios

1. **Local Proxy with Public APIM** - For local development with a public APIM backend
2. **ACA Proxy with Public APIM** - For Azure Container Apps deployment with a public APIM backend
3. **VNET Proxy Deployment** - For secure deployment within a Virtual Network

When running the setup script, you can choose to use one of these predefined scenarios instead of manually configuring all settings.

For more information about scenarios and how to customize them, see the [scenarios README](scenarios/README.md).

## Post-Deployment

After deployment:

1. Access your SimpleL7Proxy service at the URL provided at the end of the deployment
2. Configure additional settings through environment variables in the Azure Portal
3. Monitor your service using Application Insights

## Troubleshooting

If you encounter issues:

1. Check Azure Container App logs in the Azure Portal
2. Verify the Azure Container App configuration
3. Check Application Insights for error logs

## Cleanup

To remove deployed resources:

```bash
azd down
```

## Additional Documentation

For more information about SimpleL7Proxy, refer to the [main documentation](docs/README.md).