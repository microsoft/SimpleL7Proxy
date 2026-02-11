#!/bin/bash

# Setup script for first-time Container App deployment
# This script creates the Container App with managed identity and grants ACR pull permissions
# Run this ONCE before the first deploy.sh execution

set -e

# Source parameters file if it exists
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ -f "$SCRIPT_DIR/deploy.parameters.sh" ]; then
    echo "Sourcing deploy.parameters.sh..."
    source "$SCRIPT_DIR/deploy.parameters.sh"
fi

# Configuration Variables (use environment variables if set, otherwise use defaults)
RESOURCE_GROUP="${RESOURCE_GROUP:-}"
LOCATION="${LOCATION:-eastus}"
CONTAINER_APP_NAME="${CONTAINER_APP_NAME:-}"
ENVIRONMENT_NAME="${ENVIRONMENT_NAME:-}"
REGISTRY_SERVER="${REGISTRY_SERVER:-}"

# Colors for output
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m' # No Color

echo -e "${GREEN}======================================${NC}"
echo -e "${GREEN}Container App Setup Script${NC}"
echo -e "${GREEN}======================================${NC}"

# Validate required parameters
if [ -z "$RESOURCE_GROUP" ]; then
    echo -e "${RED}Error: RESOURCE_GROUP must be set${NC}"
    exit 1
fi

if [ -z "$CONTAINER_APP_NAME" ]; then
    echo -e "${RED}Error: CONTAINER_APP_NAME must be set${NC}"
    exit 1
fi

if [ -z "$ENVIRONMENT_NAME" ]; then
    echo -e "${RED}Error: ENVIRONMENT_NAME must be set${NC}"
    exit 1
fi

if [ -z "$REGISTRY_SERVER" ]; then
    echo -e "${RED}Error: REGISTRY_SERVER must be set for ACR role assignment${NC}"
    exit 1
fi

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

# Create resource group if it doesn't exist
echo -e "${YELLOW}Checking resource group...${NC}"
az group show --name "$RESOURCE_GROUP" &> /dev/null || {
    echo -e "${YELLOW}Creating resource group '$RESOURCE_GROUP' in '$LOCATION'...${NC}"
    az group create --name "$RESOURCE_GROUP" --location "$LOCATION"
}

# Get or create Container Apps Environment
echo -e "${YELLOW}Checking Container Apps Environment...${NC}"
MANAGED_ENV_ID=$(az containerapp env show \
    --name "$ENVIRONMENT_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --query id -o tsv 2>/dev/null || echo "")

if [ -z "$MANAGED_ENV_ID" ]; then
    echo -e "${YELLOW}Creating Container Apps Environment '$ENVIRONMENT_NAME'...${NC}"
    az containerapp env create \
        --name "$ENVIRONMENT_NAME" \
        --resource-group "$RESOURCE_GROUP" \
        --location "$LOCATION"
    
    MANAGED_ENV_ID=$(az containerapp env show \
        --name "$ENVIRONMENT_NAME" \
        --resource-group "$RESOURCE_GROUP" \
        --query id -o tsv)
fi

echo -e "${GREEN}Using environment: ${ENVIRONMENT_NAME}${NC}"

# Check if Container App already exists
echo -e "${YELLOW}Checking if Container App exists...${NC}"
EXISTING_APP=$(az containerapp show \
    --name "$CONTAINER_APP_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --query name -o tsv 2>/dev/null || echo "")

if [ -n "$EXISTING_APP" ]; then
    echo -e "${YELLOW}Container App '$CONTAINER_APP_NAME' already exists.${NC}"
    PRINCIPAL_ID=$(az containerapp show \
        --name "$CONTAINER_APP_NAME" \
        --resource-group "$RESOURCE_GROUP" \
        --query "identity.principalId" -o tsv)
    
    if [ -z "$PRINCIPAL_ID" ] || [ "$PRINCIPAL_ID" = "null" ]; then
        echo -e "${YELLOW}Enabling system-assigned managed identity...${NC}"
        az containerapp identity assign \
            --name "$CONTAINER_APP_NAME" \
            --resource-group "$RESOURCE_GROUP" \
            --system-assigned
        
        PRINCIPAL_ID=$(az containerapp show \
            --name "$CONTAINER_APP_NAME" \
            --resource-group "$RESOURCE_GROUP" \
            --query "identity.principalId" -o tsv)
    fi
else
    # Create Container App with a placeholder public image to get managed identity
    echo -e "${YELLOW}Creating Container App '$CONTAINER_APP_NAME' with placeholder image...${NC}"
    az containerapp create \
        --name "$CONTAINER_APP_NAME" \
        --resource-group "$RESOURCE_GROUP" \
        --environment "$ENVIRONMENT_NAME" \
        --image "mcr.microsoft.com/azuredocs/containerapps-helloworld:latest" \
        --target-port 8000 \
        --ingress external \
        --system-assigned \
        --min-replicas 0 \
        --max-replicas 1

    PRINCIPAL_ID=$(az containerapp show \
        --name "$CONTAINER_APP_NAME" \
        --resource-group "$RESOURCE_GROUP" \
        --query "identity.principalId" -o tsv)
fi

echo -e "${GREEN}Container App managed identity principal ID: ${PRINCIPAL_ID}${NC}"

# Extract ACR name from registry server
ACR_NAME=$(echo "$REGISTRY_SERVER" | cut -d'.' -f1)
echo -e "${YELLOW}Getting ACR resource ID for '$ACR_NAME'...${NC}"

ACR_RESOURCE_ID=$(az acr show --name "$ACR_NAME" --query id -o tsv 2>/dev/null || echo "")

if [ -z "$ACR_RESOURCE_ID" ]; then
    echo -e "${RED}Error: Could not find ACR '$ACR_NAME'${NC}"
    echo -e "${RED}Make sure the ACR exists and you have access to it.${NC}"
    exit 1
fi

echo -e "${GREEN}ACR Resource ID: ${ACR_RESOURCE_ID}${NC}"

# Assign AcrPull role
echo -e "${YELLOW}Assigning AcrPull role to Container App managed identity...${NC}"
ROLE_EXISTS=$(az role assignment list \
    --assignee "$PRINCIPAL_ID" \
    --role "AcrPull" \
    --scope "$ACR_RESOURCE_ID" \
    --query "[0].id" -o tsv 2>/dev/null || echo "")

if [ -n "$ROLE_EXISTS" ]; then
    echo -e "${GREEN}AcrPull role already assigned.${NC}"
else
    az role assignment create \
        --assignee "$PRINCIPAL_ID" \
        --role "AcrPull" \
        --scope "$ACR_RESOURCE_ID"
    echo -e "${GREEN}AcrPull role assigned successfully.${NC}"
fi

# Wait for role propagation
echo -e "${YELLOW}Waiting 60 seconds for role assignment to propagate...${NC}"
sleep 60

echo -e "${GREEN}======================================${NC}"
echo -e "${GREEN}Setup Complete!${NC}"
echo -e "${GREEN}======================================${NC}"
echo -e "${GREEN}Container App: ${CONTAINER_APP_NAME}${NC}"
echo -e "${GREEN}Resource Group: ${RESOURCE_GROUP}${NC}"
echo -e "${GREEN}Environment: ${ENVIRONMENT_NAME}${NC}"
echo -e "${GREEN}ACR: ${ACR_NAME}${NC}"
echo -e "${GREEN}======================================${NC}"
echo ""
echo -e "${YELLOW}You can now run deploy.sh to deploy your actual container images:${NC}"
echo -e "${GREEN}  ./deploy.sh${NC}"
echo ""
