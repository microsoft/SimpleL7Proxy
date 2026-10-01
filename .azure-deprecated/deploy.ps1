# PowerShell deployment script for SimpleL7Proxy
$ErrorActionPreference = "Stop"

# Get variables from azd environment
$envVars = azd env get-values
$AZURE_CONTAINER_REGISTRY_NAME = $envVars | Where-Object { $_ -match "AZURE_CONTAINER_REGISTRY_NAME=(.+)" } | ForEach-Object { $matches[1] }
$AZURE_CONTAINER_APP_NAME = $envVars | Where-Object { $_ -match "AZURE_CONTAINER_APP_NAME=(.+)" } | ForEach-Object { $matches[1] }
$AZURE_SUBSCRIPTION_ID = $envVars | Where-Object { $_ -match "AZURE_SUBSCRIPTION_ID=(.+)" } | ForEach-Object { $matches[1] }
$AZURE_RESOURCE_GROUP = $envVars | Where-Object { $_ -match "AZURE_RESOURCE_GROUP=(.+)" } | ForEach-Object { $matches[1] }

# Get registry login server
$ACR_LOGIN_SERVER = az acr show --name $AZURE_CONTAINER_REGISTRY_NAME --query "loginServer" --output tsv

# Get version from project
$projectContent = Get-Content -Path "src/SimpleL7Proxy/SimpleL7Proxy.csproj" -Raw
if ($projectContent -match 'AssemblyVersion\("([^"]+)') {
    $VERSION = $matches[1]
} else {
    $VERSION = "1.0.0"
}

Write-Host "Building and pushing Docker image version: $VERSION" -ForegroundColor Green

# Login to Azure Container Registry
Write-Host "Logging in to Azure Container Registry..." -ForegroundColor Yellow
az acr login --name $AZURE_CONTAINER_REGISTRY_NAME

# Build and push Docker image
Write-Host "Building and pushing Docker image..." -ForegroundColor Yellow
docker build -t "${ACR_LOGIN_SERVER}/simple-l7-proxy:${VERSION}" -t "${ACR_LOGIN_SERVER}/simple-l7-proxy:latest" .
docker push "${ACR_LOGIN_SERVER}/simple-l7-proxy:${VERSION}"
docker push "${ACR_LOGIN_SERVER}/simple-l7-proxy:latest"

Write-Host "Updating Container App with new image..." -ForegroundColor Yellow

# Check if we have an environment template to apply
$envTemplatePath = Join-Path (Split-Path -Parent $PSScriptRoot) ".azure/env-templates"
$useEnvTemplate = Read-Host "Do you want to apply an environment template to the deployment? (yes/no) [no]"
if ([string]::IsNullOrEmpty($useEnvTemplate)) { $useEnvTemplate = "no" }

if ($useEnvTemplate -eq "yes") {
    # List available environment templates
    Write-Host "`nAvailable environment templates:" -ForegroundColor Cyan
    $envTemplates = @{
        "1" = @{
            "name" = "Standard Production"
            "file" = "standard-production.env"
            "description" = "A balanced configuration for production workloads with good performance and reasonable resource usage"
        }
        "2" = @{
            "name" = "High Performance"
            "file" = "high-performance.env"
            "description" = "Optimized for maximum throughput and low latency"
        }
        "3" = @{
            "name" = "Cost Optimized"
            "file" = "cost-optimized.env"
            "description" = "Designed to minimize resource usage and cost"
        }
        "4" = @{
            "name" = "High Availability"
            "file" = "high-availability.env"
            "description" = "Maximized for resilience and uptime"
        }
        "5" = @{
            "name" = "Local Development"
            "file" = "local-development.env"
            "description" = "For running with 'dotnet run' on local machine with localhost backends"
        }
        "6" = @{
            "name" = "Container Development"
            "file" = "container-development.env"
            "description" = "For containerized development and testing scenarios"
        }
    }
    
    foreach ($key in $envTemplates.Keys | Sort-Object) {
        Write-Host "$key. $($envTemplates[$key].name) - $($envTemplates[$key].description)" -ForegroundColor Yellow
    }
    
    $envTemplateChoice = Read-Host "`nSelect an environment template (1-$($envTemplates.Count)) [1]"
    if ([string]::IsNullOrEmpty($envTemplateChoice)) { $envTemplateChoice = "1" }
    
    if ($envTemplates.ContainsKey($envTemplateChoice)) {
        $selectedEnvTemplate = $envTemplates[$envTemplateChoice]
        $envTemplateFile = Join-Path $envTemplatePath $selectedEnvTemplate.file
        
        Write-Host "Applying environment template: $($selectedEnvTemplate.name)" -ForegroundColor Green
        
        if (Test-Path $envTemplateFile) {
            # Load environment variables from the file
            $envVarsFromFile = @{}
            Get-Content $envTemplateFile | ForEach-Object {
                if (-not [string]::IsNullOrWhiteSpace($_) -and -not $_.StartsWith('#')) {
                    $parts = $_.Split('=', 2)
                    if ($parts.Length -eq 2) {
                        $key = $parts[0].Trim()
                        $value = $parts[1].Trim()
                        
                        # Remove quotes if present
                        if ($value.StartsWith('"') -and $value.EndsWith('"')) {
                            $value = $value.Substring(1, $value.Length - 2)
                        }
                        
                        $envVarsFromFile[$key] = $value
                    }
                }
            }
            
            # Convert to the format expected by az containerapp update
            $envVarsFormatted = "@("
            foreach ($key in $envVarsFromFile.Keys) {
                $envVarsFormatted += "@{name='$key';value='$($envVarsFromFile[$key])'},"
            }
            $envVarsFormatted = $envVarsFormatted.TrimEnd(',') + ")"
            
            # Update the container app with the new image and environment variables
            $updateCommand = "az containerapp update --name $AZURE_CONTAINER_APP_NAME --resource-group $AZURE_RESOURCE_GROUP --image '${ACR_LOGIN_SERVER}/simple-l7-proxy:${VERSION}' --set-env-vars $envVarsFormatted"
            Invoke-Expression $updateCommand
        }
        else {
            Write-Host "Error: Environment template file not found: $envTemplateFile" -ForegroundColor Red
            # Update only the image
            az containerapp update `
                --name $AZURE_CONTAINER_APP_NAME `
                --resource-group $AZURE_RESOURCE_GROUP `
                --image "${ACR_LOGIN_SERVER}/simple-l7-proxy:${VERSION}"
        }
    }
    else {
        Write-Host "Invalid environment template selection. Updating only the image." -ForegroundColor Yellow
        az containerapp update `
            --name $AZURE_CONTAINER_APP_NAME `
            --resource-group $AZURE_RESOURCE_GROUP `
            --image "${ACR_LOGIN_SERVER}/simple-l7-proxy:${VERSION}"
    }
}
else {
    # Just update the image
    az containerapp update `
        --name $AZURE_CONTAINER_APP_NAME `
        --resource-group $AZURE_RESOURCE_GROUP `
        --image "${ACR_LOGIN_SERVER}/simple-l7-proxy:${VERSION}"
}

Write-Host "Deployment complete!" -ForegroundColor Green
$appUrl = az containerapp show --name $AZURE_CONTAINER_APP_NAME --resource-group $AZURE_RESOURCE_GROUP --query properties.configuration.ingress.fqdn -o tsv
Write-Host "Container App URL: https://$appUrl" -ForegroundColor Cyan