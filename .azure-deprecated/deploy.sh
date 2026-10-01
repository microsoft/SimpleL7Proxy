#!/bin/bash
set -e

# Get variables from azd environment
AZURE_CONTAINER_REGISTRY_NAME=$(azd env get-values | grep AZURE_CONTAINER_REGISTRY_NAME | awk -F= '{print $2}')
AZURE_CONTAINER_APP_NAME=$(azd env get-values | grep AZURE_CONTAINER_APP_NAME | awk -F= '{print $2}')
AZURE_SUBSCRIPTION_ID=$(azd env get-values | grep AZURE_SUBSCRIPTION_ID | awk -F= '{print $2}')
AZURE_RESOURCE_GROUP=$(azd env get-values | grep AZURE_RESOURCE_GROUP | awk -F= '{print $2}')

# Get registry login server
ACR_LOGIN_SERVER=$(az acr show --name $AZURE_CONTAINER_REGISTRY_NAME --query "loginServer" --output tsv)

# Get version from project
VERSION=$(grep -o 'AssemblyVersion("[^"]*' src/SimpleL7Proxy/SimpleL7Proxy.csproj | sed 's/AssemblyVersion("//g')
if [ -z "$VERSION" ]; then
  VERSION="1.0.0"
fi

echo "Building and pushing Docker image version: $VERSION"

# Login to Azure Container Registry
echo "Logging in to Azure Container Registry..."
az acr login --name $AZURE_CONTAINER_REGISTRY_NAME

# Build and push Docker image
echo "Building and pushing Docker image..."
docker build -t ${ACR_LOGIN_SERVER}/simple-l7-proxy:${VERSION} -t ${ACR_LOGIN_SERVER}/simple-l7-proxy:latest .
docker push ${ACR_LOGIN_SERVER}/simple-l7-proxy:${VERSION}
docker push ${ACR_LOGIN_SERVER}/simple-l7-proxy:latest

echo "Updating Container App with new image..."

# Check if we have an environment template to apply
env_template_path="$(dirname "$0")/env-templates"
read -p "Do you want to apply an environment template to the deployment? (yes/no) [no]: " use_env_template
use_env_template=${use_env_template:-no}

if [ "$use_env_template" = "yes" ]; then
  # List available environment templates
  echo -e "\nAvailable environment templates:"
  echo "1. Standard Production - A balanced configuration for production workloads with good performance and reasonable resource usage"
  echo "2. High Performance - Optimized for maximum throughput and low latency"
  echo "3. Cost Optimized - Designed to minimize resource usage and cost"
  echo "4. High Availability - Maximized for resilience and uptime"
  echo "5. Local Development - For running with 'dotnet run' on local machine with localhost backends"
  echo "6. Container Development - For containerized development and testing scenarios"
  
  read -p "Select an environment template (1-6) [1]: " env_template_choice
  env_template_choice=${env_template_choice:-1}
  
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
      echo "Invalid environment template selection. Updating only the image."
      env_template_file=""
      ;;
  esac
  
  if [ -n "$env_template_file" ] && [ -f "$env_template_file" ]; then
    echo "Applying environment template: $env_template_name"
    
    # Create a temporary JSON file for environment variables
    temp_env_json=$(mktemp)
    echo "[" > "$temp_env_json"
    
    # Parse the env file and convert to JSON format
    first_var=true
    while IFS= read -r line || [ -n "$line" ]; do
      # Skip comments and empty lines
      if [[ "$line" =~ ^[[:space:]]*# ]] || [[ -z "${line// /}" ]]; then
        continue
      fi
      
      # Extract key and value
      if [[ "$line" =~ ^([^=]+)=(.*)$ ]]; then
        key="${BASH_REMATCH[1]}"
        value="${BASH_REMATCH[2]}"
        
        # Remove quotes if present
        value="${value#\"}"
        value="${value%\"}"
        
        # Add comma if not the first variable
        if [ "$first_var" = true ]; then
          first_var=false
        else
          echo "," >> "$temp_env_json"
        fi
        
        # Write to JSON
        echo "  {\"name\": \"$key\", \"value\": \"$value\"}" >> "$temp_env_json"
      fi
    done < "$env_template_file"
    
    echo "]" >> "$temp_env_json"
    
    # Update the container app with the new image and environment variables
    az containerapp update \
      --name "$AZURE_CONTAINER_APP_NAME" \
      --resource-group "$AZURE_RESOURCE_GROUP" \
      --image "${ACR_LOGIN_SERVER}/simple-l7-proxy:${VERSION}" \
      --set-env-vars @"$temp_env_json"
    
    # Clean up the temporary file
    rm -f "$temp_env_json"
  else
    echo "Environment template file not found or invalid. Updating only the image."
    az containerapp update \
      --name "$AZURE_CONTAINER_APP_NAME" \
      --resource-group "$AZURE_RESOURCE_GROUP" \
      --image "${ACR_LOGIN_SERVER}/simple-l7-proxy:${VERSION}"
  fi
else
  # Just update the image
  az containerapp update \
    --name "$AZURE_CONTAINER_APP_NAME" \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --image "${ACR_LOGIN_SERVER}/simple-l7-proxy:${VERSION}"
fi

echo "Deployment complete!"
echo "Container App URL: https://$(az containerapp show --name $AZURE_CONTAINER_APP_NAME --resource-group $AZURE_RESOURCE_GROUP --query properties.configuration.ingress.fqdn -o tsv)"