#!/bin/bash

# ACA-specific setup helper for SimpleL7Proxy
echo "========================================================"
echo "  SimpleL7Proxy Azure Container Apps (ACA) Setup"
echo "========================================================="
echo ""

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

echo "🔧 Configuring Container App settings (ACA)..."

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
default_env_ca="${env_AZURE_CONTAINER_APP_ENVIRONMENT_NAME:-}"
container_app_env=$(read_input "Enter Container App Environment name or leave blank for default" "$default_env_ca")
if [ -z "$container_app_env" ]; then
  container_app_env="cae-${environment}-s7p"
fi

echo ""
echo "🔧 Scaling Configuration (ACA)"
default_min="${env_AZURE_CONTAINER_APP_MIN_REPLICAS:-1}"
default_max="${env_AZURE_CONTAINER_APP_MAX_REPLICAS:-5}"
min_replicas=$(read_input "Enter minimum number of replicas" "$default_min")
max_replicas=$(read_input "Enter maximum number of replicas" "$default_max")

echo ""
echo "🔧 Network Configuration (ACA)"
default_vnet="${env_USE_VNET:-no}"
use_vnet=$(read_input "Do you want to deploy in a VNET? (yes/no)" "$default_vnet")
vnet_prefix=""
subnet_prefix=""
if [ "$use_vnet" = "yes" ] || [ "$use_vnet" = "true" ]; then
  use_vnet="true"
  echo ""
  default_vnet_prefix="${env_VNET_ADDRESS_PREFIX:-10.0.0.0/16}"
  default_subnet_prefix="${env_SUBNET_ADDRESS_PREFIX:-10.0.0.0/21}"
  vnet_prefix=$(read_input "Enter VNET address space" "$default_vnet_prefix")
  subnet_prefix=$(read_input "Enter subnet address space" "$default_subnet_prefix")
else
  use_vnet="false"
fi

echo ""
echo "⏳ Applying ACA settings to AZD environment..."

# Ensure AZD environment exists/selected before setting ACA vars
azd env set AZURE_CONTAINER_REGISTRY_NAME "$container_registry"
azd env set AZURE_CONTAINER_APP_NAME "$container_app_name"
azd env set AZURE_CONTAINER_APP_ENVIRONMENT_NAME "$container_app_env"
azd env set AZURE_CONTAINER_APP_MIN_REPLICAS "$min_replicas"
azd env set AZURE_CONTAINER_APP_MAX_REPLICAS "$max_replicas"
azd env set USE_VNET "$use_vnet"
[ "$use_vnet" = "true" ] && azd env set VNET_ADDRESS_PREFIX "$vnet_prefix"
[ "$use_vnet" = "true" ] && azd env set SUBNET_ADDRESS_PREFIX "$subnet_prefix"

echo "✅ ACA settings applied."

echo "📋 ACA Summary:"
echo "   • Container App: $container_app_name"
echo "   • Container Registry: $container_registry"
echo "   • Container App Environment: $container_app_env"
echo "   • Replicas: min=$min_replicas, max=$max_replicas"
echo "   • Use VNET: $use_vnet"
