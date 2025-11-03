# SimpleL7Proxy AZD Deployment Script (PowerShell)
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "  SimpleL7Proxy AZD Deployment Setup" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan

# Function to read user input with a default value
function Read-UserInput {
    param (
        [string]$prompt,
        [string]$default
    )
    
    if ($default) {
        $prompt = "$prompt [$default]"
    }
    
    $input = Read-Host -Prompt $prompt
    
    if ([string]::IsNullOrEmpty($input) -and $default) {
        $input = $default
    }
    
    return $input
}

# Function to load environment variables from a file
function Load-EnvFile {
    param (
        [string]$filePath
    )
    
    $envVars = @{}
    
    if (Test-Path $filePath) {
        $content = Get-Content $filePath
        
        foreach ($line in $content) {
            # Skip comments and empty lines
            if ($line.Trim() -eq "" -or $line.Trim().StartsWith("#")) {
                continue
            }
            
            $parts = $line.Split("=", 2)
            if ($parts.Length -eq 2) {
                $key = $parts[0].Trim()
                $value = $parts[1].Trim()
                
                # Remove quotes if present
                if ($value.StartsWith('"') -and $value.EndsWith('"')) {
                    $value = $value.Substring(1, $value.Length - 2)
                }
                
                $envVars[$key] = $value
            }
        }
    }
    
    return $envVars
}

# Check if AZD is installed
if (-not (Get-Command azd -ErrorAction SilentlyContinue)) {
    Write-Host "Azure Developer CLI (AZD) is not installed." -ForegroundColor Red
    Write-Host "Please install it from https://learn.microsoft.com/en-us/azure/developer/azure-developer-cli/install-azd" -ForegroundColor Red
    exit 1
}

# Check if Azure CLI is installed
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Write-Host "Azure CLI is not installed." -ForegroundColor Red
    Write-Host "Please install it from https://docs.microsoft.com/en-us/cli/azure/install-azure-cli" -ForegroundColor Red
    exit 1
}

# Check if user is logged in to Azure
Write-Host "Checking Azure login status..." -ForegroundColor Green
$loginStatus = az account show 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "You are not logged in to Azure. Please login first." -ForegroundColor Yellow
    az login
}

# Display available subscriptions
Write-Host "Available Azure Subscriptions:" -ForegroundColor Green
az account list --query "[].{Name:name, SubscriptionId:id}" --output table

# Check for deployment scenarios
$scenarioPath = Join-Path $PSScriptRoot "scenarios"
$useScenario = Read-UserInput "Would you like to use a predefined deployment scenario? (yes/no)" "yes"

if ($useScenario -eq "yes") {
    # List available scenarios
    Write-Host "`nAvailable deployment scenarios:" -ForegroundColor Cyan
    $scenarios = @{
        "1" = @{
            "name" = "Local proxy with public APIM"
            "file" = "local-proxy-public-apim.env"
            "description" = "Run the proxy locally while connecting to a public Azure API Management instance"
        }
        "2" = @{
            "name" = "ACA proxy with public APIM"
            "file" = "aca-proxy-public-apim.env" 
            "description" = "Deploy proxy as Azure Container App connecting to a public APIM instance"
        }
        "3" = @{
            "name" = "VNET proxy deployment"
            "file" = "vnet-proxy-deployment.env"
            "description" = "Deploy proxy within a Virtual Network for enhanced security"
        }
    }
    
    foreach ($key in $scenarios.Keys | Sort-Object) {
        Write-Host "$key. $($scenarios[$key].name) - $($scenarios[$key].description)" -ForegroundColor Yellow
    }
    
    $scenarioChoice = Read-UserInput "`nSelect a scenario (1-$($scenarios.Count))" "2"
    
    if ($scenarios.ContainsKey($scenarioChoice)) {
        $selectedScenario = $scenarios[$scenarioChoice]
        $envFile = Join-Path $scenarioPath $selectedScenario.file
        
        Write-Host "Loading scenario: $($selectedScenario.name)" -ForegroundColor Green
        
        if (Test-Path $envFile) {
            $envVars = Load-EnvFile $envFile
            
            # Override with scenario values
            $environment = if ($envVars.ContainsKey("ENVIRONMENT_NAME")) { $envVars["ENVIRONMENT_NAME"] } else { "dev" }
            $region = if ($envVars.ContainsKey("AZURE_LOCATION")) { $envVars["AZURE_LOCATION"] } else { "westus2" }
        }
        else {
            Write-Host "Error: Scenario file not found: $envFile" -ForegroundColor Red
            exit 1
        }
    }
    else {
        Write-Host "Invalid scenario selection. Using manual configuration." -ForegroundColor Yellow
    }
}

# Check for environment template
$envTemplatePath = Join-Path $PSScriptRoot "env-templates"
$useEnvTemplate = Read-UserInput "Would you like to use a predefined environment template for container app settings? (yes/no)" "no"

$containerEnvVars = @{}
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
    
    $envTemplateChoice = Read-UserInput "`nSelect an environment template (1-$($envTemplates.Count))" "1"
    
    if ($envTemplates.ContainsKey($envTemplateChoice)) {
        $selectedEnvTemplate = $envTemplates[$envTemplateChoice]
        $envTemplateFile = Join-Path $envTemplatePath $selectedEnvTemplate.file
        
        Write-Host "Loading environment template: $($selectedEnvTemplate.name)" -ForegroundColor Green
        
        if (Test-Path $envTemplateFile) {
            $containerEnvVars = Load-EnvFile $envTemplateFile
            Write-Host "Loaded $($containerEnvVars.Count) container environment variables." -ForegroundColor Green
        }
        else {
            Write-Host "Error: Environment template file not found: $envTemplateFile" -ForegroundColor Red
        }
    }
    else {
        Write-Host "Invalid environment template selection. No template will be applied." -ForegroundColor Yellow
    }
}

# Get Azure subscription (even if using a scenario)
$subscription = Read-UserInput "Enter your Azure Subscription ID" ""
if ($subscription) {
    az account set --subscription "$subscription"
}

# Get environment name (if not loaded from scenario)
if (-not $environment) {
    $environment = Read-UserInput "Enter environment name (dev, test, prod)" "dev"
}

# Get region (if not loaded from scenario)
if (-not $region) {
    $region = Read-UserInput "Enter Azure region for deployment" "westus2"
}

# Use scenario values if available, otherwise prompt for inputs
$usingScenario = $useScenario -eq "yes" -and $envVars -ne $null

# Resource group
$default_rg = if ($usingScenario -and $envVars.ContainsKey("AZURE_RESOURCE_GROUP")) { $envVars["AZURE_RESOURCE_GROUP"] } else { "" }
$resource_group = Read-UserInput "Enter resource group name or leave blank for default" $default_rg
if ([string]::IsNullOrEmpty($resource_group)) {
    $resource_group = "rg-${environment}-s7p"
}

# Container App name
$default_app_name = if ($usingScenario -and $envVars.ContainsKey("AZURE_CONTAINER_APP_NAME")) { $envVars["AZURE_CONTAINER_APP_NAME"] } else { "s7p-${environment}" }
$container_app_name = Read-UserInput "Enter Container App name" $default_app_name

# Container registry
$default_acr = if ($usingScenario -and $envVars.ContainsKey("AZURE_CONTAINER_REGISTRY_NAME")) { $envVars["AZURE_CONTAINER_REGISTRY_NAME"] } else { "" }
$container_registry = Read-UserInput "Enter container registry name or leave blank for default" $default_acr
if ([string]::IsNullOrEmpty($container_registry)) {
    $container_registry = "acr${environment}s7p"
}

# Container App Environment
$default_env = if ($usingScenario -and $envVars.ContainsKey("AZURE_CONTAINER_APP_ENVIRONMENT_NAME")) { $envVars["AZURE_CONTAINER_APP_ENVIRONMENT_NAME"] } else { "" }
$container_app_env = Read-UserInput "Enter Container App Environment name or leave blank for default" $default_env
if ([string]::IsNullOrEmpty($container_app_env)) {
    $container_app_env = "cae-${environment}-s7p"
}

# Replicas
$default_min = if ($usingScenario -and $envVars.ContainsKey("AZURE_CONTAINER_APP_MIN_REPLICAS")) { $envVars["AZURE_CONTAINER_APP_MIN_REPLICAS"] } else { "1" }
$default_max = if ($usingScenario -and $envVars.ContainsKey("AZURE_CONTAINER_APP_MAX_REPLICAS")) { $envVars["AZURE_CONTAINER_APP_MAX_REPLICAS"] } else { "5" }
$min_replicas = Read-UserInput "Enter minimum number of replicas" $default_min
$max_replicas = Read-UserInput "Enter maximum number of replicas" $default_max

# VNET Configuration
$default_vnet = if ($usingScenario -and $envVars.ContainsKey("USE_VNET")) { $envVars["USE_VNET"] } else { "no" }
$use_vnet = Read-UserInput "Do you want to deploy in a VNET? (yes/no)" $default_vnet
$vnet_prefix = ""
$subnet_prefix = ""
if ($use_vnet -eq "yes" -or $use_vnet -eq "true") {
    $use_vnet = "true"  # Normalize to "true" for bicep
    $default_vnet_prefix = if ($usingScenario -and $envVars.ContainsKey("VNET_ADDRESS_PREFIX")) { $envVars["VNET_ADDRESS_PREFIX"] } else { "10.0.0.0/16" }
    $default_subnet_prefix = if ($usingScenario -and $envVars.ContainsKey("SUBNET_ADDRESS_PREFIX")) { $envVars["SUBNET_ADDRESS_PREFIX"] } else { "10.0.0.0/21" }
    $vnet_prefix = Read-UserInput "Enter VNET address space" $default_vnet_prefix
    $subnet_prefix = Read-UserInput "Enter subnet address space" $default_subnet_prefix
}
else {
    $use_vnet = "false"  # Normalize to "false" for bicep
}

# Authentication
$default_auth = if ($usingScenario -and $envVars.ContainsKey("ENABLE_AUTHENTICATION")) { $envVars["ENABLE_AUTHENTICATION"] } else { "no" }
$enable_auth = Read-UserInput "Do you want to enable authentication? (yes/no)" $default_auth
$auth_type = "none"
if ($enable_auth -eq "yes" -or $enable_auth -eq "true") {
    $enable_auth = "true"  # Normalize to "true" for bicep
    $default_auth_type = if ($usingScenario -and $envVars.ContainsKey("AUTH_TYPE")) { $envVars["AUTH_TYPE"] } else { "none" }
    $auth_type = Read-UserInput "Enter authentication type (entraID, servicebus, none)" $default_auth_type
}
else {
    $enable_auth = "false"  # Normalize to "false" for bicep
}

# Storage
$default_storage = if ($usingScenario -and $envVars.ContainsKey("AZURE_STORAGE_ACCOUNT")) { $envVars["AZURE_STORAGE_ACCOUNT"] } else { "" }
$storage_account = Read-UserInput "Enter storage account name or leave blank for default" $default_storage

$default_async = if ($usingScenario -and $envVars.ContainsKey("USE_ASYNC_STORAGE")) { $envVars["USE_ASYNC_STORAGE"] } else { "no" }
$use_async_storage = Read-UserInput "Do you want to enable async storage features? (yes/no)" $default_async
if ($use_async_storage -eq "yes" -or $use_async_storage -eq "true") {
    $use_async_storage = "true"
}
else {
    $use_async_storage = "false"
}

# Backend URLs
$default_urls = if ($usingScenario -and $envVars.ContainsKey("BACKEND_HOST_URLS")) { $envVars["BACKEND_HOST_URLS"] } else { "" }
$backend_urls = Read-UserInput "Enter comma-separated list of backend URLs" $default_urls
while ([string]::IsNullOrEmpty($backend_urls)) {
    Write-Host "Backend URLs are required!" -ForegroundColor Red
    $backend_urls = Read-UserInput "Enter comma-separated list of backend URLs" ""
}

# Request timeout
$default_timeout_val = if ($usingScenario -and $envVars.ContainsKey("DEFAULT_REQUEST_TIMEOUT")) { $envVars["DEFAULT_REQUEST_TIMEOUT"] } else { "100000" }
$default_timeout = Read-UserInput "Enter default request timeout in milliseconds" $default_timeout_val

# Monitoring
$default_insights = if ($usingScenario -and $envVars.ContainsKey("ENABLE_APP_INSIGHTS")) { $envVars["ENABLE_APP_INSIGHTS"] } else { "yes" }
$enable_app_insights = Read-UserInput "Do you want to enable Application Insights? (yes/no)" $default_insights
if ($enable_app_insights -eq "yes" -or $enable_app_insights -eq "true") {
    $enable_app_insights = "true"
}
else {
    $enable_app_insights = "false"
}

$default_logs = if ($usingScenario -and $envVars.ContainsKey("AZURE_LOG_ANALYTICS_WORKSPACE")) { $envVars["AZURE_LOG_ANALYTICS_WORKSPACE"] } else { "" }
$log_analytics = Read-UserInput "Enter Log Analytics workspace name or leave blank for default" $default_logs
if ([string]::IsNullOrEmpty($log_analytics)) {
    $log_analytics = "log-${environment}-s7p"
}

# Advanced Configuration
$default_levels = if ($usingScenario -and $envVars.ContainsKey("QUEUE_PRIORITY_LEVELS")) { $envVars["QUEUE_PRIORITY_LEVELS"] } else { "3" }
$priority_levels = Read-UserInput "Enter number of priority levels" $default_levels

$default_scale = if ($usingScenario -and $envVars.ContainsKey("ENABLE_SCALE_RULE")) { $envVars["ENABLE_SCALE_RULE"] } else { "yes" }
$enable_scale_rule = Read-UserInput "Do you want to enable auto-scaling? (yes/no)" $default_scale
if ($enable_scale_rule -eq "yes" -or $enable_scale_rule -eq "true") {
    $enable_scale_rule = "true"
}
else {
    $enable_scale_rule = "false"
}

$default_http = if ($usingScenario -and $envVars.ContainsKey("HTTP_CONCURRENT_REQUESTS_THRESHOLD")) { $envVars["HTTP_CONCURRENT_REQUESTS_THRESHOLD"] } else { "10" }
$http_threshold = Read-UserInput "Enter HTTP concurrent requests threshold for scaling" $default_http

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "Creating AZD environment..." -ForegroundColor Green
Write-Host "========================================================" -ForegroundColor Cyan

# Initialize AZD environment
try {
    azd env new "s7p-${environment}" --no-prompt
}
catch {
    azd env select "s7p-${environment}"
}

# Set AZD environment variables
azd env set AZURE_LOCATION "$region"
azd env set AZURE_RESOURCE_GROUP "$resource_group"
azd env set AZURE_SUBSCRIPTION_ID "$subscription"

# Set bicep parameters
azd env set AZURE_CONTAINER_REGISTRY_NAME "$container_registry"
azd env set AZURE_CONTAINER_APP_NAME "$container_app_name"
azd env set AZURE_CONTAINER_APP_ENVIRONMENT_NAME "$container_app_env"
azd env set AZURE_CONTAINER_APP_MIN_REPLICAS "$min_replicas"
azd env set AZURE_CONTAINER_APP_MAX_REPLICAS "$max_replicas"
azd env set USE_VNET "$use_vnet"
if ($use_vnet -eq "yes") {
    azd env set VNET_ADDRESS_PREFIX "$vnet_prefix"
    azd env set SUBNET_ADDRESS_PREFIX "$subnet_prefix"
}
azd env set ENABLE_AUTHENTICATION "$enable_auth"
azd env set AUTH_TYPE "$auth_type"
if (-not [string]::IsNullOrEmpty($storage_account)) {
    azd env set AZURE_STORAGE_ACCOUNT "$storage_account"
}
azd env set USE_ASYNC_STORAGE "$use_async_storage"
azd env set BACKEND_HOST_URLS "$backend_urls"
azd env set DEFAULT_REQUEST_TIMEOUT "$default_timeout"
azd env set ENABLE_APP_INSIGHTS "$enable_app_insights"
if (-not [string]::IsNullOrEmpty($log_analytics)) {
    azd env set AZURE_LOG_ANALYTICS_WORKSPACE "$log_analytics"
}
azd env set QUEUE_PRIORITY_LEVELS "$priority_levels"
azd env set ENABLE_SCALE_RULE "$enable_scale_rule"
azd env set HTTP_CONCURRENT_REQUESTS_THRESHOLD "$http_threshold"

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "Environment variables set." -ForegroundColor Green
Write-Host "========================================================" -ForegroundColor Cyan

Write-Host "To deploy the infrastructure, run: azd provision" -ForegroundColor Yellow
Write-Host "To build and deploy the app, run: azd deploy" -ForegroundColor Yellow
Write-Host "To do both at once, run: azd up" -ForegroundColor Yellow