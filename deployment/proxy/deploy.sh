#!/bin/bash

# Deploy HealthProbe Container App with Sidecar
# This script deploys or updates an Azure Container App with web and health sidecar containers

set -e

# Source parameters file - prefer consolidated parent file
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PARENT_PARAMS="${SCRIPT_DIR}/../deploy.parameters.sh"
if [ -f "${PARENT_PARAMS}" ]; then
    echo "Sourcing ${PARENT_PARAMS}..."
    # shellcheck disable=SC1091
    source "${PARENT_PARAMS}"
elif [ -f "$SCRIPT_DIR/deploy.parameters.sh" ]; then
    echo "Sourcing legacy deploy.parameters.sh..."
    source "$SCRIPT_DIR/deploy.parameters.sh"
fi

# Map consolidated variable names to those expected by this script
RESOURCE_GROUP="${RESOURCE_GROUP:-${CONTAINER_APP_RESOURCE_GROUP:-}}"

# Configuration Variables (use environment variables if set, otherwise use defaults)
RESOURCE_GROUP="${RESOURCE_GROUP:-TR-apim}"
LOCATION="${LOCATION:-eastus}"
CONTAINER_APP_NAME="${CONTAINER_APP_NAME:-simplel7dev}"
ENVIRONMENT_NAME="${ENVIRONMENT_NAME:-simplelL7Proxy}"

# Container Images (must be set via environment or deploy.parameters.sh)
WEB_IMAGE="${WEB_IMAGE:-}"
HEALTH_IMAGE="${HEALTH_IMAGE:-}"

# Validate required images based on deployment type
if [ -z "$WEB_IMAGE" ]; then
    echo -e "${RED}Error: WEB_IMAGE must be set.${NC}"
    echo "Either set it as an environment variable or create deploy.parameters.sh"
    echo "See deploy.parameters.example.sh for reference."
    exit 1
fi

if [ "${HEALTHPROBE_TYPE:-sidecar}" = "sidecar" ] && [ -z "$HEALTH_IMAGE" ]; then
    echo -e "${RED}Error: HEALTH_IMAGE must be set for sidecar deployments.${NC}"
    echo "Either set it as an environment variable or create deploy.parameters.sh"
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
TERMINATION_GRACE_PERIOD_SECONDS="${TERMINATION_GRACE_PERIOD_SECONDS:-30}"

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
SUBSCRIPTION_ID=$(az account show --query id -o tsv | tr -d '\r')
echo -e "${GREEN}Using subscription: ${SUBSCRIPTION_ID}${NC}"

echo -e "${YELLOW}Ensuring resource group ${RESOURCE_GROUP} exists...${NC}"
GROUP_CREATE_ERROR_FILE="$(mktemp)"
if ! az group create --name "$RESOURCE_GROUP" --location "$LOCATION" --output none 2>"${GROUP_CREATE_ERROR_FILE}"; then
    if grep -q "ResourceGroupBeingDeleted" "${GROUP_CREATE_ERROR_FILE}"; then
        echo -e "${RED}Error: Resource group '${RESOURCE_GROUP}' is currently being deleted.${NC}"
        echo -e "${YELLOW}Wait for deletion to finish, or update CONTAINER_APP_RESOURCE_GROUP in deploy.parameters.sh to a different name and rerun this step.${NC}"
    else
        cat "${GROUP_CREATE_ERROR_FILE}" >&2
    fi
    rm -f "${GROUP_CREATE_ERROR_FILE}"
    exit 1
fi
rm -f "${GROUP_CREATE_ERROR_FILE}"
echo -e "${GREEN}✓ Resource group ready${NC}"

# Get or create Container Apps Environment
echo -e "${YELLOW}Getting Container Apps Environment...${NC}"
MANAGED_ENV_ID=$(az containerapp env show \
    --name "$ENVIRONMENT_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --query id -o tsv 2>/dev/null | tr -d '\r' || echo "")

wait_for_managed_env() {
    local env_id=""
    local provisioning_state=""
    local attempts=0
    local max_attempts=60

    echo -e "${YELLOW}Waiting for Container Apps Environment to finish provisioning...${NC}"
    while [[ $attempts -lt $max_attempts ]]; do
        env_id=$(az containerapp env show \
            --name "$ENVIRONMENT_NAME" \
            --resource-group "$RESOURCE_GROUP" \
            --query id -o tsv 2>/dev/null || echo "")
        provisioning_state=$(az containerapp env show \
            --name "$ENVIRONMENT_NAME" \
            --resource-group "$RESOURCE_GROUP" \
            --query "properties.provisioningState" -o tsv 2>/dev/null || echo "")

        if [ -n "$env_id" ] && [ "$provisioning_state" = "Succeeded" ]; then
            MANAGED_ENV_ID="$env_id"
            echo -e "${GREEN}✓ Container Apps Environment is ready${NC}"
            return 0
        fi

        attempts=$((attempts + 1))
        sleep 10
    done

    echo -e "${RED}Error: Container Apps Environment did not become ready in time.${NC}"
    echo -e "${YELLOW}Last observed provisioning state: ${provisioning_state:-unknown}${NC}"
    exit 1
}

if [ -z "$MANAGED_ENV_ID" ]; then
    echo -e "${YELLOW}Container Apps Environment not found. Creating...${NC}"
    az containerapp env create \
        --name "$ENVIRONMENT_NAME" \
        --resource-group "$RESOURCE_GROUP" \
        --location "$LOCATION"
    
    MANAGED_ENV_ID=$(az containerapp env show \
        --name "$ENVIRONMENT_NAME" \
        --resource-group "$RESOURCE_GROUP" \
        --query id -o tsv | tr -d '\r')
    
    wait_for_managed_env
else
    wait_for_managed_env
fi

echo -e "${GREEN}Using environment: ${MANAGED_ENV_ID}${NC}"

# Build Bicep parameters based on deployment type
BICEP_PARAMS="containerAppName=$CONTAINER_APP_NAME"
BICEP_PARAMS="$BICEP_PARAMS managedEnvId=$MANAGED_ENV_ID"
BICEP_PARAMS="$BICEP_PARAMS location=$LOCATION"
BICEP_PARAMS="$BICEP_PARAMS webImage=$WEB_IMAGE"
BICEP_PARAMS="$BICEP_PARAMS webCpu=$WEB_CPU"
BICEP_PARAMS="$BICEP_PARAMS webMemory=${WEB_MEMORY}Gi"
BICEP_PARAMS="$BICEP_PARAMS webPort=$WEB_PORT"
BICEP_PARAMS="$BICEP_PARAMS ingressType=$INGRESS_TYPE"
BICEP_PARAMS="$BICEP_PARAMS enableHttps=$ENABLE_HTTPS"
BICEP_PARAMS="$BICEP_PARAMS revisionMode=$REVISION_MODE"
BICEP_PARAMS="$BICEP_PARAMS terminationGracePeriodSeconds=$TERMINATION_GRACE_PERIOD_SECONDS"

# Add sidecar-specific parameters if using sidecar deployment
if [ "${HEALTHPROBE_TYPE:-sidecar}" = "sidecar" ]; then
    BICEP_PARAMS="$BICEP_PARAMS healthImage=$HEALTH_IMAGE"
    BICEP_PARAMS="$BICEP_PARAMS healthCpu=$HEALTH_CPU"
    BICEP_PARAMS="$BICEP_PARAMS healthMemory=${HEALTH_MEMORY}Gi"
    BICEP_PARAMS="$BICEP_PARAMS healthPort=$HEALTH_PORT"
fi

# Add registry parameters if configured
if [ -n "$REGISTRY_SERVER" ]; then
    BICEP_PARAMS="$BICEP_PARAMS registryServer=$REGISTRY_SERVER"
fi

if [ -n "$REGISTRY_SERVER" ]; then
    echo -e "${YELLOW}Verifying configured images exist in ACR...${NC}"
    IMAGES_TO_VERIFY=("$WEB_IMAGE")
    if [ "${HEALTHPROBE_TYPE:-sidecar}" = "sidecar" ]; then
        IMAGES_TO_VERIFY+=("$HEALTH_IMAGE")
    fi
    
    for IMAGE_REF in "${IMAGES_TO_VERIFY[@]}"; do
        IMAGE_REPO_TAG="${IMAGE_REF#${REGISTRY_SERVER}/}"
        IMAGE_REPO="${IMAGE_REPO_TAG%:*}"
        IMAGE_TAG="${IMAGE_REPO_TAG##*:}"

        if [ "$IMAGE_REPO_TAG" = "$IMAGE_REF" ] || [ "$IMAGE_REPO" = "$IMAGE_REPO_TAG" ] || [ -z "$IMAGE_REPO" ] || [ -z "$IMAGE_TAG" ]; then
            echo -e "${RED}Error: Image '$IMAGE_REF' must be in the form ${REGISTRY_SERVER}/repository:tag.${NC}"
            exit 1
        fi

        if az acr repository show-tags \
            --name "${REGISTRY_SERVER%%.*}" \
            --repository "$IMAGE_REPO" \
            --query "[?@=='$IMAGE_TAG'] | [0]" -o tsv 2>/dev/null | tr -d '\r' | grep -qx "$IMAGE_TAG"; then
            echo -e "${GREEN}✓ Found ${IMAGE_REPO}:${IMAGE_TAG}${NC}"
        else
            echo -e "${RED}Error: Missing ACR image ${IMAGE_REPO}:${IMAGE_TAG}.${NC}"
            echo -e "${YELLOW}Run ../ContainerImage/validate-acr.sh, then ../ContainerImage/deploy.sh before deploying the Container App.${NC}"
            exit 1
        fi
    done
fi

# Note: Host1, Workers, Port, AsyncModeEnabled, HealthProbeSidecar are now
# served from Azure App Configuration (Step 7). They are no longer baked
# into the Container App env vars.

# Ensure the Container App has a managed identity and AcrPull before deploying private ACR images.
echo -e "${YELLOW}Checking Container App managed identity and ACR access...${NC}"
EXISTING_APP_NAME=$(az containerapp show \
    --name "$CONTAINER_APP_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --query "name" -o tsv 2>/dev/null | tr -d '\r' || echo "")

if [ -z "$EXISTING_APP_NAME" ] && [ -n "$REGISTRY_SERVER" ]; then
    echo -e "${YELLOW}Container App doesn't exist yet. Creating placeholder app to establish managed identity...${NC}"
    az containerapp create \
        --name "$CONTAINER_APP_NAME" \
        --resource-group "$RESOURCE_GROUP" \
        --environment "$ENVIRONMENT_NAME" \
        --image "mcr.microsoft.com/azuredocs/containerapps-helloworld:latest" \
        --target-port "$WEB_PORT" \
        --ingress "$INGRESS_TYPE" \
        --system-assigned \
        --min-replicas 0 \
        --max-replicas 1 \
        --output none
fi

EXISTING_APP_PRINCIPAL_ID=$(az containerapp show \
    --name "$CONTAINER_APP_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --query "identity.principalId" -o tsv 2>/dev/null | tr -d '\r' || echo "")

if [ -z "$EXISTING_APP_PRINCIPAL_ID" ] || [ "$EXISTING_APP_PRINCIPAL_ID" = "null" ]; then
    echo -e "${YELLOW}Enabling system-assigned managed identity...${NC}"
    az containerapp identity assign \
        --name "$CONTAINER_APP_NAME" \
        --resource-group "$RESOURCE_GROUP" \
        --system-assigned \
        --output none

    EXISTING_APP_PRINCIPAL_ID=$(az containerapp show \
        --name "$CONTAINER_APP_NAME" \
        --resource-group "$RESOURCE_GROUP" \
        --query "identity.principalId" -o tsv | tr -d '\r')
fi

if [ -n "$EXISTING_APP_PRINCIPAL_ID" ] && [ -n "$REGISTRY_SERVER" ]; then
    echo -e "${YELLOW}Ensuring AcrPull role for Container App managed identity...${NC}"
    ACR_NAME=$(echo "$REGISTRY_SERVER" | cut -d'.' -f1)
    ACR_RESOURCE_ID=$(az acr show --name "$ACR_NAME" --query id -o tsv 2>/dev/null | tr -d '\r' || echo "")
    
    if [ -n "$ACR_RESOURCE_ID" ]; then
        ROLE_EXISTS=$(az role assignment list \
            --assignee "$EXISTING_APP_PRINCIPAL_ID" \
            --role "AcrPull" \
            --scope "$ACR_RESOURCE_ID" \
            --query "[0].id" -o tsv 2>/dev/null | tr -d '\r' || echo "")

        if [ -n "$ROLE_EXISTS" ]; then
            echo -e "${GREEN}AcrPull role already assigned${NC}"
        else
            az role assignment create \
                --assignee "$EXISTING_APP_PRINCIPAL_ID" \
                --role "AcrPull" \
                --scope "$ACR_RESOURCE_ID" \
                --output none
            echo -e "${GREEN}AcrPull role assigned${NC}"
        fi
    else
        echo -e "${RED}Error: Could not find ACR '$ACR_NAME'.${NC}"
        exit 1
    fi
fi

# Deploy using Bicep
echo -e "${YELLOW}Deploying Container App with Bicep...${NC}"
DEPLOYMENT_NAME="healthprobe-deployment-$(date +%s)"

# Select appropriate Bicep template based on deployment type
if [ "${HEALTHPROBE_TYPE:-sidecar}" = "sidecar" ]; then
    BICEP_TEMPLATE="$(dirname "$0")/script.bicep"
else
    BICEP_TEMPLATE="$(dirname "$0")/script-nosidecar.bicep"
fi

#az deployment group create --debug \
az deployment group create  \
    --name "$DEPLOYMENT_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --template-file "$BICEP_TEMPLATE" \
    --parameters $BICEP_PARAMS \
    --query "properties.outputs" -o json

# Get deployment outputs
echo -e "${YELLOW}Retrieving deployment outputs...${NC}"
FQDN=$(az deployment group show \
    --name "$DEPLOYMENT_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --query "properties.outputs.fqdn.value" -o tsv | tr -d '\r')

RESOURCE_ID=$(az deployment group show \
    --name "$DEPLOYMENT_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --query "properties.outputs.resourceId.value" -o tsv | tr -d '\r')

REVISION_NAME=$(az deployment group show \
    --name "$DEPLOYMENT_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --query "properties.outputs.latestRevisionName.value" -o tsv | tr -d '\r')

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
