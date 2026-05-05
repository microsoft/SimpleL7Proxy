#!/bin/bash

# Deploy/Update Azure Container Apps (ACA) Environment and Container App
# This creates an internal ACA environment that integrates with the VNet
# and only accepts traffic from within the network.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [ -f "${SCRIPT_DIR}/deploy.parameters.sh" ]; then
    echo "Sourcing deploy.parameters.sh..."
    # shellcheck disable=SC1091
    source "${SCRIPT_DIR}/deploy.parameters.sh"
elif [ -f "${SCRIPT_DIR}/deploy.parameters.example.sh" ]; then
    echo "deploy.parameters.sh not found."
    echo "Copy deploy.parameters.example.sh to deploy.parameters.sh and update values."
    echo "Example: cp deploy.parameters.example.sh deploy.parameters.sh"
    exit 1
fi

# Required parameters
RESOURCE_GROUP="${RESOURCE_GROUP:?'RESOURCE_GROUP must be set'}"
LOCATION="${LOCATION:?'LOCATION must be set'}"
VNET_NAME="${VNET_NAME:?'VNET_NAME must be set'}"
SUBNET_ACA_NAME="${SUBNET_ACA_NAME:?'SUBNET_ACA_NAME must be set'}"
ACA_ENVIRONMENT_NAME="${ACA_ENVIRONMENT_NAME:?'ACA_ENVIRONMENT_NAME must be set'}"
CONTAINER_APP_NAME="${CONTAINER_APP_NAME:?'CONTAINER_APP_NAME must be set'}"
IMAGE_NAME="${IMAGE_NAME:?'IMAGE_NAME must be set'}"

# Optional parameters
CPU="${CPU:-0.5}"
MEMORY="${MEMORY:-1.0Gi}"
MIN_REPLICAS="${MIN_REPLICAS:-1}"
MAX_REPLICAS="${MAX_REPLICAS:-5}"
INGRESS_VISIBILITY="${INGRESS_VISIBILITY:-Internal}"
INGRESS_PORT="${INGRESS_PORT:-8000}"
BACKEND_HOST="${BACKEND_HOST:-}"
ENABLE_MANAGED_IDENTITY="${ENABLE_MANAGED_IDENTITY:-true}"
ENABLE_APP_INSIGHTS="${ENABLE_APP_INSIGHTS:-false}"
LOG_ANALYTICS_WORKSPACE_NAME="${LOG_ANALYTICS_WORKSPACE_NAME:-}"

GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m'

# Preconditions
if ! command -v az >/dev/null 2>&1; then
    echo -e "${RED}Error: Azure CLI is not installed.${NC}"
    exit 1
fi

echo -e "${YELLOW}Checking Azure login status...${NC}"
az account show >/dev/null 2>&1 || az login >/dev/null

SUBSCRIPTION_ID="$(az account show --query id -o tsv)"
echo -e "${GREEN}Using subscription: ${SUBSCRIPTION_ID}${NC}"

# Get subnet ID from VNet
echo -e "${YELLOW}Getting VNet subnet ID...${NC}"
SUBNET_ID=$(az network vnet subnet show \
    --resource-group "${RESOURCE_GROUP}" \
    --vnet-name "${VNET_NAME}" \
    --name "${SUBNET_ACA_NAME}" \
    --query id -o tsv)

if [ -z "${SUBNET_ID}" ]; then
    echo -e "${RED}Error: Could not find subnet ${SUBNET_ACA_NAME} in VNet ${VNET_NAME}.${NC}"
    exit 1
fi

echo -e "${GREEN}Subnet ID: ${SUBNET_ID}${NC}"

# Create or get Log Analytics workspace
if [ "${ENABLE_APP_INSIGHTS}" = "true" ] && [ -n "${LOG_ANALYTICS_WORKSPACE_NAME}" ]; then
    echo -e "${YELLOW}Ensuring Log Analytics workspace exists...${NC}"
    
    if ! az monitor log-analytics workspace show \
        --resource-group "${RESOURCE_GROUP}" \
        --workspace-name "${LOG_ANALYTICS_WORKSPACE_NAME}" >/dev/null 2>&1; then
        
        echo -e "${YELLOW}Creating Log Analytics workspace...${NC}"
        az monitor log-analytics workspace create \
            --resource-group "${RESOURCE_GROUP}" \
            --location "${LOCATION}" \
            --workspace-name "${LOG_ANALYTICS_WORKSPACE_NAME}" \
            >/dev/null
    fi
    
    LOG_ANALYTICS_ID=$(az monitor log-analytics workspace show \
        --resource-group "${RESOURCE_GROUP}" \
        --workspace-name "${LOG_ANALYTICS_WORKSPACE_NAME}" \
        --query id -o tsv)
fi

# Create or update Container Apps Environment
echo -e "${YELLOW}Ensuring Container Apps Environment '${ACA_ENVIRONMENT_NAME}' exists...${NC}"

if az containerapp env show \
    --resource-group "${RESOURCE_GROUP}" \
    --name "${ACA_ENVIRONMENT_NAME}" >/dev/null 2>&1; then
    
    echo -e "${GREEN}Using existing Container Apps Environment: ${ACA_ENVIRONMENT_NAME}${NC}"
else
    echo -e "${YELLOW}Creating Container Apps Environment...${NC}"
    
    if [ "${ENABLE_APP_INSIGHTS}" = "true" ] && [ -n "${LOG_ANALYTICS_ID}" ]; then
        az containerapp env create \
            --resource-group "${RESOURCE_GROUP}" \
            --location "${LOCATION}" \
            --name "${ACA_ENVIRONMENT_NAME}" \
            --infrastructure-subnet-resource-id "${SUBNET_ID}" \
            --logs-destination log-analytics \
            --logs-key "${LOG_ANALYTICS_ID}" \
            >/dev/null
    else
        az containerapp env create \
            --resource-group "${RESOURCE_GROUP}" \
            --location "${LOCATION}" \
            --name "${ACA_ENVIRONMENT_NAME}" \
            --infrastructure-subnet-resource-id "${SUBNET_ID}" \
            >/dev/null
    fi
fi

# Create or update Container App
echo -e "${YELLOW}Ensuring Container App '${CONTAINER_APP_NAME}' exists...${NC}"

if az containerapp show \
    --resource-group "${RESOURCE_GROUP}" \
    --name "${CONTAINER_APP_NAME}" >/dev/null 2>&1; then
    
    echo -e "${YELLOW}Updating Container App...${NC}"
    az containerapp update \
        --resource-group "${RESOURCE_GROUP}" \
        --name "${CONTAINER_APP_NAME}" \
        --image "${IMAGE_NAME}" \
        --cpu "${CPU}" \
        --memory "${MEMORY}" \
        --min-replicas "${MIN_REPLICAS}" \
        --max-replicas "${MAX_REPLICAS}" \
        >/dev/null
else
    echo -e "${YELLOW}Creating Container App...${NC}"
    
    # Build environment variables
    ENV_VARS=""
    if [ -n "${BACKEND_HOST}" ]; then
        ENV_VARS="${ENV_VARS} Host1=${BACKEND_HOST}"
    fi
    
    az containerapp create \
        --resource-group "${RESOURCE_GROUP}" \
        --name "${CONTAINER_APP_NAME}" \
        --environment "${ACA_ENVIRONMENT_NAME}" \
        --image "${IMAGE_NAME}" \
        --cpu "${CPU}" \
        --memory "${MEMORY}" \
        --min-replicas "${MIN_REPLICAS}" \
        --max-replicas "${MAX_REPLICAS}" \
        --ingress "${INGRESS_VISIBILITY}" \
        --target-port "${INGRESS_PORT}" \
        --environment-variables ${ENV_VARS} \
        >/dev/null
fi

# Enable managed identity if requested
if [ "${ENABLE_MANAGED_IDENTITY}" = "true" ]; then
    echo -e "${YELLOW}Ensuring system-assigned managed identity is enabled...${NC}"
    
    IDENTITY_CHECK=$(az containerapp show \
        --resource-group "${RESOURCE_GROUP}" \
        --name "${CONTAINER_APP_NAME}" \
        --query "identity.type" -o tsv 2>/dev/null || echo "")
    
    if [ "${IDENTITY_CHECK}" != "SystemAssigned" ]; then
        az containerapp identity assign \
            --resource-group "${RESOURCE_GROUP}" \
            --name "${CONTAINER_APP_NAME}" \
            --system-assigned \
            >/dev/null
    fi
fi

echo -e "${GREEN}======================================${NC}"
echo -e "${GREEN}ACA deployment complete${NC}"
echo -e "${GREEN}======================================${NC}"
echo "Resource Group: ${RESOURCE_GROUP}"
echo "Location: ${LOCATION}"
echo "ACA Environment: ${ACA_ENVIRONMENT_NAME}"
echo "Container App: ${CONTAINER_APP_NAME}"
echo "Image: ${IMAGE_NAME}"
echo "Ingress: ${INGRESS_VISIBILITY} (port ${INGRESS_PORT})"
echo "CPU: ${CPU} | Memory: ${MEMORY}"
echo "Replicas: ${MIN_REPLICAS}-${MAX_REPLICAS}"
