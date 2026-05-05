#!/bin/bash

# Deploy/Update Private DNS Zone for internal service discovery
# This creates a private DNS zone linked to the VNet, enabling internal services
# (ACA, APIM, etc.) to resolve via custom DNS names.

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
DNS_ZONE_NAME="${DNS_ZONE_NAME:?'DNS_ZONE_NAME must be set'}"

# Optional parameters
ACA_INTERNAL_FQDN="${ACA_INTERNAL_FQDN:-}"
ACA_RECORD_NAME="${ACA_RECORD_NAME:-}"
APIM_PRIVATE_IP="${APIM_PRIVATE_IP:-}"
APIM_RECORD_NAME="${APIM_RECORD_NAME:-}"

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

# Get VNet ID
echo -e "${YELLOW}Getting VNet ID...${NC}"
VNET_ID=$(az network vnet show \
    --resource-group "${RESOURCE_GROUP}" \
    --name "${VNET_NAME}" \
    --query id -o tsv)

if [ -z "${VNET_ID}" ]; then
    echo -e "${RED}Error: Could not find VNet ${VNET_NAME}.${NC}"
    exit 1
fi

echo -e "${GREEN}VNet ID: ${VNET_ID}${NC}"

# Create or reuse private DNS zone
echo -e "${YELLOW}Ensuring private DNS zone '${DNS_ZONE_NAME}' exists...${NC}"

if az network private-dns zone show \
    --resource-group "${RESOURCE_GROUP}" \
    --name "${DNS_ZONE_NAME}" >/dev/null 2>&1; then
    
    echo -e "${GREEN}Using existing private DNS zone: ${DNS_ZONE_NAME}${NC}"
else
    echo -e "${YELLOW}Creating private DNS zone...${NC}"
    az network private-dns zone create \
        --resource-group "${RESOURCE_GROUP}" \
        --name "${DNS_ZONE_NAME}" \
        >/dev/null
fi

# Link VNet to DNS zone
echo -e "${YELLOW}Linking VNet to DNS zone...${NC}"

LINK_NAME="${VNET_NAME}-link"
if az network private-dns link vnet show \
    --resource-group "${RESOURCE_GROUP}" \
    --zone-name "${DNS_ZONE_NAME}" \
    --name "${LINK_NAME}" >/dev/null 2>&1; then
    
    echo -e "${GREEN}VNet already linked to DNS zone.${NC}"
else
    echo -e "${YELLOW}Creating VNet link...${NC}"
    az network private-dns link vnet create \
        --resource-group "${RESOURCE_GROUP}" \
        --zone-name "${DNS_ZONE_NAME}" \
        --name "${LINK_NAME}" \
        --virtual-network "${VNET_ID}" \
        --registration-enabled false \
        >/dev/null
fi

# Add ACA DNS record (CNAME to internal FQDN)
if [ -n "${ACA_RECORD_NAME}" ] && [ -n "${ACA_INTERNAL_FQDN}" ]; then
    echo -e "${YELLOW}Adding ACA DNS record: ${ACA_RECORD_NAME} -> ${ACA_INTERNAL_FQDN}${NC}"
    
    if az network private-dns record-set cname show \
        --resource-group "${RESOURCE_GROUP}" \
        --zone-name "${DNS_ZONE_NAME}" \
        --name "${ACA_RECORD_NAME}" >/dev/null 2>&1; then
        
        echo -e "${YELLOW}Updating existing CNAME record...${NC}"
        az network private-dns record-set cname delete \
            --resource-group "${RESOURCE_GROUP}" \
            --zone-name "${DNS_ZONE_NAME}" \
            --name "${ACA_RECORD_NAME}" \
            --yes >/dev/null
    fi
    
    az network private-dns record-set cname create \
        --resource-group "${RESOURCE_GROUP}" \
        --zone-name "${DNS_ZONE_NAME}" \
        --name "${ACA_RECORD_NAME}" \
        >/dev/null
    
    az network private-dns record-set cname set-record \
        --resource-group "${RESOURCE_GROUP}" \
        --zone-name "${DNS_ZONE_NAME}" \
        --record-set-name "${ACA_RECORD_NAME}" \
        --cname "${ACA_INTERNAL_FQDN}" \
        >/dev/null
fi

# Add APIM DNS record (A record pointing to private IP)
if [ -n "${APIM_RECORD_NAME}" ] && [ -n "${APIM_PRIVATE_IP}" ]; then
    echo -e "${YELLOW}Adding APIM DNS record: ${APIM_RECORD_NAME} -> ${APIM_PRIVATE_IP}${NC}"
    
    if az network private-dns record-set a show \
        --resource-group "${RESOURCE_GROUP}" \
        --zone-name "${DNS_ZONE_NAME}" \
        --name "${APIM_RECORD_NAME}" >/dev/null 2>&1; then
        
        echo -e "${YELLOW}Updating existing A record...${NC}"
        az network private-dns record-set a delete \
            --resource-group "${RESOURCE_GROUP}" \
            --zone-name "${DNS_ZONE_NAME}" \
            --name "${APIM_RECORD_NAME}" \
            --yes >/dev/null
    fi
    
    az network private-dns record-set a create \
        --resource-group "${RESOURCE_GROUP}" \
        --zone-name "${DNS_ZONE_NAME}" \
        --name "${APIM_RECORD_NAME}" \
        >/dev/null
    
    az network private-dns record-set a add-record \
        --resource-group "${RESOURCE_GROUP}" \
        --zone-name "${DNS_ZONE_NAME}" \
        --record-set-name "${APIM_RECORD_NAME}" \
        --ipv4-address "${APIM_PRIVATE_IP}" \
        >/dev/null
fi

echo -e "${GREEN}======================================${NC}"
echo -e "${GREEN}DNS deployment complete${NC}"
echo -e "${GREEN}======================================${NC}"
echo "Resource Group: ${RESOURCE_GROUP}"
echo "VNet: ${VNET_NAME}"
echo "DNS Zone: ${DNS_ZONE_NAME}"
echo ""
echo "VNet Link: ${LINK_NAME}"
if [ -n "${ACA_RECORD_NAME}" ]; then
    echo "ACA Record: ${ACA_RECORD_NAME} (CNAME)"
fi
if [ -n "${APIM_RECORD_NAME}" ]; then
    echo "APIM Record: ${APIM_RECORD_NAME} (A)"
fi

