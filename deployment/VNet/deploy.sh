#!/bin/bash

# Deploy/Update Azure VNet and required subnets for SimpleL7Proxy environments.

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

# Map consolidated variable names to those expected by this script
RESOURCE_GROUP="${RESOURCE_GROUP:-${NETWORK_RESOURCE_GROUP:-}}"

# ----------------------------------------------------------------------------
# Required parameters
# ----------------------------------------------------------------------------
RESOURCE_GROUP="${RESOURCE_GROUP:?'RESOURCE_GROUP (or NETWORK_RESOURCE_GROUP) must be set'}"
LOCATION="${LOCATION:?'LOCATION must be set'}"
VNET_NAME="${VNET_NAME:?'VNET_NAME must be set'}"
VNET_ADDRESS_PREFIX="${VNET_ADDRESS_PREFIX:?'VNET_ADDRESS_PREFIX must be set'}"

SUBNET_ACA_NAME="${SUBNET_ACA_NAME:?'SUBNET_ACA_NAME must be set'}"
SUBNET_ACA_PREFIX="${SUBNET_ACA_PREFIX:?'SUBNET_ACA_PREFIX must be set'}"
SUBNET_CLIENTVM_NAME="${SUBNET_CLIENTVM_NAME:?'SUBNET_CLIENTVM_NAME must be set'}"
SUBNET_CLIENTVM_PREFIX="${SUBNET_CLIENTVM_PREFIX:?'SUBNET_CLIENTVM_PREFIX must be set'}"
SUBNET_AZUREFUNCTIONS_NAME="${SUBNET_AZUREFUNCTIONS_NAME:?'SUBNET_AZUREFUNCTIONS_NAME must be set'}"
SUBNET_AZUREFUNCTIONS_PREFIX="${SUBNET_AZUREFUNCTIONS_PREFIX:?'SUBNET_AZUREFUNCTIONS_PREFIX must be set'}"
SUBNET_APIM_NAME="${SUBNET_APIM_NAME:?'SUBNET_APIM_NAME must be set'}"
SUBNET_APIM_PREFIX="${SUBNET_APIM_PREFIX:?'SUBNET_APIM_PREFIX must be set'}"
SUBNET_PRIVATEENDPOINTS_NAME="${SUBNET_PRIVATEENDPOINTS_NAME:?'SUBNET_PRIVATEENDPOINTS_NAME must be set'}"
SUBNET_PRIVATEENDPOINTS_PREFIX="${SUBNET_PRIVATEENDPOINTS_PREFIX:?'SUBNET_PRIVATEENDPOINTS_PREFIX must be set'}"

DISABLE_PRIVATE_ENDPOINT_NETWORK_POLICIES="${DISABLE_PRIVATE_ENDPOINT_NETWORK_POLICIES:-true}"

GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m'

ensure_subnet() {
    local subnet_name="$1"
    local subnet_prefix="$2"

    if az network vnet subnet show \
        --resource-group "${RESOURCE_GROUP}" \
        --vnet-name "${VNET_NAME}" \
        --name "${subnet_name}" >/dev/null 2>&1; then
        echo -e "${YELLOW}Updating subnet '${subnet_name}' (${subnet_prefix})...${NC}"
        az network vnet subnet update \
            --resource-group "${RESOURCE_GROUP}" \
            --vnet-name "${VNET_NAME}" \
            --name "${subnet_name}" \
            --address-prefixes "${subnet_prefix}" \
            >/dev/null
    else
        echo -e "${YELLOW}Creating subnet '${subnet_name}' (${subnet_prefix})...${NC}"
        az network vnet subnet create \
            --resource-group "${RESOURCE_GROUP}" \
            --vnet-name "${VNET_NAME}" \
            --name "${subnet_name}" \
            --address-prefixes "${subnet_prefix}" \
            >/dev/null
    fi
}

# ----------------------------------------------------------------------------
# Preconditions
# ----------------------------------------------------------------------------
if ! command -v az >/dev/null 2>&1; then
    echo -e "${RED}Error: Azure CLI is not installed.${NC}"
    exit 1
fi

echo -e "${YELLOW}Checking Azure login status...${NC}"
az account show >/dev/null 2>&1 || az login >/dev/null

SUBSCRIPTION_ID="$(az account show --query id -o tsv)"
echo -e "${GREEN}Using subscription: ${SUBSCRIPTION_ID}${NC}"

# ----------------------------------------------------------------------------
# Create or reuse resource group and VNet
# ----------------------------------------------------------------------------
echo -e "${YELLOW}Ensuring resource group '${RESOURCE_GROUP}' exists...${NC}"
az group create --name "${RESOURCE_GROUP}" --location "${LOCATION}" >/dev/null

if az network vnet show --resource-group "${RESOURCE_GROUP}" --name "${VNET_NAME}" >/dev/null 2>&1; then
    echo -e "${YELLOW}Updating VNet '${VNET_NAME}' address space to '${VNET_ADDRESS_PREFIX}'...${NC}"
    az network vnet update \
        --resource-group "${RESOURCE_GROUP}" \
        --name "${VNET_NAME}" \
        --address-prefixes "${VNET_ADDRESS_PREFIX}" \
        >/dev/null
else
    echo -e "${YELLOW}Creating VNet '${VNET_NAME}' (${VNET_ADDRESS_PREFIX})...${NC}"
    az network vnet create \
        --resource-group "${RESOURCE_GROUP}" \
        --location "${LOCATION}" \
        --name "${VNET_NAME}" \
        --address-prefixes "${VNET_ADDRESS_PREFIX}" \
        >/dev/null
fi

# ----------------------------------------------------------------------------
# Ensure required subnets exist
# ----------------------------------------------------------------------------
ensure_subnet "${SUBNET_ACA_NAME}" "${SUBNET_ACA_PREFIX}"
ensure_subnet "${SUBNET_CLIENTVM_NAME}" "${SUBNET_CLIENTVM_PREFIX}"
ensure_subnet "${SUBNET_AZUREFUNCTIONS_NAME}" "${SUBNET_AZUREFUNCTIONS_PREFIX}"
ensure_subnet "${SUBNET_APIM_NAME}" "${SUBNET_APIM_PREFIX}"
ensure_subnet "${SUBNET_PRIVATEENDPOINTS_NAME}" "${SUBNET_PRIVATEENDPOINTS_PREFIX}"

if [ "${DISABLE_PRIVATE_ENDPOINT_NETWORK_POLICIES,,}" = "true" ]; then
    echo -e "${YELLOW}Disabling private endpoint network policies on '${SUBNET_PRIVATEENDPOINTS_NAME}'...${NC}"
    az network vnet subnet update \
        --resource-group "${RESOURCE_GROUP}" \
        --vnet-name "${VNET_NAME}" \
        --name "${SUBNET_PRIVATEENDPOINTS_NAME}" \
        --disable-private-endpoint-network-policies true \
        >/dev/null
fi

echo -e "${GREEN}======================================${NC}"
echo -e "${GREEN}VNet deployment complete${NC}"
echo -e "${GREEN}======================================${NC}"
echo "Resource Group: ${RESOURCE_GROUP}"
echo "Location: ${LOCATION}"
echo "VNet: ${VNET_NAME} (${VNET_ADDRESS_PREFIX})"
echo "Subnets:"
echo "  - ${SUBNET_ACA_NAME}: ${SUBNET_ACA_PREFIX}"
echo "  - ${SUBNET_CLIENTVM_NAME}: ${SUBNET_CLIENTVM_PREFIX}"
echo "  - ${SUBNET_AZUREFUNCTIONS_NAME}: ${SUBNET_AZUREFUNCTIONS_PREFIX}"
echo "  - ${SUBNET_APIM_NAME}: ${SUBNET_APIM_PREFIX}"
echo "  - ${SUBNET_PRIVATEENDPOINTS_NAME}: ${SUBNET_PRIVATEENDPOINTS_PREFIX}"
