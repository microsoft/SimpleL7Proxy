#!/bin/bash

# Deploy HealthProbe Container App with Sidecar
# This script deploys or updates an Azure Container App with web and health sidecar containers

set -e

# Configuration Variables
RESOURCE_GROUP="TR-apim"
LOCATION="eastus"
CONTAINER_APP_NAME="simplel7dev"
ENVIRONMENT_NAME="simplelL7Proxy"

# Container Images
WEB_IMAGE="nvmacr.azurecr.io/myproxy:v2.2.8.d15"
HEALTH_IMAGE="nvmacr.azurecr.io/healthprobe:v2.2.8.d15"

# Azure Container Registry (used with system-assigned managed identity)
REGISTRY_SERVER="nvmacr.azurecr.io"  # Set to your ACR login server

# Resource Configuration
WEB_CPU=0.5
WEB_MEMORY=1.0
HEALTH_CPU=0.25
HEALTH_MEMORY=0.5

# Network Configuration
WEB_PORT=8000
HEALTH_PORT=9000
INGRESS_TYPE="external"  # or "internal"
ENABLE_HTTPS=true
REVISION_MODE="single"  # or "multiple"

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

# Deploy using Bicep
echo -e "${YELLOW}Deploying Container App with Bicep...${NC}"
DEPLOYMENT_NAME="healthprobe-deployment-$(date +%s)"

az deployment group create --debug \
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
