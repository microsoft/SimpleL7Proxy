#!/bin/bash

# SimpleL7Proxy AZD Deployment Script
echo "========================================================"
echo "  SimpleL7Proxy AZD Deployment Setup"
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

echo "========================================================"
echo "  Step 1: Checking Prerequisites"
echo "========================================================"
echo ""

# Check if AZD is installed
echo "Checking if Azure Developer CLI (AZD) is installed..."
if ! command -v azd &> /dev/null; then
  echo "❌ Azure Developer CLI (AZD) is not installed."
  echo "Please install it from https://learn.microsoft.com/en-us/azure/developer/azure-developer-cli/install-azd"
  exit 1
fi
echo "✅ Azure Developer CLI (AZD) is installed"
echo ""

# Check if Azure CLI is installed
echo "Checking if Azure CLI is installed..."
if ! command -v az &> /dev/null; then
  echo "❌ Azure CLI is not installed."
  echo "Please install it from https://docs.microsoft.com/en-us/cli/azure/install-azure-cli"
  exit 1
fi
echo "✅ Azure CLI is installed"
echo ""

echo "========================================================"
echo "  Step 2: Azure Authentication & Subscription (delegated)"
echo "========================================================"
echo ""
echo "Note: Cloud-specific authentication and azd/az interactions are handled by '.azure/azure-setup.sh' when you select a cloud scenario."
echo ""

echo "========================================================"
echo "  Step 3: Deployment Scenario Selection"
echo "========================================================"
echo ""
echo "💡 Deployment scenarios provide pre-configured settings for common use cases."
echo "   They help simplify setup by setting appropriate defaults for your deployment type."
echo ""
echo "   • Scenario 1: Local development with cloud APIM - Test locally before deploying"
echo "   • Scenario 2: Full cloud deployment - Production-ready Azure Container Apps setup"  
echo "   • Scenario 3: Secure VNET deployment - Enhanced security with private networking"
echo ""

# Check for deployment scenarios
scenario_path="$(dirname "$0")/scenarios"
use_scenario=$(read_input "Would you like to use a predefined deployment scenario? (yes/no)" "yes")

if [ "$use_scenario" = "yes" ]; then
  echo ""
  echo "📋 Available deployment scenarios:"
  echo ""
  echo "1. Local proxy with public APIM"
  echo "   → Run the proxy locally on your machine while connecting to Azure API Management"
  echo "   → Best for: Development, testing, debugging before cloud deployment"
  echo ""
  echo "2. ACA proxy with public APIM (RECOMMENDED)"
  echo "   → Deploy proxy as Azure Container App connecting to a public APIM instance"
  echo "   → Best for: Production workloads, scalable cloud deployment"
  echo ""
  echo "3. VNET proxy deployment"
  echo "   → Deploy proxy within a Virtual Network for enhanced security"
  echo "   → Best for: Enterprise environments requiring network isolation"
  echo ""
  
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
    echo ""
    echo "🔧 Loading scenario: $scenario_name"
    
    if ! load_env_file "$env_file"; then
      echo "❌ Error: Scenario file not found: $env_file"
      exit 1
    fi
    echo "✅ Scenario configuration loaded successfully"
  fi
fi

echo ""
echo "========================================================"
echo "  Step 4: Environment Template Selection"
echo "========================================================"
echo ""
env_template_path="$(dirname "$0")/env-templates"
use_env_template=$(read_input "Would you like to use a predefined environment template for container app settings? (yes/no)" "no")

if [ "$use_env_template" = "yes" ]; then
  echo ""
  echo "📋 Available environment templates are in: $env_template_path"
  echo "If you select one, it will be copied to a temporary file and used during the cloud deployment flow."
  env_template_choice=$(read_input "Enter template filename (or leave blank to cancel)" "")
  if [ -n "$env_template_choice" ]; then
    env_template_file="$env_template_path/$env_template_choice"
    if [ -f "$env_template_file" ]; then
      container_env_file=$(mktemp)
      cp "$env_template_file" "$container_env_file"
      echo "✅ Loaded container environment variables from template: $env_template_choice"
    else
      echo "❌ Error: Environment template file not found: $env_template_file"
      container_env_file=""
    fi
  fi
fi

echo ""
echo "========================================================"
echo "  Step 5: Azure Configuration"
echo "========================================================"
echo ""

# Check if we need Azure resources based on scenario
if [ "$scenario_choice" = "1" ]; then
  # Delegate to local-only setup script and exit
  if [ -x "$(dirname "$0")/local-setup.sh" ]; then
    "$(dirname "$0")/local-setup.sh"
    exit 0
  else
    echo "⚠️  Local setup helper not found or not executable: $(dirname "$0")/local-setup.sh"
    echo "Falling back to inline local prompts."
    subscription=""
  fi
else
  # Delegate cloud-specific interactive flow to azure-setup.sh
  echo "🔧 Delegating cloud-specific setup to '.azure/azure-setup.sh'"
  if [ -x "$(dirname "$0")/azure-setup.sh" ]; then
    "$(dirname "$0")/azure-setup.sh"
    # azure-setup.sh will perform azd env set and print next steps
    exit 0
  else
    echo "❌ Cloud setup helper not found or not executable: $(dirname "$0")/azure-setup.sh"
    echo "Please make it executable and re-run this script or run the cloud steps manually."
    exit 1
  fi
fi

echo ""

# Get environment name (if not loaded from scenario)
default_env="${env_ENVIRONMENT_NAME:-dev}"
environment=$(read_input "Enter environment name (dev, test, prod)" "$default_env")

# For local development, we only need basic configuration
if [ "$scenario_choice" = "1" ]; then
  echo ""
  echo "🔧 For local development, we only need basic settings..."
  echo "   Most configuration will be handled by the local application settings."
  echo ""
  
  # Use default region for any references but do not prompt the user for it
  region="${env_AZURE_LOCATION:-westus2}"

  # Local dev: pick backend mode (use short prompt to avoid wrapping)
  echo ""
  echo "🔎 Local backend options: choose how the proxy will reach your APIs"
  echo "   Options: 'apim' = existing APIM gateway, 'null' = local null/mock server, 'real' = real backend URLs"
  backend_mode=$(read_input "Backend (apim/null/real)" "real")

  case "$backend_mode" in
    apim)
      # existing APIM gateway URL
      apim_url=$(read_input "APIM gateway URL (e.g. https://myapim.azure-api.net)" "")
      backend_urls="$apim_url"
      ;;
    null)
      # local mock/nullserver — request URLs including ports
      echo ""
      echo "Provide one or more local mock server URLs (include port numbers)."
      echo "Example: http://localhost:3000 or http://localhost:3000,http://localhost:3001"
      mock_urls=$(read_input "Local mock server URL(s)" "http://localhost:3000")
      backend_urls="$mock_urls"
      ;;
    *)
      # real backends - we will prompt for them below (leave empty to force prompt)
      backend_urls=""
      ;;
  esac

  # Proxy listen port (local dev) - short prompt
  proxy_port=$(read_input "Local proxy listen port" "5000")

  
  # Skip to backend configuration which is essential
  echo ""
  echo "========================================================"
  echo "  Backend Configuration (Local Development)"
  echo "========================================================"
  echo ""
  
  echo "🔧 Configuring backend URLs and timeouts..."
  echo "   ⚠️  Backend URLs are REQUIRED for the proxy to function"
  echo ""

  # Backend URLs
  # If backend_urls already provided (e.g., from apim_url or mock_urls), skip re-prompt.
  if [ -z "$backend_urls" ]; then
    default_urls="${env_BACKEND_HOST_URLS:-}"
    backend_urls=$(read_input "Enter comma-separated list of backend URLs" "$default_urls")
    while [ -z "$backend_urls" ]; do
      echo ""
      echo "❌ Backend URLs are required!"
      echo "   Example: https://api1.example.com,https://api2.example.com"
      echo "   For local testing, you can also use: http://localhost:3000,http://localhost:3001"
      backend_urls=$(read_input "Enter comma-separated list of backend URLs" "")
    done
  else
    # keep the previously set backend_urls (from apim or mock)
    :
  fi

  # Request timeout
  default_timeout_val="${env_DEFAULT_REQUEST_TIMEOUT:-100000}"
  default_timeout=$(read_input "Enter default request timeout in milliseconds" "$default_timeout_val")
  
else
  # For cloud deployments, get region and continue with full configuration
  default_region="${env_AZURE_LOCATION:-westus2}"
  region=$(read_input "Enter Azure region for deployment" "$default_region")

  echo ""
  echo "========================================================"
  echo "  Step 6: Resource Configuration"
  echo "========================================================"
  echo ""

  echo "🔧 Configuring Azure resources..."
  echo ""

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

  echo ""
  echo "========================================================"
  echo "  Step 7: Scaling Configuration"
  echo "========================================================"
  echo ""

  echo "🔧 Configuring scaling and replica settings..."
  echo ""

  # Replicas
  default_min="${env_AZURE_CONTAINER_APP_MIN_REPLICAS:-1}"
  default_max="${env_AZURE_CONTAINER_APP_MAX_REPLICAS:-5}"
  min_replicas=$(read_input "Enter minimum number of replicas" "$default_min")
  max_replicas=$(read_input "Enter maximum number of replicas" "$default_max")

  echo ""
  echo "========================================================"
  echo "  Step 8: Network Configuration"
  echo "========================================================"
  echo ""

  echo "🔧 Configuring network settings..."
  echo ""

  # VNET Configuration
  default_vnet="${env_USE_VNET:-no}"
  use_vnet=$(read_input "Do you want to deploy in a VNET? (yes/no)" "$default_vnet")
  vnet_prefix=""
  subnet_prefix=""
  if [ "$use_vnet" = "yes" ] || [ "$use_vnet" = "true" ]; then
    use_vnet="true"  # Normalize to "true" for bicep
    echo ""
    echo "🔧 Configuring VNET address spaces..."
    default_vnet_prefix="${env_VNET_ADDRESS_PREFIX:-10.0.0.0/16}"
    default_subnet_prefix="${env_SUBNET_ADDRESS_PREFIX:-10.0.0.0/21}"
    vnet_prefix=$(read_input "Enter VNET address space" "$default_vnet_prefix")
    subnet_prefix=$(read_input "Enter subnet address space" "$default_subnet_prefix")
  else
    use_vnet="false"  # Normalize to "false" for bicep
  fi

  echo ""
  echo "========================================================"
  echo "  Step 9: Authentication Configuration"
  echo "========================================================"
  echo ""

  echo "🔧 Configuring authentication settings..."
  echo ""

  # Authentication
  default_auth="${env_ENABLE_AUTHENTICATION:-no}"
  enable_auth=$(read_input "Do you want to enable authentication? (yes/no)" "$default_auth")
  auth_type="none"
  if [ "$enable_auth" = "yes" ] || [ "$enable_auth" = "true" ]; then
    enable_auth="true"  # Normalize to "true" for bicep
    echo ""
    default_auth_type="${env_AUTH_TYPE:-none}"
    auth_type=$(read_input "Enter authentication type (entraID, servicebus, none)" "$default_auth_type")
  else
    enable_auth="false"  # Normalize to "false" for bicep
  fi

  echo ""
  echo "========================================================"
  echo "  Step 10: Storage Configuration"
  echo "========================================================"
  echo ""

  echo "🔧 Configuring storage settings..."
  echo ""

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

  echo ""
  echo "========================================================"
  echo "  Step 11: Backend Configuration"
  echo "========================================================"
  echo ""

  echo "🔧 Configuring backend URLs and timeouts..."
  echo "   ⚠️  Backend URLs are REQUIRED for the proxy to function"
  echo ""

  # Backend URLs
  default_urls="${env_BACKEND_HOST_URLS:-}"
  backend_urls=$(read_input "Enter comma-separated list of backend URLs" "$default_urls")
  while [ -z "$backend_urls" ]; do
    echo ""
    echo "❌ Backend URLs are required!"
    echo "   Example: https://api1.example.com,https://api2.example.com"
    backend_urls=$(read_input "Enter comma-separated list of backend URLs" "")
  done

  # Request timeout
  default_timeout_val="${env_DEFAULT_REQUEST_TIMEOUT:-100000}"
  default_timeout=$(read_input "Enter default request timeout in milliseconds" "$default_timeout_val")

  echo ""
  echo "========================================================"
  echo "  Step 12: Monitoring Configuration"
  echo "========================================================"
  echo ""

  echo "🔧 Configuring monitoring and logging..."
  echo ""

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

  echo ""
  echo "========================================================"
  echo "  Step 13: Advanced Configuration"
  echo "========================================================"
  echo ""

  echo "🔧 Configuring advanced settings..."
  echo ""

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
fi

echo ""
echo "========================================================"
echo "  Step 14: Creating AZD Environment"
echo "========================================================"
echo ""

if [ "$scenario_choice" = "1" ]; then
  echo "🏠 For local development scenario, creating local configuration..."
  echo ""
  echo "   The proxy will run locally using 'dotnet run' and connect to Azure services."
  echo "   No Azure infrastructure will be deployed."
  echo ""
  
  # For local development, we might just create a local settings file
  echo "🔧 Creating local development configuration..."
  
  # Create local settings file or environment file for local development
  local_config_file=".azure/local-dev.env"
  echo "# Local Development Configuration" > "$local_config_file"
  echo "ENVIRONMENT_NAME=$environment" >> "$local_config_file"
  [ -n "$subscription" ] && echo "AZURE_SUBSCRIPTION_ID=$subscription" >> "$local_config_file"
  echo "AZURE_LOCATION=$region" >> "$local_config_file"
  echo "BACKEND_HOST_URLS=$backend_urls" >> "$local_config_file"
  echo "DEFAULT_REQUEST_TIMEOUT=$default_timeout" >> "$local_config_file"
  
  echo "✅ Local development configuration saved to $local_config_file"
  echo ""
  echo "🚀 Next Steps for Local Development:"
  echo "   1. Navigate to the src/SimpleL7Proxy directory"
  echo "   2. Run: dotnet run"
  echo "   3. The proxy will start locally and connect to your configured backends"
  
else
  echo "🚀 All configuration complete! Creating AZD environment for cloud deployment..."
  echo ""

  # Initialize AZD environment
  azd env new s7p-${environment} --no-prompt || azd env select s7p-${environment}

  echo ""
  echo "🔧 Setting AZD environment variables..."

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
  [ "$use_vnet" = "true" ] && azd env set VNET_ADDRESS_PREFIX "$vnet_prefix"
  [ "$use_vnet" = "true" ] && azd env set SUBNET_ADDRESS_PREFIX "$subnet_prefix"
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
fi

echo ""
echo "========================================================"
echo "  ✅ Setup Complete!"
echo "========================================================"
echo ""

if [ "$scenario_choice" = "1" ]; then
  echo "🎉 Local development environment configured successfully!"
  echo ""
  echo "📋 Configuration Summary:"
  echo "   • Environment: $environment (local)"
  echo "   • Backend URLs: $backend_urls"
  echo "   • Configuration saved to: .azure/local-dev.env"
  echo ""
  echo "🚀 Next Steps:"
  echo "   1. Navigate to: cd src/SimpleL7Proxy"
  echo "   2. Run locally: dotnet run"
  echo "   3. Test your proxy at: http://localhost:5000"
  echo ""
  echo "💡 The proxy will run on your local machine and proxy requests to your configured backends!"
else
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
fi
echo "========================================================"