#!/bin/bash

# Build and Push SimpleL7Proxy Container Image
# Extracts version from Constants.cs and builds Docker image for deployment.

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

# Map consolidated variable names to those expected by this script.
# This script wants the bare repo name (e.g. "simple-l7-proxy"), not the full
# registry/repo:tag reference. Prefer PROXY_IMAGE_NAME when set.
if [ -n "${PROXY_IMAGE_NAME:-}" ]; then
    IMAGE_NAME="${PROXY_IMAGE_NAME}"
fi

# Required parameters
ACR_NAME="${ACR_NAME:?'ACR_NAME must be set'}"
IMAGE_NAME="${IMAGE_NAME:?'IMAGE_NAME (or PROXY_IMAGE_NAME) must be set'}"
CONTAINER_APP_RESOURCE_GROUP="${CONTAINER_APP_RESOURCE_GROUP:?'CONTAINER_APP_RESOURCE_GROUP must be set'}"

# Optional
BUILD_METHOD="${BUILD_METHOD:-remote}"  # remote (default) or local
DOCKERFILE_PATH="${DOCKERFILE_PATH:-SimpleL7Proxy/Dockerfile}"
HEALTH_IMAGE_NAME="${HEALTH_IMAGE_NAME:-healthprobe}"
HEALTH_DOCKERFILE_PATH="${HEALTH_DOCKERFILE_PATH:-HealthProbe/Dockerfile}"
ACR_SKU="${ACR_SKU:-Basic}"

GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
BLUE='\033[0;34m'
NC='\033[0m'

# Repository root (2 levels up from deployment/)
REPO_ROOT="${SCRIPT_DIR}/../../"

echo -e "${BLUE}========================================${NC}"
echo -e "${BLUE}Building SimpleL7Proxy Container Image${NC}"
echo -e "${BLUE}========================================${NC}"
echo ""

# Ensure resource group exists
echo -e "${YELLOW}Ensuring resource group ${CONTAINER_APP_RESOURCE_GROUP} exists...${NC}"
GROUP_CREATE_ERROR_FILE="$(mktemp)"
if ! az group create --name "${CONTAINER_APP_RESOURCE_GROUP}" --location "${LOCATION:-eastus}" --output none 2>"${GROUP_CREATE_ERROR_FILE}"; then
    if grep -q "ResourceGroupBeingDeleted" "${GROUP_CREATE_ERROR_FILE}"; then
        echo -e "${RED}Error: Resource group '${CONTAINER_APP_RESOURCE_GROUP}' is currently being deleted.${NC}"
        echo -e "${YELLOW}Wait for deletion to finish, or update CONTAINER_APP_RESOURCE_GROUP in deploy.parameters.sh to a different name and rerun Step 3.${NC}"
    else
        cat "${GROUP_CREATE_ERROR_FILE}" >&2
    fi
    rm -f "${GROUP_CREATE_ERROR_FILE}"
    exit 1
fi
rm -f "${GROUP_CREATE_ERROR_FILE}"
echo -e "${GREEN}✓ Resource group ready${NC}"

# Ensure ACR exists (idempotent)
echo -e "${YELLOW}Ensuring ACR ${ACR_NAME} exists...${NC}"
if az acr show --name "${ACR_NAME}" --resource-group "${CONTAINER_APP_RESOURCE_GROUP}" --output none 2>/dev/null; then
    echo -e "${GREEN}✓ ACR already exists${NC}"
else
    echo -e "${YELLOW}Creating ACR ${ACR_NAME} (SKU: ${ACR_SKU})...${NC}"
    az acr create \
        --name "${ACR_NAME}" \
        --resource-group "${CONTAINER_APP_RESOURCE_GROUP}" \
        --sku "${ACR_SKU}" \
        --output none
    echo -e "${GREEN}✓ ACR created${NC}"
fi
echo ""

# Extract version from Constants.cs
echo -e "${YELLOW}Extracting version from Constants.cs...${NC}"

CONSTANTS_FILE="${REPO_ROOT}/src/SimpleL7Proxy/Constants.cs"
if [ ! -f "${CONSTANTS_FILE}" ]; then
    echo -e "${RED}Error: Could not find ${CONSTANTS_FILE}${NC}"
    exit 1
fi

VERSION=$(grep -oP 'VERSION = "\K[^"]+' "${CONSTANTS_FILE}" 2>/dev/null || echo "")

if [ -z "${VERSION}" ]; then
    echo -e "${RED}Error: Could not extract version from Constants.cs${NC}"
    exit 1
fi

# Add v prefix if not present
if [[ ! $VERSION == v* ]]; then
    VERSION="v$VERSION"
fi

echo -e "${GREEN}✓ Version: ${VERSION}${NC}"
echo ""

echo -e "${YELLOW}Extracting HealthProbe version from Constants.cs...${NC}"

HEALTH_CONSTANTS_FILE="${REPO_ROOT}/src/HealthProbe/Constants.cs"
if [ ! -f "${HEALTH_CONSTANTS_FILE}" ]; then
    echo -e "${RED}Error: Could not find ${HEALTH_CONSTANTS_FILE}${NC}"
    exit 1
fi

HEALTH_VERSION="${HEALTHPROBE_VERSION:-}"
if [ -z "${HEALTH_VERSION}" ]; then
    HEALTH_VERSION=$(grep -oP 'VERSION = "\K[^"]+' "${HEALTH_CONSTANTS_FILE}" 2>/dev/null || echo "")
fi

if [ -z "${HEALTH_VERSION}" ]; then
    echo -e "${RED}Error: Could not extract version from HealthProbe Constants.cs${NC}"
    exit 1
fi

if [[ ! $HEALTH_VERSION == v* ]]; then
    HEALTH_VERSION="v$HEALTH_VERSION"
fi

echo -e "${GREEN}✓ HealthProbe Version: ${HEALTH_VERSION}${NC}"
echo ""

# Build method: local or remote
if [ "${BUILD_METHOD}" = "local" ]; then
    echo -e "${YELLOW}Building locally with Docker...${NC}"
    
    if ! command -v docker >/dev/null 2>&1; then
        echo -e "${RED}Error: Docker is not installed.${NC}"
        echo "Install Docker from: https://www.docker.com/products/docker-desktop"
        exit 1
    fi
    
    # Check Docker is running
    if ! docker ps >/dev/null 2>&1; then
        echo -e "${RED}Error: Docker daemon is not running.${NC}"
        exit 1
    fi
    
    # Login to ACR if needed
    echo -e "${YELLOW}Authenticating to Azure Container Registry...${NC}"
    if ! az acr check-health --name "${ACR_NAME}" >/dev/null 2>&1; then
        echo -e "${YELLOW}Logging into ACR: ${ACR_NAME}${NC}"
        az acr login --name "${ACR_NAME}"
    fi
    
    ACR_SERVER="${ACR_NAME}.azurecr.io"
    FULL_IMAGE="${ACR_SERVER}/${IMAGE_NAME}:${VERSION}"
    FULL_HEALTH_IMAGE="${ACR_SERVER}/${HEALTH_IMAGE_NAME}:${HEALTH_VERSION}"
    
    echo -e "${YELLOW}Building image: ${FULL_IMAGE}${NC}"
    docker build -t "${FULL_IMAGE}" \
        -f "${REPO_ROOT}/src/${DOCKERFILE_PATH}" \
        "${REPO_ROOT}/src"

    echo -e "${YELLOW}Building health image: ${FULL_HEALTH_IMAGE}${NC}"
    docker build -t "${FULL_HEALTH_IMAGE}" \
        -f "${REPO_ROOT}/src/${HEALTH_DOCKERFILE_PATH}" \
        "${REPO_ROOT}/src"
    
    echo -e "${YELLOW}Pushing image to ACR...${NC}"
    docker push "${FULL_IMAGE}"

    echo -e "${YELLOW}Pushing health image to ACR...${NC}"
    docker push "${FULL_HEALTH_IMAGE}"
    
    echo -e "${GREEN}✓ Local build and push complete${NC}"
    
elif [ "${BUILD_METHOD}" = "remote" ]; then
    echo -e "${YELLOW}Building remotely in Azure Container Registry...${NC}"
    
    if ! command -v az >/dev/null 2>&1; then
        echo -e "${RED}Error: Azure CLI is not installed.${NC}"
        exit 1
    fi
    
    # Check Azure CLI login
    if ! az account show >/dev/null 2>&1; then
        echo -e "${YELLOW}Authenticating to Azure...${NC}"
        az login
    fi
    
    ACR_SERVER="${ACR_NAME}.azurecr.io"
    
    echo -e "${YELLOW}Starting remote build in ACR...${NC}"
    (
        cd "${REPO_ROOT}/src"
        az acr build \
            --registry "${ACR_NAME}" \
            --image "${IMAGE_NAME}:${VERSION}" \
            --file "${DOCKERFILE_PATH}" \
            .

        az acr build \
            --registry "${ACR_NAME}" \
            --image "${HEALTH_IMAGE_NAME}:${HEALTH_VERSION}" \
            --file "${HEALTH_DOCKERFILE_PATH}" \
            .
    )
    
    echo -e "${GREEN}✓ Remote build complete${NC}"
else
    echo -e "${RED}Error: BUILD_METHOD must be 'local' or 'remote'${NC}"
    exit 1
fi

echo ""
echo -e "${GREEN}======================================${NC}"
echo -e "${GREEN}Build Complete${NC}"
echo -e "${GREEN}======================================${NC}"
echo "ACR: ${ACR_NAME}"
echo "Image: ${IMAGE_NAME}:${VERSION}"
echo "Health Image: ${HEALTH_IMAGE_NAME}:${HEALTH_VERSION}"
echo ""
echo "Ready for deployment:"
echo "  cd ../ACA"
echo "  # Update deploy.parameters.sh with:"
echo "  export IMAGE_NAME=\"${ACR_SERVER}/${IMAGE_NAME}:${VERSION}\""
echo "  ./deploy.sh"
