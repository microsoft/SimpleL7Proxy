#!/bin/bash

# Build and Push SimpleL7Proxy Container Image
# Extracts version from Constants.cs and builds Docker image for deployment.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [ -f "${SCRIPT_DIR}/build.parameters.sh" ]; then
    echo "Sourcing build.parameters.sh..."
    # shellcheck disable=SC1091
    source "${SCRIPT_DIR}/build.parameters.sh"
elif [ -f "${SCRIPT_DIR}/build.parameters.example.sh" ]; then
    echo "build.parameters.sh not found."
    echo "Copy build.parameters.example.sh to build.parameters.sh and update values."
    echo "Example: cp build.parameters.example.sh build.parameters.sh"
    exit 1
fi

# Required parameters
ACR_NAME="${ACR_NAME:?'ACR_NAME must be set'}"
IMAGE_NAME="${IMAGE_NAME:?'IMAGE_NAME must be set'}"

# Optional
BUILD_METHOD="${BUILD_METHOD:-remote}"  # remote (default) or local
DOCKERFILE_PATH="${DOCKERFILE_PATH:-SimpleL7Proxy/Dockerfile}"

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
    
    echo -e "${YELLOW}Building image: ${FULL_IMAGE}${NC}"
    docker build -t "${FULL_IMAGE}" \
        -f "${REPO_ROOT}/src/${DOCKERFILE_PATH}" \
        "${REPO_ROOT}/src"
    
    echo -e "${YELLOW}Pushing image to ACR...${NC}"
    docker push "${FULL_IMAGE}"
    
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
    az acr build \
        --registry "${ACR_NAME}" \
        --image "${IMAGE_NAME}:${VERSION}" \
        --file "src/${DOCKERFILE_PATH}" \
        "${REPO_ROOT}/src"
    
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
echo ""
echo "Ready for deployment:"
echo "  cd ../ACA"
echo "  # Update deploy.parameters.sh with:"
echo "  export IMAGE_NAME=\"${ACR_SERVER}/${IMAGE_NAME}:${VERSION}\""
echo "  ./deploy.sh"
