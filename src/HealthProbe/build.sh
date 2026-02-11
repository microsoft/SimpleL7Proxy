#!/bin/bash

# Build script for HealthProbe Docker image
# Mirrors the SimpleL7Proxy build pattern: extract version from Constants.cs, build, test, and push to ACR

set -e

# Colors for output
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m'

# Source parameters file if it exists (for ACR variable)
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PARAMS_FILE="$SCRIPT_DIR/../../deployment/proxy-with-sidecar/deploy.parameters.sh"

if [ -f "$PARAMS_FILE" ]; then
    echo -e "${YELLOW}Sourcing deploy.parameters.sh...${NC}"
    source "$PARAMS_FILE"
fi

# Validate ACR is set
if [ -z "$ACR" ]; then
    echo -e "${RED}Error: ACR environment variable is not set.${NC}"
    echo "Either:"
    echo "  1. Create deployment/proxy-with-sidecar/deploy.parameters.sh (copy from .example.sh)"
    echo "  2. Or run: export ACR=myregistry"
    exit 1
fi

echo -e "${GREEN}======================================${NC}"
echo -e "${GREEN}Building HealthProbe Docker Image${NC}"
echo -e "${GREEN}======================================${NC}"

# Extract the version from Constants.cs
ver=$(grep -oP 'VERSION = "\K[^"]+' Constants.cs)

# Add 'v' prefix if it doesn't start with it already
if [[ ! $ver == v* ]]; then
    ver="v$ver"
fi

echo -e "${GREEN}Extracted Version: ${ver}${NC}"

# Build the image name with ACR registry
FULL_IMAGE_NAME="$ACR.azurecr.io/healthprobe:$ver"
echo -e "${YELLOW}Building image: ${FULL_IMAGE_NAME}${NC}"

# Login to ACR (uses existing Azure CLI credentials)
echo -e "${YELLOW}Logging into ACR...${NC}"
az acr login --name $ACR

# Build from parent directory (src) to include Shared project
cd ..
echo -e "${YELLOW}Building Docker image from src directory...${NC}"
docker build -t "$FULL_IMAGE_NAME" -f HealthProbe/Dockerfile .

echo -e "${GREEN}======================================${NC}"
echo -e "${GREEN}Build Complete!${NC}"
echo -e "${GREEN}======================================${NC}"

# Push to ACR
docker push "$FULL_IMAGE_NAME"

echo -e "${GREEN}======================================${NC}"
echo -e "${GREEN}Success!${NC}"
echo -e "${GREEN}Image: ${FULL_IMAGE_NAME}${NC}"
echo -e "${GREEN}======================================${NC}"
echo -e "${YELLOW}Update HEALTHPROBE_VERSION in deploy.parameters.sh:${NC}"
echo -e "${GREEN}  export HEALTHPROBE_VERSION=\"$ver\"${NC}"
echo -e "${GREEN}Done!${NC}"
