#!/bin/bash

# Validate Azure Container Registry before building images.
# If the registry is missing, optionally create it after operator confirmation.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PARENT_PARAMS="${SCRIPT_DIR}/../deploy.parameters.sh"
PARENT_EXAMPLE="${SCRIPT_DIR}/../deploy.parameters.example.sh"

if [ -f "${PARENT_PARAMS}" ]; then
    echo "Sourcing ${PARENT_PARAMS}..."
    # shellcheck disable=SC1091
    source "${PARENT_PARAMS}"
elif [ -f "${SCRIPT_DIR}/deploy.parameters.sh" ]; then
    echo "Sourcing legacy deploy.parameters.sh..."
    # shellcheck disable=SC1091
    source "${SCRIPT_DIR}/deploy.parameters.sh"
elif [ -f "${PARENT_EXAMPLE}" ]; then
    echo "deploy.parameters.sh not found."
    echo "Copy ${PARENT_EXAMPLE} to ${PARENT_PARAMS} and update values."
    echo "Example: cp ${PARENT_EXAMPLE} ${PARENT_PARAMS}"
    exit 1
fi

ACR_NAME="${ACR_NAME:?'ACR_NAME must be set'}"
ACR_SKU="${ACR_SKU:-Basic}"
CONTAINER_APP_RESOURCE_GROUP="${CONTAINER_APP_RESOURCE_GROUP:?'CONTAINER_APP_RESOURCE_GROUP must be set'}"
LOCATION="${LOCATION:-eastus}"
CREATE_ACR_IF_MISSING="${CREATE_ACR_IF_MISSING:-}"

GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
BLUE='\033[0;34m'
NC='\033[0m'

echo -e "${BLUE}========================================${NC}"
echo -e "${BLUE}Validate Azure Container Registry${NC}"
echo -e "${BLUE}========================================${NC}"
echo ""

if ! command -v az >/dev/null 2>&1; then
    echo -e "${RED}Error: Azure CLI is not installed.${NC}"
    exit 1
fi

echo -e "${YELLOW}Checking Azure login status...${NC}"
if ! az account show >/dev/null 2>&1; then
    echo -e "${YELLOW}Authenticating to Azure...${NC}"
    az login >/dev/null
fi

SUBSCRIPTION_ID="$(az account show --query id -o tsv)"
echo -e "${GREEN}Using subscription: ${SUBSCRIPTION_ID}${NC}"
echo ""

echo -e "${YELLOW}Checking for ACR ${ACR_NAME}...${NC}"
ACR_RESOURCE_GROUP="$(az acr show --name "${ACR_NAME}" --query resourceGroup -o tsv 2>/dev/null || echo "")"

if [ -n "${ACR_RESOURCE_GROUP}" ]; then
    echo -e "${GREEN}✓ ACR exists in resource group: ${ACR_RESOURCE_GROUP}${NC}"
    exit 0
fi

echo -e "${YELLOW}ACR '${ACR_NAME}' was not found in the current subscription.${NC}"
echo "New registries will be created in resource group '${CONTAINER_APP_RESOURCE_GROUP}' with SKU '${ACR_SKU}'."
echo ""

SHOULD_CREATE="false"
case "${CREATE_ACR_IF_MISSING}" in
    true|TRUE|yes|YES|y|Y|1)
        SHOULD_CREATE="true"
        ;;
    false|FALSE|no|NO|n|N|0)
        SHOULD_CREATE="false"
        ;;
    *)
        if [ -t 0 ]; then
            read -r -p "Create ACR '${ACR_NAME}' in '${CONTAINER_APP_RESOURCE_GROUP}' with SKU '${ACR_SKU}'? [y/N]: " REPLY
            case "${REPLY}" in
                y|Y|yes|YES) SHOULD_CREATE="true" ;;
            esac
        fi
        ;;
esac

if [ "${SHOULD_CREATE}" != "true" ]; then
    echo -e "${YELLOW}ACR was not created.${NC}"
    echo "Create it manually or rerun this step and choose 'y'."
    echo "For non-interactive runs, set CREATE_ACR_IF_MISSING=true."
    exit 1
fi

echo -e "${YELLOW}Checking global name availability for ${ACR_NAME}...${NC}"
NAME_AVAILABLE="$(az acr check-name --name "${ACR_NAME}" --query nameAvailable -o tsv 2>/dev/null || echo "false")"
if [ "${NAME_AVAILABLE}" != "true" ]; then
    echo -e "${RED}Error: ACR name '${ACR_NAME}' is not available.${NC}"
    echo -e "${YELLOW}ACR names are globally unique across Azure. Update ACR_NAME in deploy.parameters.sh and rerun this step.${NC}"
    exit 1
fi

echo -e "${YELLOW}Ensuring resource group ${CONTAINER_APP_RESOURCE_GROUP} exists...${NC}"
GROUP_CREATE_ERROR_FILE="$(mktemp)"
if ! az group create --name "${CONTAINER_APP_RESOURCE_GROUP}" --location "${LOCATION}" --output none 2>"${GROUP_CREATE_ERROR_FILE}"; then
    if grep -q "ResourceGroupBeingDeleted" "${GROUP_CREATE_ERROR_FILE}"; then
        echo -e "${RED}Error: Resource group '${CONTAINER_APP_RESOURCE_GROUP}' is currently being deleted.${NC}"
        echo -e "${YELLOW}Wait for deletion to finish, or update CONTAINER_APP_RESOURCE_GROUP in deploy.parameters.sh to a different name and rerun this step.${NC}"
    else
        cat "${GROUP_CREATE_ERROR_FILE}" >&2
    fi
    rm -f "${GROUP_CREATE_ERROR_FILE}"
    exit 1
fi
rm -f "${GROUP_CREATE_ERROR_FILE}"
echo -e "${GREEN}✓ Resource group ready${NC}"

echo -e "${YELLOW}Creating ACR ${ACR_NAME} (SKU: ${ACR_SKU})...${NC}"
az acr create \
    --name "${ACR_NAME}" \
    --resource-group "${CONTAINER_APP_RESOURCE_GROUP}" \
    --sku "${ACR_SKU}" \
    --output none

echo -e "${GREEN}✓ ACR created${NC}"