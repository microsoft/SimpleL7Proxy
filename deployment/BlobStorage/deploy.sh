#!/bin/bash

# Deploy/Update Azure Storage Account for SimpleL7Proxy.
#
# What this script does:
#   1. Creates the storage account (if it does not already exist) in the
#      configured resource group and region.
#   2. Optionally creates the blob containers used by the proxy
#      (`templates` and `simplel7proxy`) when CREATE_CONTAINERS=true.
#   3. Grants the Container App's system-assigned managed identity the
#      "Storage Blob Data Contributor" role on the storage account so the
#      proxy can read and write blobs at runtime using its managed identity.

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

# ----------------------------------------------------------------------------
# Required parameters
# ----------------------------------------------------------------------------
CONTAINER_APP_NAME="${CONTAINER_APP_NAME:?'CONTAINER_APP_NAME must be set'}"
CONTAINER_APP_RESOURCE_GROUP="${CONTAINER_APP_RESOURCE_GROUP:?'CONTAINER_APP_RESOURCE_GROUP must be set'}"
RESOURCE_GROUP="${RESOURCE_GROUP:?'RESOURCE_GROUP must be set (storage account resource group)'}"
LOCATION="${LOCATION:?'LOCATION must be set (storage account location)'}"
STORAGE_ACCOUNT_NAME="${STORAGE_ACCOUNT_NAME:?'STORAGE_ACCOUNT_NAME must be set'}"

# ----------------------------------------------------------------------------
# Optional overrides
# ----------------------------------------------------------------------------
# Accept STORAGE_SKU but fall back to APPCONFIG_SKU for backwards-compat
# with the existing deploy.parameters.sh template.
STORAGE_SKU="${STORAGE_SKU:-${APPCONFIG_SKU:-Standard_LRS}}"
# Normalize common short forms (e.g. "lrs" -> "Standard_LRS")
case "${STORAGE_SKU,,}" in
    lrs)  STORAGE_SKU="Standard_LRS" ;;
    grs)  STORAGE_SKU="Standard_GRS" ;;
    zrs)  STORAGE_SKU="Standard_ZRS" ;;
    ragrs) STORAGE_SKU="Standard_RAGRS" ;;
esac

CREATE_CONTAINERS="${CREATE_CONTAINERS:-false}"
# Containers to create when CREATE_CONTAINERS=true (space-separated).
BLOB_CONTAINERS="${BLOB_CONTAINERS:-templates simplel7proxy}"

# Role assigned to the Container App managed identity. The proxy reads
# and writes blobs, so Contributor is required.
CA_BLOB_ROLE="${CA_BLOB_ROLE:-Storage Blob Data Contributor}"

GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m'

# ----------------------------------------------------------------------------
# Preconditions
# ----------------------------------------------------------------------------
if ! command -v az >/dev/null 2>&1; then
    echo -e "${RED}Error: Azure CLI is not installed.${NC}"
    exit 1
fi

if ! command -v jq >/dev/null 2>&1; then
    echo -e "${RED}Error: jq is not installed.${NC}"
    exit 1
fi

echo -e "${YELLOW}Checking Azure login status...${NC}"
az account show >/dev/null 2>&1 || az login >/dev/null

SUBSCRIPTION_ID="$(az account show --query id -o tsv)"
echo -e "${GREEN}Using subscription: ${SUBSCRIPTION_ID}${NC}"

# ----------------------------------------------------------------------------
# Read the live Container App (for managed identity principal)
# ----------------------------------------------------------------------------
echo -e "${YELLOW}Reading Container App '${CONTAINER_APP_NAME}' from '${CONTAINER_APP_RESOURCE_GROUP}'...${NC}"
CA_JSON="$(az containerapp show \
    --name "${CONTAINER_APP_NAME}" \
    --resource-group "${CONTAINER_APP_RESOURCE_GROUP}" \
    -o json)" || { echo -e "${RED}Error: Could not read Container App.${NC}"; exit 1; }

CA_PRINCIPAL_ID="$(echo "${CA_JSON}" | jq -r '.identity.principalId // empty')"
if [ -z "${CA_PRINCIPAL_ID}" ]; then
    echo -e "${YELLOW}Container App has no system-assigned managed identity. Enabling it...${NC}"
    az containerapp identity assign \
        --name "${CONTAINER_APP_NAME}" \
        --resource-group "${CONTAINER_APP_RESOURCE_GROUP}" \
        --system-assigned \
        >/dev/null
    CA_PRINCIPAL_ID="$(az containerapp show \
        --name "${CONTAINER_APP_NAME}" \
        --resource-group "${CONTAINER_APP_RESOURCE_GROUP}" \
        --query identity.principalId -o tsv)"
fi
echo -e "${GREEN}Container App principalId: ${CA_PRINCIPAL_ID}${NC}"

# ----------------------------------------------------------------------------
# Create or reuse storage account
# ----------------------------------------------------------------------------
echo -e "${YELLOW}Ensuring resource group '${RESOURCE_GROUP}' exists...${NC}"
az group create --name "${RESOURCE_GROUP}" --location "${LOCATION}" >/dev/null

EXISTING_STORAGE="$(az storage account show \
    --name "${STORAGE_ACCOUNT_NAME}" \
    --resource-group "${RESOURCE_GROUP}" \
    --query name -o tsv 2>/dev/null || true)"

if [ -z "${EXISTING_STORAGE}" ]; then
    # Confirm the name is not taken globally by someone else
    NAME_AVAILABLE="$(az storage account check-name --name "${STORAGE_ACCOUNT_NAME}" --query nameAvailable -o tsv)"
    if [ "${NAME_AVAILABLE}" != "true" ]; then
        REASON="$(az storage account check-name --name "${STORAGE_ACCOUNT_NAME}" --query message -o tsv)"
        echo -e "${RED}Error: storage account name '${STORAGE_ACCOUNT_NAME}' is not available: ${REASON}${NC}"
        exit 1
    fi

    echo -e "${YELLOW}Creating storage account '${STORAGE_ACCOUNT_NAME}' (${STORAGE_SKU}) in '${LOCATION}'...${NC}"
    az storage account create \
        --name "${STORAGE_ACCOUNT_NAME}" \
        --resource-group "${RESOURCE_GROUP}" \
        --location "${LOCATION}" \
        --sku "${STORAGE_SKU}" \
        --kind StorageV2 \
        --allow-blob-public-access true \
        --public-network-access Enabled \
        --min-tls-version TLS1_2 \
        >/dev/null
    echo -e "${GREEN}✓ Storage account created${NC}"
else
    echo -e "${GREEN}Using existing storage account: ${STORAGE_ACCOUNT_NAME}${NC}"
fi

STORAGE_RESOURCE_ID="$(az storage account show \
    --name "${STORAGE_ACCOUNT_NAME}" \
    --resource-group "${RESOURCE_GROUP}" \
    --query id -o tsv)"
STORAGE_BLOB_ENDPOINT="$(az storage account show \
    --name "${STORAGE_ACCOUNT_NAME}" \
    --resource-group "${RESOURCE_GROUP}" \
    --query primaryEndpoints.blob -o tsv)"

# ----------------------------------------------------------------------------
# Grant the Container App's managed identity read access to the storage account
# ----------------------------------------------------------------------------
EXISTING_CA_ROLE="$(az role assignment list \
    --assignee "${CA_PRINCIPAL_ID}" \
    --role "${CA_BLOB_ROLE}" \
    --scope "${STORAGE_RESOURCE_ID}" \
    --query "[0].id" -o tsv 2>/dev/null || true)"

if [ -z "${EXISTING_CA_ROLE}" ]; then
    echo -e "${YELLOW}Assigning '${CA_BLOB_ROLE}' role to Container App managed identity (${CA_PRINCIPAL_ID})...${NC}"
    az role assignment create \
        --assignee-object-id "${CA_PRINCIPAL_ID}" \
        --assignee-principal-type ServicePrincipal \
        --role "${CA_BLOB_ROLE}" \
        --scope "${STORAGE_RESOURCE_ID}" \
        >/dev/null
    echo -e "${GREEN}✓ Role assigned. RBAC propagation may take a few minutes.${NC}"
else
    echo -e "${GREEN}Container App managed identity already has '${CA_BLOB_ROLE}' role.${NC}"
fi

# ----------------------------------------------------------------------------
# Optionally create blob containers
# ----------------------------------------------------------------------------
if [ "${CREATE_CONTAINERS,,}" = "true" ]; then
    echo -e "${YELLOW}Creating blob containers (CREATE_CONTAINERS=true)...${NC}"

    # Container creation uses Azure AD auth (--auth-mode login) because
    # storage accounts may have shared-key auth disabled. The signed-in
    # user therefore needs data-plane access on the account.
    SIGNED_IN_PRINCIPAL_ID="$(az ad signed-in-user show --query id -o tsv 2>/dev/null || true)"
    if [ -n "${SIGNED_IN_PRINCIPAL_ID}" ]; then
        EXISTING_USER_ROLE="$(az role assignment list \
            --assignee "${SIGNED_IN_PRINCIPAL_ID}" \
            --role "Storage Blob Data Contributor" \
            --scope "${STORAGE_RESOURCE_ID}" \
            --query "[0].id" -o tsv 2>/dev/null || true)"

        if [ -z "${EXISTING_USER_ROLE}" ]; then
            echo -e "${YELLOW}Assigning 'Storage Blob Data Contributor' to current user for container management...${NC}"
            az role assignment create \
                --assignee "${SIGNED_IN_PRINCIPAL_ID}" \
                --role "Storage Blob Data Contributor" \
                --scope "${STORAGE_RESOURCE_ID}" \
                >/dev/null
            echo -e "${YELLOW}Waiting for RBAC propagation (30s)...${NC}"
            sleep 30
        fi
    fi

    for CONTAINER in ${BLOB_CONTAINERS}; do
        EXISTS="$(az storage container exists \
            --name "${CONTAINER}" \
            --account-name "${STORAGE_ACCOUNT_NAME}" \
            --auth-mode login \
            --query exists -o tsv 2>/dev/null || echo "false")"

        if [ "${EXISTS}" = "true" ]; then
            echo -e "${GREEN}  ✓ Container '${CONTAINER}' already exists${NC}"
        else
            echo -e "${YELLOW}  Creating container '${CONTAINER}'...${NC}"
            az storage container create \
                --name "${CONTAINER}" \
                --account-name "${STORAGE_ACCOUNT_NAME}" \
                --auth-mode login \
                --public-access off \
                >/dev/null
            echo -e "${GREEN}  ✓ Container '${CONTAINER}' created${NC}"
        fi
    done
else
    echo -e "${GREEN}Skipping container creation (CREATE_CONTAINERS != true).${NC}"
fi

# ----------------------------------------------------------------------------
# Summary
# ----------------------------------------------------------------------------
echo -e "${GREEN}"
echo "=============================================================="
echo " Deployment complete"
echo "=============================================================="
echo " Storage Account : ${STORAGE_ACCOUNT_NAME}"
echo " Resource Group  : ${RESOURCE_GROUP}"
echo " Location        : ${LOCATION}"
echo " SKU             : ${STORAGE_SKU}"
echo " Blob endpoint   : ${STORAGE_BLOB_ENDPOINT}"
echo " Container App   : ${CONTAINER_APP_NAME} (${CA_BLOB_ROLE})"
if [ "${CREATE_CONTAINERS,,}" = "true" ]; then
    echo " Containers      : ${BLOB_CONTAINERS}"
fi
echo "=============================================================="
echo -e "${NC}"
