#!/bin/bash

# Deploy/Update Azure Container App without a sidecar.
# This script deploys a single proxy container into an existing or newly created
# Container Apps environment, while preserving the same ACR and managed identity
# flow used by the sidecar deployment.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PARENT_PARAMS="${SCRIPT_DIR}/../deploy.parameters.sh"
LEGACY_PARAMS="${SCRIPT_DIR}/deploy.parameters.sh"

if [ -f "${PARENT_PARAMS}" ]; then
    echo "Sourcing ${PARENT_PARAMS}..."
    # shellcheck disable=SC1091
    source "${PARENT_PARAMS}"
elif [ -f "${LEGACY_PARAMS}" ]; then
    echo "Sourcing legacy deploy.parameters.sh..."
    # shellcheck disable=SC1091
    source "${LEGACY_PARAMS}"
fi

RESOURCE_GROUP="${RESOURCE_GROUP:-${CONTAINER_APP_RESOURCE_GROUP:-}}"
RESOURCE_GROUP="${RESOURCE_GROUP:-TR-apim}"
LOCATION="${LOCATION:-eastus}"
CONTAINER_APP_NAME="${CONTAINER_APP_NAME:-simplel7dev}"
ENVIRONMENT_NAME="${ENVIRONMENT_NAME:-simplelL7Proxy}"

GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m'

# Accept the same naming used by ACA/deployment scripts, while still allowing
# WEB_IMAGE for consistency with the sidecar deployment folder.
IMAGE_NAME="${IMAGE_NAME:-${PROXY_IMAGE:-${WEB_IMAGE:-}}}"

if [ -z "${IMAGE_NAME}" ]; then
    echo -e "${RED}Error: IMAGE_NAME (or PROXY_IMAGE / WEB_IMAGE) must be set.${NC}"
    echo "Either set it as an environment variable or create deploy.parameters.sh"
    exit 1
fi

REGISTRY_SERVER="${REGISTRY_SERVER:-}"
CPU="${CPU:-${WEB_CPU:-0.5}}"
MEMORY="${MEMORY:-${WEB_MEMORY:-1.0}}"
WEB_PORT="${WEB_PORT:-8000}"
INGRESS_TYPE="${INGRESS_TYPE:-external}"
ENABLE_HTTPS="${ENABLE_HTTPS:-true}"
REVISION_MODE="${REVISION_MODE:-single}"

if ! command -v az >/dev/null 2>&1; then
    echo -e "${RED}Error: Azure CLI is not installed${NC}"
    exit 1
fi

echo -e "${YELLOW}Checking Azure login status...${NC}"
az account show >/dev/null 2>&1 || {
    echo -e "${RED}Not logged in to Azure. Running 'az login'...${NC}"
    az login
}

SUBSCRIPTION_ID=$(az account show --query id -o tsv)
echo -e "${GREEN}Using subscription: ${SUBSCRIPTION_ID}${NC}"

echo -e "${YELLOW}Ensuring resource group ${RESOURCE_GROUP} exists...${NC}"
GROUP_CREATE_ERROR_FILE="$(mktemp)"
if ! az group create --name "${RESOURCE_GROUP}" --location "${LOCATION}" --output none 2>"${GROUP_CREATE_ERROR_FILE}"; then
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

echo -e "${YELLOW}Getting Container Apps Environment...${NC}"
MANAGED_ENV_ID=$(az containerapp env show \
    --name "${ENVIRONMENT_NAME}" \
    --resource-group "${RESOURCE_GROUP}" \
    --query id -o tsv 2>/dev/null || echo "")

wait_for_managed_env() {
    local env_id=""
    local provisioning_state=""
    local attempts=0
    local max_attempts=30

    echo -e "${YELLOW}Waiting for Container Apps Environment to finish provisioning...${NC}"
    while [[ ${attempts} -lt ${max_attempts} ]]; do
        env_id=$(az containerapp env show \
            --name "${ENVIRONMENT_NAME}" \
            --resource-group "${RESOURCE_GROUP}" \
            --query id -o tsv 2>/dev/null || echo "")
        provisioning_state=$(az containerapp env show \
            --name "${ENVIRONMENT_NAME}" \
            --resource-group "${RESOURCE_GROUP}" \
            --query "properties.provisioningState" -o tsv 2>/dev/null || echo "")

        if [ -n "${env_id}" ] && [ "${provisioning_state}" = "Succeeded" ]; then
            MANAGED_ENV_ID="${env_id}"
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

if [ -z "${MANAGED_ENV_ID}" ]; then
    echo -e "${YELLOW}Container Apps Environment not found. Creating...${NC}"
    az containerapp env create \
        --name "${ENVIRONMENT_NAME}" \
        --resource-group "${RESOURCE_GROUP}" \
        --location "${LOCATION}" \
        >/dev/null
fi

wait_for_managed_env

echo -e "${GREEN}Using environment: ${MANAGED_ENV_ID}${NC}"

EXISTING_APP_NAME=$(az containerapp show \
    --name "${CONTAINER_APP_NAME}" \
    --resource-group "${RESOURCE_GROUP}" \
    --query "name" -o tsv 2>/dev/null || echo "")

if [ -z "${EXISTING_APP_NAME}" ] && [ -n "${REGISTRY_SERVER}" ]; then
    echo -e "${YELLOW}Container App doesn't exist yet. Creating placeholder app to establish managed identity...${NC}"
    az containerapp create \
        --name "${CONTAINER_APP_NAME}" \
        --resource-group "${RESOURCE_GROUP}" \
        --environment "${ENVIRONMENT_NAME}" \
        --image "mcr.microsoft.com/azuredocs/containerapps-helloworld:latest" \
        --target-port "${WEB_PORT}" \
        --ingress "${INGRESS_TYPE}" \
        --system-assigned \
        --min-replicas 0 \
        --max-replicas 1 \
        --output none
fi

EXISTING_APP_PRINCIPAL_ID=$(az containerapp show \
    --name "${CONTAINER_APP_NAME}" \
    --resource-group "${RESOURCE_GROUP}" \
    --query "identity.principalId" -o tsv 2>/dev/null || echo "")

if [ -z "${EXISTING_APP_PRINCIPAL_ID}" ] || [ "${EXISTING_APP_PRINCIPAL_ID}" = "null" ]; then
    echo -e "${YELLOW}Enabling system-assigned managed identity...${NC}"
    az containerapp identity assign \
        --name "${CONTAINER_APP_NAME}" \
        --resource-group "${RESOURCE_GROUP}" \
        --system-assigned \
        --output none

    EXISTING_APP_PRINCIPAL_ID=$(az containerapp show \
        --name "${CONTAINER_APP_NAME}" \
        --resource-group "${RESOURCE_GROUP}" \
        --query "identity.principalId" -o tsv)
fi

if [ -n "${EXISTING_APP_PRINCIPAL_ID}" ] && [ -n "${REGISTRY_SERVER}" ]; then
    echo -e "${YELLOW}Ensuring AcrPull role for Container App managed identity...${NC}"
    ACR_NAME=$(echo "${REGISTRY_SERVER}" | cut -d'.' -f1)
    ACR_RESOURCE_ID=$(az acr show --name "${ACR_NAME}" --query id -o tsv 2>/dev/null || echo "")

    if [ -n "${ACR_RESOURCE_ID}" ]; then
        ROLE_EXISTS=$(az role assignment list \
            --assignee "${EXISTING_APP_PRINCIPAL_ID}" \
            --role "AcrPull" \
            --scope "${ACR_RESOURCE_ID}" \
            --query "[0].id" -o tsv 2>/dev/null || echo "")

        if [ -n "${ROLE_EXISTS}" ]; then
            echo -e "${GREEN}AcrPull role already assigned${NC}"
        else
            az role assignment create \
                --assignee "${EXISTING_APP_PRINCIPAL_ID}" \
                --role "AcrPull" \
                --scope "${ACR_RESOURCE_ID}" \
                --output none
            echo -e "${GREEN}AcrPull role assigned${NC}"
        fi
    else
        echo -e "${RED}Error: Could not find ACR '${ACR_NAME}'.${NC}"
        exit 1
    fi
fi

echo -e "${YELLOW}Deploying Container App without sidecar using Bicep...${NC}"
DEPLOYMENT_NAME="proxy-nosidecar-deployment-$(date +%s)"

BICEP_PARAMS="containerAppName=${CONTAINER_APP_NAME}"
BICEP_PARAMS="${BICEP_PARAMS} managedEnvId=${MANAGED_ENV_ID}"
BICEP_PARAMS="${BICEP_PARAMS} location=${LOCATION}"
BICEP_PARAMS="${BICEP_PARAMS} webImage=${IMAGE_NAME}"
BICEP_PARAMS="${BICEP_PARAMS} webCpu=${CPU}"
BICEP_PARAMS="${BICEP_PARAMS} webMemory=${MEMORY}Gi"
BICEP_PARAMS="${BICEP_PARAMS} webPort=${WEB_PORT}"
BICEP_PARAMS="${BICEP_PARAMS} ingressType=${INGRESS_TYPE}"
BICEP_PARAMS="${BICEP_PARAMS} enableHttps=${ENABLE_HTTPS}"
BICEP_PARAMS="${BICEP_PARAMS} revisionMode=${REVISION_MODE}"

if [ -n "${REGISTRY_SERVER}" ]; then
    BICEP_PARAMS="${BICEP_PARAMS} registryServer=${REGISTRY_SERVER}"
fi

az deployment group create \
    --name "${DEPLOYMENT_NAME}" \
    --resource-group "${RESOURCE_GROUP}" \
    --template-file "${SCRIPT_DIR}/script-nosidecar.bicep" \
    --parameters ${BICEP_PARAMS} \
    --query "properties.outputs" -o json

echo -e "${YELLOW}Retrieving deployment outputs...${NC}"
FQDN=$(az deployment group show \
    --name "${DEPLOYMENT_NAME}" \
    --resource-group "${RESOURCE_GROUP}" \
    --query "properties.outputs.fqdn.value" -o tsv)

RESOURCE_ID=$(az deployment group show \
    --name "${DEPLOYMENT_NAME}" \
    --resource-group "${RESOURCE_GROUP}" \
    --query "properties.outputs.resourceId.value" -o tsv)

REVISION_NAME=$(az deployment group show \
    --name "${DEPLOYMENT_NAME}" \
    --resource-group "${RESOURCE_GROUP}" \
    --query "properties.outputs.latestRevisionName.value" -o tsv)

echo -e "${GREEN}======================================${NC}"
echo -e "${GREEN}Deployment Complete!${NC}"
echo -e "${GREEN}======================================${NC}"
echo -e "${GREEN}FQDN: ${FQDN}${NC}"
echo -e "${GREEN}Resource ID: ${RESOURCE_ID}${NC}"
echo -e "${GREEN}Latest Revision: ${REVISION_NAME}${NC}"
echo -e "${GREEN}======================================${NC}"

if [ "${INGRESS_TYPE}" = "external" ]; then
    PROTOCOL="https"
    if [ "${ENABLE_HTTPS}" = "false" ]; then
        PROTOCOL="http"
    fi
    echo -e "${YELLOW}Access your app at: ${PROTOCOL}://${FQDN}${NC}"
fi

echo -e "${GREEN}Done!${NC}"
