#!/bin/bash

# Build script for HealthProbe Docker image
# Mirrors the SimpleL7Proxy build pattern: extract version from Constants.cs, build, test, and push to ACR

set -e

# Colors for output
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m'

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
echo -e "${GREEN}Done!${NC}"
