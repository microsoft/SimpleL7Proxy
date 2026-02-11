#!/bin/bash

# Deploy HealthProbe Container App with Sidecar
# This script deploys or updates an Azure Container App with web and health sidecar containers

set -e

# Source parameters file if it exists
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ -f "$SCRIPT_DIR/deploy.parameters.sh" ]; then
    echo "Sourcing deploy.parameters.sh..."
    source "$SCRIPT_DIR/deploy.parameters.sh"
fi

# Configuration Variables (use environment variables if set, otherwise use defaults)
RESOURCE_GROUP="${RESOURCE_GROUP:-TR-apim}"
LOCATION="${LOCATION:-eastus}"
CONTAINER_APP_NAME="${CONTAINER_APP_NAME:-simplel7dev}"
ENVIRONMENT_NAME="${ENVIRONMENT_NAME:-simplelL7Proxy}"

# Container Images (must be set via environment or deploy.parameters.sh)
WEB_IMAGE="${WEB_IMAGE:-}"
HEALTH_IMAGE="${HEALTH_IMAGE:-}"

# Validate required images
if [ -z "$WEB_IMAGE" ] || [ -z "$HEALTH_IMAGE" ]; then
    echo -e "${RED}Error: WEB_IMAGE and HEALTH_IMAGE must be set.${NC}"
    echo "Either set them as environment variables or create deploy.parameters.sh"
    echo "See deploy.parameters.example.sh for reference."
    exit 1
fi

# Azure Container Registry (used with system-assigned managed identity)
REGISTRY_SERVER="${REGISTRY_SERVER:-}"  # Set to your ACR login server

# Resource Configuration
WEB_CPU="${WEB_CPU:-0.5}"
WEB_MEMORY="${WEB_MEMORY:-1.0}"
HEALTH_CPU="${HEALTH_CPU:-0.25}"
HEALTH_MEMORY="${HEALTH_MEMORY:-0.5}"

# Network Configuration
WEB_PORT="${WEB_PORT:-8000}"
HEALTH_PORT="${HEALTH_PORT:-9000}"
INGRESS_TYPE="${INGRESS_TYPE:-external}"  # or "internal"
ENABLE_HTTPS="${ENABLE_HTTPS:-true}"
REVISION_MODE="${REVISION_MODE:-single}"  # or "multiple"

# Colors for output
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m' # No Color

echo -e "${GREEN}======================================${NC}"
echo -e "${GREEN}HealthProbe Container App Deployment${NC}"
echo -e "${GREEN}======================================${NC}"

# Check if Azure CLI is installed
if ! command -v az &> /dev/null; then
    echo -e "${RED}Error: Azure CLI is not installed${NC}"
    exit 1
fi

# Check if logged in
echo -e "${YELLOW}Checking Azure login status...${NC}"
az account show &> /dev/null || {
    echo -e "${RED}Not logged in to Azure. Running 'az login'...${NC}"
    az login
}

# Get current subscription
SUBSCRIPTION_ID=$(az account show --query id -o tsv)
echo -e "${GREEN}Using subscription: ${SUBSCRIPTION_ID}${NC}"

# Get or create Container Apps Environment
echo -e "${YELLOW}Getting Container Apps Environment...${NC}"
MANAGED_ENV_ID=$(az containerapp env show \
    --name "$ENVIRONMENT_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --query id -o tsv 2>/dev/null || echo "")

if [ -z "$MANAGED_ENV_ID" ]; then
    echo -e "${YELLOW}Container Apps Environment not found. Creating...${NC}"
    az containerapp env create \
        --name "$ENVIRONMENT_NAME" \
        --resource-group "$RESOURCE_GROUP" \
        --location "$LOCATION"
    
    MANAGED_ENV_ID=$(az containerapp env show \
        --name "$ENVIRONMENT_NAME" \
        --resource-group "$RESOURCE_GROUP" \
        --query id -o tsv)
fi

echo -e "${GREEN}Using environment: ${MANAGED_ENV_ID}${NC}"

# Build Bicep parameters
BICEP_PARAMS="containerAppName=$CONTAINER_APP_NAME"
BICEP_PARAMS="$BICEP_PARAMS managedEnvId=$MANAGED_ENV_ID"
BICEP_PARAMS="$BICEP_PARAMS location=$LOCATION"
BICEP_PARAMS="$BICEP_PARAMS webImage=$WEB_IMAGE"
BICEP_PARAMS="$BICEP_PARAMS healthImage=$HEALTH_IMAGE"
BICEP_PARAMS="$BICEP_PARAMS webCpu=$WEB_CPU"
BICEP_PARAMS="$BICEP_PARAMS webMemory=${WEB_MEMORY}Gi"
BICEP_PARAMS="$BICEP_PARAMS healthCpu=$HEALTH_CPU"
BICEP_PARAMS="$BICEP_PARAMS healthMemory=${HEALTH_MEMORY}Gi"
BICEP_PARAMS="$BICEP_PARAMS webPort=$WEB_PORT"
BICEP_PARAMS="$BICEP_PARAMS healthPort=$HEALTH_PORT"
BICEP_PARAMS="$BICEP_PARAMS ingressType=$INGRESS_TYPE"
BICEP_PARAMS="$BICEP_PARAMS enableHttps=$ENABLE_HTTPS"
BICEP_PARAMS="$BICEP_PARAMS revisionMode=$REVISION_MODE"

# Add registry parameters if configured
if [ -n "$REGISTRY_SERVER" ]; then
    BICEP_PARAMS="$BICEP_PARAMS registryServer=$REGISTRY_SERVER"
fi

# Add Host1 configuration
if [ -n "$HOST1" ]; then
    BICEP_PARAMS="$BICEP_PARAMS host1=$HOST1"
else
    echo -e "${RED}Error: HOST1 must be set${NC}"
    exit 1
fi

# Check if Container App exists and grant ACR pull permission to its managed identity
echo -e "${YELLOW}Checking if Container App exists for ACR role assignment...${NC}"
EXISTING_APP_PRINCIPAL_ID=$(az containerapp show \
    --name "$CONTAINER_APP_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --query "identity.principalId" -o tsv 2>/dev/null || echo "")

if [ -n "$EXISTING_APP_PRINCIPAL_ID" ] && [ -n "$REGISTRY_SERVER" ]; then
    echo -e "${YELLOW}Granting AcrPull role to Container App managed identity...${NC}"
    # Extract ACR name from registry server (e.g., nvmacr.azurecr.io -> nvmacr)
    ACR_NAME=$(echo "$REGISTRY_SERVER" | cut -d'.' -f1)
    ACR_RESOURCE_ID=$(az acr show --name "$ACR_NAME" --query id -o tsv 2>/dev/null || echo "")
    
    if [ -n "$ACR_RESOURCE_ID" ]; then
        az role assignment create \
            --assignee "$EXISTING_APP_PRINCIPAL_ID" \
            --role "AcrPull" \
            --scope "$ACR_RESOURCE_ID" \
            2>/dev/null || echo -e "${YELLOW}Role assignment already exists or failed (continuing...)${NC}"
        echo -e "${GREEN}ACR role assignment configured${NC}"
    else
        echo -e "${YELLOW}Warning: Could not find ACR '$ACR_NAME'. Role assignment skipped.${NC}"
    fi
else
    echo -e "${YELLOW}Container App doesn't exist yet. ACR role will be assigned after first deployment.${NC}"
fi

# Deploy using Bicep
echo -e "${YELLOW}Deploying Container App with Bicep...${NC}"
DEPLOYMENT_NAME="healthprobe-deployment-$(date +%s)"

#az deployment group create --debug \
az deployment group create  \
    --name "$DEPLOYMENT_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --template-file "$(dirname "$0")/script.bicep" \
    --parameters $BICEP_PARAMS \
    --query "properties.outputs" -o json

# Get deployment outputs
echo -e "${YELLOW}Retrieving deployment outputs...${NC}"
FQDN=$(az deployment group show \
    --name "$DEPLOYMENT_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --query "properties.outputs.fqdn.value" -o tsv)

RESOURCE_ID=$(az deployment group show \
    --name "$DEPLOYMENT_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --query "properties.outputs.resourceId.value" -o tsv)

REVISION_NAME=$(az deployment group show \
    --name "$DEPLOYMENT_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --query "properties.outputs.latestRevisionName.value" -o tsv)

# If this was first deployment, assign ACR role now that managed identity exists
if [ -z "$EXISTING_APP_PRINCIPAL_ID" ] && [ -n "$REGISTRY_SERVER" ]; then
    echo -e "${YELLOW}Assigning AcrPull role to newly created Container App managed identity...${NC}"
    NEW_PRINCIPAL_ID=$(az containerapp show \
        --name "$CONTAINER_APP_NAME" \
        --resource-group "$RESOURCE_GROUP" \
        --query "identity.principalId" -o tsv)
    
    if [ -n "$NEW_PRINCIPAL_ID" ]; then
        ACR_NAME=$(echo "$REGISTRY_SERVER" | cut -d'.' -f1)
        ACR_RESOURCE_ID=$(az acr show --name "$ACR_NAME" --query id -o tsv 2>/dev/null || echo "")
        
        if [ -n "$ACR_RESOURCE_ID" ]; then
            az role assignment create \
                --assignee "$NEW_PRINCIPAL_ID" \
                --role "AcrPull" \
                --scope "$ACR_RESOURCE_ID" \
                2>/dev/null || echo -e "${YELLOW}Role assignment already exists or failed${NC}"
            echo -e "${GREEN}ACR role assignment configured for new Container App${NC}"
            echo -e "${YELLOW}Note: You may need to re-deploy for the role assignment to take effect.${NC}"
        fi
    fi
fi

echo -e "${GREEN}======================================${NC}"
echo -e "${GREEN}Deployment Complete!${NC}"
echo -e "${GREEN}======================================${NC}"
echo -e "${GREEN}FQDN: ${FQDN}${NC}"
echo -e "${GREEN}Resource ID: ${RESOURCE_ID}${NC}"
echo -e "${GREEN}Latest Revision: ${REVISION_NAME}${NC}"
echo -e "${GREEN}======================================${NC}"

if [ "$INGRESS_TYPE" = "external" ]; then
    PROTOCOL="https"
    if [ "$ENABLE_HTTPS" = "false" ]; then
        PROTOCOL="http"
    fi
    echo -e "${YELLOW}Access your app at: ${PROTOCOL}://${FQDN}${NC}"
fi

echo -e "${GREEN}Done!${NC}"
