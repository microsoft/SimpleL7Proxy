#!/bin/bash

# SimpleL7Proxy AZD Deployment Script
echo "========================================================"
echo "  SimpleL7Proxy AZD Deployment Setup"
echo "========================================================"

# Function to read user input with a default value
read_input() {
  local prompt="$1"
  local default="$2"
  local input=""
  
  if [ -n "$default" ]; then
    prompt="$prompt [$default]"
  fi
  
  read -p "$prompt: " input
  
  if [ -z "$input" ] && [ -n "$default" ]; then
    input="$default"
  fi
  
  echo "$input"
}

# Function to load environment variables from a file
load_env_file() {
  local file_path="$1"
  
  if [ -f "$file_path" ]; then
    while IFS= read -r line || [ -n "$line" ]; do
      # Skip comments and empty lines
      if [[ "$line" =~ ^[[:space:]]*# ]] || [[ -z "${line// /}" ]]; then
        continue
      fi
      
      # Extract key and value
      if [[ "$line" =~ ^([^=]+)=(.*)$ ]]; then
        local key="${BASH_REMATCH[1]}"
        local value="${BASH_REMATCH[2]}"
        
        # Remove quotes if present
        value="${value#\"}"
        value="${value%\"}"
        
        # Store in associative array
        declare -g "env_${key}=$value"
      fi
    done < "$file_path"
    return 0
  else
    return 1
  fi
}

# Check if AZD is installed
if ! command -v azd &> /dev/null; then
  echo "Azure Developer CLI (AZD) is not installed."
  echo "Please install it from https://learn.microsoft.com/en-us/azure/developer/azure-developer-cli/install-azd"
  exit 1
fi

# Check if Azure CLI is installed
if ! command -v az &> /dev/null; then
  echo "Azure CLI is not installed."
  echo "Please install it from https://docs.microsoft.com/en-us/cli/azure/install-azure-cli"
  exit 1
fi

# Check if user is logged in to Azure
echo "Checking Azure login status..."
if ! az account show &> /dev/null; then
  echo "You are not logged in to Azure. Please login first."
  az login
fi

# Display available subscriptions
echo "Available Azure Subscriptions:"
az account list --query "[].{Name:name, SubscriptionId:id}" --output table

# Check for deployment scenarios
scenario_path="$(dirname "$0")/scenarios"
use_scenario=$(read_input "Would you like to use a predefined deployment scenario? (yes/no)" "yes")

if [ "$use_scenario" = "yes" ]; then
  # List available scenarios
  echo -e "\nAvailable deployment scenarios:"
  echo "1. Local proxy with public APIM - Run the proxy locally while connecting to a public Azure API Management instance"
  echo "2. ACA proxy with public APIM - Deploy proxy as Azure Container App connecting to a public APIM instance"
  echo "3. VNET proxy deployment - Deploy proxy within a Virtual Network for enhanced security"
  
  scenario_choice=$(read_input "Select a scenario (1-3)" "2")
  
  case "$scenario_choice" in
    1)
      scenario_name="Local proxy with public APIM"
      env_file="$scenario_path/local-proxy-public-apim.env"
      ;;
    2)
      scenario_name="ACA proxy with public APIM"
      env_file="$scenario_path/aca-proxy-public-apim.env"
      ;;
    3)
      scenario_name="VNET proxy deployment"
      env_file="$scenario_path/vnet-proxy-deployment.env"
      ;;
    *)
      echo "Invalid scenario selection. Using manual configuration."
      env_file=""
      ;;
  esac
  
  if [ -n "$env_file" ]; then
    echo "Loading scenario: $scenario_name"
    
    if ! load_env_file "$env_file"; then
      echo "Error: Scenario file not found: $env_file"
      exit 1
    fi
  fi
fi

# Check for environment template
env_template_path="$(dirname "$0")/env-templates"
use_env_template=$(read_input "Would you like to use a predefined environment template for container app settings? (yes/no)" "no")

if [ "$use_env_template" = "yes" ]; then
  # List available environment templates
  echo -e "\nAvailable environment templates:"
  echo "1. Standard Production - A balanced configuration for production workloads with good performance and reasonable resource usage"
  echo "2. High Performance - Optimized for maximum throughput and low latency"
  echo "3. Cost Optimized - Designed to minimize resource usage and cost"
  echo "4. High Availability - Maximized for resilience and uptime"
  echo "5. Local Development - For running with 'dotnet run' on local machine with localhost backends"
  echo "6. Container Development - For containerized development and testing scenarios"
  
  env_template_choice=$(read_input "Select an environment template (1-6)" "1")
  
  case "$env_template_choice" in
    1)
      env_template_name="Standard Production"
      env_template_file="$env_template_path/standard-production.env"
      ;;
    2)
      env_template_name="High Performance"
      env_template_file="$env_template_path/high-performance.env"
      ;;
    3)
      env_template_name="Cost Optimized"
      env_template_file="$env_template_path/cost-optimized.env"
      ;;
    4)
      env_template_name="High Availability"
      env_template_file="$env_template_path/high-availability.env"
      ;;
    5)
      env_template_name="Local Development"
      env_template_file="$env_template_path/local-development.env"
      ;;
    6)
      env_template_name="Container Development"
      env_template_file="$env_template_path/container-development.env"
      ;;
    *)
      echo "Invalid environment template selection. No template will be applied."
      env_template_file=""
      ;;
  esac
  
  if [ -n "$env_template_file" ]; then
    echo "Loading environment template: $env_template_name"
    
    # Create a temporary file to store container env vars
    container_env_file=$(mktemp)
    
    if [ -f "$env_template_file" ]; then
      # Copy the content to a temporary file
      cp "$env_template_file" "$container_env_file"
      echo "Loaded container environment variables from template."
    else
      echo "Error: Environment template file not found: $env_template_file"
      rm -f "$container_env_file"
      container_env_file=""
    fi
  fi
fi

# Get Azure subscription (even if using a scenario)
subscription=$(read_input "Enter your Azure Subscription ID" "")
if [ -n "$subscription" ]; then
  az account set --subscription "$subscription"
fi

# Get environment name (if not loaded from scenario)
default_env="${env_ENVIRONMENT_NAME:-dev}"
environment=$(read_input "Enter environment name (dev, test, prod)" "$default_env")

# Get region (if not loaded from scenario)
default_region="${env_AZURE_LOCATION:-westus2}"
region=$(read_input "Enter Azure region for deployment" "$default_region")

# Resource group
default_rg="${env_AZURE_RESOURCE_GROUP:-}"
resource_group=$(read_input "Enter resource group name or leave blank for default" "$default_rg")
if [ -z "$resource_group" ]; then
  resource_group="rg-${environment}-s7p"
fi

# Container App name
default_app_name="${env_AZURE_CONTAINER_APP_NAME:-s7p-${environment}}"
container_app_name=$(read_input "Enter Container App name" "$default_app_name")

# Container registry
default_acr="${env_AZURE_CONTAINER_REGISTRY_NAME:-}"
container_registry=$(read_input "Enter container registry name or leave blank for default" "$default_acr")
if [ -z "$container_registry" ]; then
  container_registry="acr${environment}s7p"
fi

# Container App Environment
default_env="${env_AZURE_CONTAINER_APP_ENVIRONMENT_NAME:-}"
container_app_env=$(read_input "Enter Container App Environment name or leave blank for default" "$default_env")
if [ -z "$container_app_env" ]; then
  container_app_env="cae-${environment}-s7p"
fi

# Replicas
default_min="${env_AZURE_CONTAINER_APP_MIN_REPLICAS:-1}"
default_max="${env_AZURE_CONTAINER_APP_MAX_REPLICAS:-5}"
min_replicas=$(read_input "Enter minimum number of replicas" "$default_min")
max_replicas=$(read_input "Enter maximum number of replicas" "$default_max")

# VNET Configuration
default_vnet="${env_USE_VNET:-no}"
use_vnet=$(read_input "Do you want to deploy in a VNET? (yes/no)" "$default_vnet")
vnet_prefix=""
subnet_prefix=""
if [ "$use_vnet" = "yes" ] || [ "$use_vnet" = "true" ]; then
  use_vnet="true"  # Normalize to "true" for bicep
  default_vnet_prefix="${env_VNET_ADDRESS_PREFIX:-10.0.0.0/16}"
  default_subnet_prefix="${env_SUBNET_ADDRESS_PREFIX:-10.0.0.0/21}"
  vnet_prefix=$(read_input "Enter VNET address space" "$default_vnet_prefix")
  subnet_prefix=$(read_input "Enter subnet address space" "$default_subnet_prefix")
else
  use_vnet="false"  # Normalize to "false" for bicep
fi

# Authentication
default_auth="${env_ENABLE_AUTHENTICATION:-no}"
enable_auth=$(read_input "Do you want to enable authentication? (yes/no)" "$default_auth")
auth_type="none"
if [ "$enable_auth" = "yes" ] || [ "$enable_auth" = "true" ]; then
  enable_auth="true"  # Normalize to "true" for bicep
  default_auth_type="${env_AUTH_TYPE:-none}"
  auth_type=$(read_input "Enter authentication type (entraID, servicebus, none)" "$default_auth_type")
else
  enable_auth="false"  # Normalize to "false" for bicep
fi

# Storage
default_storage="${env_AZURE_STORAGE_ACCOUNT:-}"
storage_account=$(read_input "Enter storage account name or leave blank for default" "$default_storage")

default_async="${env_USE_ASYNC_STORAGE:-no}"
use_async_storage=$(read_input "Do you want to enable async storage features? (yes/no)" "$default_async")
if [ "$use_async_storage" = "yes" ] || [ "$use_async_storage" = "true" ]; then
  use_async_storage="true"
else
  use_async_storage="false"
fi

# Backend URLs
default_urls="${env_BACKEND_HOST_URLS:-}"
backend_urls=$(read_input "Enter comma-separated list of backend URLs" "$default_urls")
while [ -z "$backend_urls" ]; do
  echo "Backend URLs are required!"
  backend_urls=$(read_input "Enter comma-separated list of backend URLs" "")
done

# Request timeout
default_timeout_val="${env_DEFAULT_REQUEST_TIMEOUT:-100000}"
default_timeout=$(read_input "Enter default request timeout in milliseconds" "$default_timeout_val")

# Monitoring
default_insights="${env_ENABLE_APP_INSIGHTS:-yes}"
enable_app_insights=$(read_input "Do you want to enable Application Insights? (yes/no)" "$default_insights")
if [ "$enable_app_insights" = "yes" ] || [ "$enable_app_insights" = "true" ]; then
  enable_app_insights="true"
else
  enable_app_insights="false"
fi

default_logs="${env_AZURE_LOG_ANALYTICS_WORKSPACE:-}"
log_analytics=$(read_input "Enter Log Analytics workspace name or leave blank for default" "$default_logs")
if [ -z "$log_analytics" ]; then
  log_analytics="log-${environment}-s7p"
fi

# Advanced Configuration
default_levels="${env_QUEUE_PRIORITY_LEVELS:-3}"
priority_levels=$(read_input "Enter number of priority levels" "$default_levels")

default_scale="${env_ENABLE_SCALE_RULE:-yes}"
enable_scale_rule=$(read_input "Do you want to enable auto-scaling? (yes/no)" "$default_scale")
if [ "$enable_scale_rule" = "yes" ] || [ "$enable_scale_rule" = "true" ]; then
  enable_scale_rule="true"
else
  enable_scale_rule="false"
fi

default_http="${env_HTTP_CONCURRENT_REQUESTS_THRESHOLD:-10}"
http_threshold=$(read_input "Enter HTTP concurrent requests threshold for scaling" "$default_http")

echo "========================================================"
echo "Creating AZD environment..."
echo "========================================================"

# Initialize AZD environment
azd env new s7p-${environment} --no-prompt || azd env select s7p-${environment}

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
[ "$use_vnet" = "yes" ] && azd env set VNET_ADDRESS_PREFIX "$vnet_prefix"
[ "$use_vnet" = "yes" ] && azd env set SUBNET_ADDRESS_PREFIX "$subnet_prefix"
azd env set ENABLE_AUTHENTICATION "$enable_auth"
azd env set AUTH_TYPE "$auth_type"
[ -n "$storage_account" ] && azd env set AZURE_STORAGE_ACCOUNT "$storage_account"
azd env set USE_ASYNC_STORAGE "$use_async_storage"
azd env set BACKEND_HOST_URLS "$backend_urls"
azd env set DEFAULT_REQUEST_TIMEOUT "$default_timeout"
azd env set ENABLE_APP_INSIGHTS "$enable_app_insights"
[ -n "$log_analytics" ] && azd env set AZURE_LOG_ANALYTICS_WORKSPACE "$log_analytics"
azd env set QUEUE_PRIORITY_LEVELS "$priority_levels"
azd env set ENABLE_SCALE_RULE "$enable_scale_rule"
azd env set HTTP_CONCURRENT_REQUESTS_THRESHOLD "$http_threshold"

echo "========================================================"
echo "Environment variables set."
echo "========================================================"

echo "To deploy the infrastructure, run: azd provision"
echo "To build and deploy the app, run: azd deploy"
echo "To do both at once, run: azd up"