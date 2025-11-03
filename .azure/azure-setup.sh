#!/bin/bash

# Azure-specific setup for SimpleL7Proxy
echo "========================================================"
echo "  SimpleL7Proxy AZD Cloud Deployment Setup"
echo "========================================================"
echo ""

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

# Simple loader for key=value env files; stores into env_<KEY> globals
load_env_file() {
  local file_path="$1"
  
  if [ -f "$file_path" ]; then
    while IFS= read -r line || [ -n "$line" ]; do
      if [[ "$line" =~ ^[[:space:]]*# ]] || [[ -z "${line// /}" ]]; then
        continue
      fi
      if [[ "$line" =~ ^([^=]+)=(.*)$ ]]; then
        local key="${BASH_REMATCH[1]}"
        local value="${BASH_REMATCH[2]}"
        value="${value#\"}"
        value="${value%\"}"
        declare -g "env_${key}=$value"
      fi
    done < "$file_path"
    return 0
  else
    return 1
  fi
}

echo "========================================================"
echo "  Step 1: Checking Prerequisites"
echo "========================================================"
echo ""

echo "Checking if Azure Developer CLI (AZD) is installed..."
if ! command -v azd &> /dev/null; then
  echo "❌ Azure Developer CLI (AZD) is not installed."
  echo "Please install it from https://learn.microsoft.com/en-us/azure/developer/azure-developer-cli/install-azd"
  exit 1
fi
echo "✅ Azure Developer CLI (AZD) is installed"
echo ""

echo "Checking if Azure CLI is installed..."
if ! command -v az &> /dev/null; then
  echo "❌ Azure CLI is not installed."
  echo "Please install it from https://docs.microsoft.com/en-us/cli/azure/install-azure-cli"
  exit 1
fi
echo "✅ Azure CLI is installed"
echo ""

echo "========================================================"
echo "  Step 2: Azure Authentication & Subscription"
echo "========================================================"
echo ""

echo "Checking Azure login status..."
if ! az account show &> /dev/null; then
  echo "You are not logged in to Azure. Please login first."
  az login
fi
echo "✅ Successfully logged into Azure"
echo ""

echo "========================================================"
echo "  Cloud deployment configuration"
echo "========================================================"
echo ""

# Display available subscriptions
echo "📋 Available Azure subscriptions:"
echo "   (Choose the subscription where you want to deploy the SimpleL7Proxy)"
echo ""
az account list --query "[].{Name:name, SubscriptionId:id}" --output table
echo ""

subscription=$(read_input "Enter your Azure Subscription ID" "")
while [ -z "$subscription" ]; do
  echo "❌ Subscription ID is required for cloud deployment!"
  subscription=$(read_input "Enter your Azure Subscription ID" "")
done

az account set --subscription "$subscription"
echo "✅ Azure subscription set to: $subscription"
echo ""

# Get environment name
default_env="${env_ENVIRONMENT_NAME:-dev}"
environment=$(read_input "Enter environment name (dev, test, prod)" "$default_env")

# Region
default_region="${env_AZURE_LOCATION:-westus2}"
region=$(read_input "Enter Azure region for deployment" "$default_region")

echo ""
echo "========================================================"
echo "  Resource Configuration"
echo "========================================================"
echo ""

# Resource group
default_rg="${env_AZURE_RESOURCE_GROUP:-}"
resource_group=$(read_input "Enter resource group name or leave blank for default" "$default_rg")
if [ -z "$resource_group" ]; then
  resource_group="rg-${environment}-s7p"
fi

echo "NOTE: ACA (Azure Container Apps) specific settings (container app name, registry, container environment, scaling, VNET) are handled by '.azure/aca-setup.sh'."
echo "If you want to configure those now, ensure '.azure/aca-setup.sh' is executable; it will be invoked later in this flow."

# ACA settings and scaling/network configuration are delegated to aca-setup.sh
aca_script="$(dirname "$0")/aca-setup.sh"


echo ""
echo "========================================================"
echo "  Authentication Configuration"
echo "========================================================"
echo ""

default_auth="${env_ENABLE_AUTHENTICATION:-no}"
enable_auth=$(read_input "Do you want to enable authentication? (yes/no)" "$default_auth")
auth_type="none"
if [ "$enable_auth" = "yes" ] || [ "$enable_auth" = "true" ]; then
  enable_auth="true"
  echo ""
  default_auth_type="${env_AUTH_TYPE:-none}"
  auth_type=$(read_input "Enter authentication type (entraID, servicebus, none)" "$default_auth_type")
else
  enable_auth="false"
fi

echo ""
echo "========================================================"
echo "  Storage Configuration"
echo "========================================================"
echo ""

default_storage="${env_AZURE_STORAGE_ACCOUNT:-}"
storage_account=$(read_input "Enter storage account name or leave blank for default" "$default_storage")

default_async="${env_USE_ASYNC_STORAGE:-no}"
use_async_storage=$(read_input "Do you want to enable async storage features? (yes/no)" "$default_async")
if [ "$use_async_storage" = "yes" ] || [ "$use_async_storage" = "true" ]; then
  use_async_storage="true"
else
  use_async_storage="false"
fi

echo ""
echo "========================================================"
echo "  Backend Configuration"
echo "========================================================"
echo ""

default_urls="${env_BACKEND_HOST_URLS:-}"
backend_urls=$(read_input "Enter comma-separated list of backend URLs" "$default_urls")
while [ -z "$backend_urls" ]; do
  echo ""
  echo "❌ Backend URLs are required!"
  echo "   Example: https://api1.example.com,https://api2.example.com"
  backend_urls=$(read_input "Enter comma-separated list of backend URLs" "")
done

default_timeout_val="${env_DEFAULT_REQUEST_TIMEOUT:-100000}"
default_timeout=$(read_input "Enter default request timeout in milliseconds" "$default_timeout_val")

echo ""
echo "========================================================"
echo "  Monitoring Configuration"
echo "========================================================"
echo ""

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

echo ""
echo "========================================================"
echo "  Advanced Configuration"
echo "========================================================"
echo ""

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

echo ""
echo "========================================================"
echo "  Creating AZD Environment"
echo "========================================================"
echo ""

echo "🚀 All configuration complete! Creating AZD environment for cloud deployment..."
echo ""

# Check azd version to give an early, clear warning (less noisy than letting azd print repeatedly)
if command -v azd &> /dev/null; then
  azd_ver_raw=$(azd --version 2>/dev/null | head -n1)
  # try to extract first semantic version-looking token
  azd_ver=$(echo "$azd_ver_raw" | grep -oE '[0-9]+(\.[0-9]+)+')
  min_ver="1.20.0"
  if [ -n "$azd_ver" ]; then
    # simple numeric compare by splitting
    IFS=. read -r a1 a2 a3 <<< "$azd_ver"
    IFS=. read -r b1 b2 b3 <<< "$min_ver"
    a1=${a1:-0}; a2=${a2:-0}; a3=${a3:-0}
    b1=${b1:-0}; b2=${b2:-0}; b3=${b3:-0}
    if [ "$a1" -lt "$b1" ] || { [ "$a1" -eq "$b1" ] && [ "$a2" -lt "$b2" ]; } || { [ "$a1" -eq "$b1" ] && [ "$a2" -eq "$b2" ] && [ "$a3" -lt "$b3" ]; }; then
      echo "⚠️  Your azd version is $azd_ver — recommended >= $min_ver. Consider upgrading to avoid unexpected issues."
      echo "   Upgrade: curl -fsSL https://aka.ms/install-azd.sh | bash"
      echo ""
    fi
  fi
fi

# Initialize AZD environment (capture failures; azd prints a noisy error if no project exists)
azd_env_ready=false
create_output=$(azd env new s7p-${environment} --no-prompt 2>&1)
create_rc=$?
if [ $create_rc -ne 0 ]; then
  # Detect specific 'no project exists' message and handle gracefully
  if echo "$create_output" | grep -qi "no project exists"; then
    echo "❌ No azd project detected in this directory. 'azd env new' failed with:"
    echo "   $create_output" | sed 's/^/   /'
    echo ""
    echo "Action: initialize an azd project first (run 'azd init' in the repo root), or run 'azd up' to create the project and environment interactively."
    echo "Skipping AZD env variable setup."
    azd_env_ready=false
  else
    echo "❌ Failed to create/select AZD environment:" 
    echo "$create_output"
    exit $create_rc
  fi
else
  azd_env_ready=true
fi

echo ""
echo "🔧 Setting AZD environment variables..."

if [ "$azd_env_ready" = true ]; then
  azd env set AZURE_LOCATION "$region"
  azd env set AZURE_RESOURCE_GROUP "$resource_group"
  azd env set AZURE_SUBSCRIPTION_ID "$subscription"

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

  # Prompt for registry to use (allow user to pick the created ACR name or an existing one)
  echo ""
  echo "========================================================"
  echo "  Container Registry (ACR) Selection"
  echo "========================================================"
  echo ""
  default_acr_from_env="${env_AZURE_CONTAINER_REGISTRY_NAME:-}" 
  acr_choice=$(read_input "Enter the ACR name to use (leave blank to use default naming)" "$default_acr_from_env")
  if [ -z "$acr_choice" ]; then
    acr_choice="acr${environment}s7p"
    echo "Using generated ACR name: $acr_choice"
  fi

  # Offer to build & push the image to the chosen registry
  echo ""
  build_choice=$(read_input "Do you want me to build & push the container image to ACR now? (yes/no)" "yes")
  if [ "$build_choice" = "yes" ]; then
    deploy_script="$(dirname "$0")/deploy-container-to-registry.sh"
    if [ -x "$deploy_script" ]; then
      echo "Running $deploy_script $acr_choice --acr-build"
      # Prefer Azure build in CI-friendly mode; fallback to local build if az acr build fails
      if ! "$deploy_script" "$acr_choice" --acr-build; then
        echo "az acr build failed or not available; trying local docker build & push"
        "$deploy_script" "$acr_choice"
      fi
    else
      echo "⚠️ Deploy script not found or not executable: $deploy_script"
      echo "Manual steps: build your image and push to $acr_choice or run 'az acr build' yourself."
    fi
  else
    echo "Skipping image build/push. Remember to push your image before provisioning to avoid Container Apps revision failures."
  fi

  # Delegate ACA-specific settings (container app, registry, scaling, VNET) if helper exists
  if [ -x "$aca_script" ]; then
    echo "\n🔧 Delegating ACA-specific configuration to $aca_script"
    "$aca_script"
  else
    echo "\n⚠️  ACA helper not found or not executable: $aca_script"
    echo "If you plan to deploy to Azure Container Apps, please make $aca_script executable and re-run this flow, or set ACA variables manually."
  fi
else
  echo "⚠️ Skipped applying AZD environment variables because AZD environment was not created/selected." 
  echo "   After initializing an azd project, re-run this script or run the following manually to set environment variables:"
  echo ""
  echo "   azd env set AZURE_LOCATION \"$region\""
  echo "   azd env set AZURE_RESOURCE_GROUP \"$resource_group\""
  echo "   azd env set AZURE_SUBSCRIPTION_ID \"$subscription\""
  echo "   ... (and others listed in the script)"
fi

echo ""
echo "🎉 AZD environment 's7p-${environment}' has been configured successfully!"
echo ""
echo "📋 Configuration Summary:"
echo "   • Environment: $environment"
echo "   • Region: $region"
echo "   • Resource Group: $resource_group"
echo "   • Container App: $container_app_name"
echo "   • Backend URLs: $backend_urls"
echo ""
echo "🚀 Next Steps:" 
echo "   1. To deploy the infrastructure: azd provision"
echo "   2. To build and deploy the app: azd deploy"
echo "   3. To do both at once: azd up"
echo ""
echo "💡 Tip: Run 'azd up' to deploy everything in one command!"
