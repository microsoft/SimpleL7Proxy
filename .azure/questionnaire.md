# SimpleL7Proxy Deployment Questionnaire

Please answer the following questions to help configure your SimpleL7Proxy deployment:

## Azure Environment
1. **Azure Subscription ID**: 
   - The Azure subscription ID where you want to deploy SimpleL7Proxy

2. **Resource Group Name**:
   - Existing resource group or name for a new one
   - Leave blank to create a new one with the name `rg-<environment>-<app-name>`

3. **Azure Region**:
   - Region for deployment (e.g., westus2, eastus, etc.)
   - Default: `westus2`

4. **Environment Name**:
   - Environment name (e.g., dev, test, prod)
   - Default: `dev`

## Container Configuration
5. **Container Registry**:
   - ACR name (will be created if it doesn't exist)
   - Leave blank to create a new one with the name `acr<environment><appname>`

6. **Container App Name**:
   - Name for the Container App instance
   - Default: `s7p-<environment>`

7. **Container App Environment**:
   - Name for the Container App Environment
   - Leave blank to create a new one with the name `cae-<environment>-<app-name>`

8. **Container App Min Replicas**:
   - Minimum number of replicas
   - Default: `1`

9. **Container App Max Replicas**:
   - Maximum number of replicas
   - Default: `5`

## Network Configuration
10. **VNET Configuration**:
    - Do you want to deploy in a VNET? (yes/no)
    - Default: `no`

11. **VNET Address Space** (if using VNET):
    - VNET address space CIDR
    - Default: `10.0.0.0/16`

12. **Subnet Address Space** (if using VNET):
    - Subnet address space CIDR
    - Default: `10.0.0.0/21`

## Authentication Configuration
13. **Enable Authentication**:
    - Do you want to enable authentication? (yes/no)
    - Default: `no`

14. **Authentication Type** (if enabling auth):
    - Authentication type (entraID, servicebus, none)
    - Default: `none`

## Storage Configuration
15. **Storage Account Name**:
    - Storage account name for blob storage
    - Leave blank to create a new one with the name `st<environment><appname>`

16. **Use Async Storage**:
    - Do you want to enable async storage features? (yes/no)
    - Default: `no`

## Backend Services
17. **Backend Host URLs**:
    - Comma-separated list of backend URLs that the proxy will forward to
    - Example: `https://api1.example.com,https://api2.example.com`

18. **Default Request Timeout**:
    - Default timeout for requests in milliseconds
    - Default: `100000`

## Monitoring Configuration
19. **Enable Application Insights**:
    - Do you want to enable Application Insights? (yes/no)
    - Default: `yes`

20. **Log Analytics Workspace**:
    - Existing Log Analytics workspace or name for a new one
    - Leave blank to create a new one with the name `log-<environment>-<app-name>`

## Advanced Configuration
21. **Queue Priority Levels**:
    - Number of priority levels to configure
    - Default: `3`

22. **Enable Scale Rule**:
    - Do you want to enable auto-scaling based on HTTP concurrent requests? (yes/no)
    - Default: `yes`

23. **HTTP Concurrent Requests Threshold**:
    - Number of concurrent requests that trigger scaling
    - Default: `10`