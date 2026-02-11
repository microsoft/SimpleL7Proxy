#!/bin/bash

# Build and push SimpleL7Proxy container image
# Version is extracted from Constants.cs

set -e

# Source parameters file if it exists (for ACR variable)
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PARAMS_FILE="$SCRIPT_DIR/../../deployment/proxy-with-sidecar/deploy.parameters.sh"

if [ -f "$PARAMS_FILE" ]; then
    echo "Sourcing deploy.parameters.sh..."
    source "$PARAMS_FILE"
fi

# Validate ACR is set
if [ -z "$ACR" ]; then
    echo "Error: ACR environment variable is not set."
    echo "Either:"
    echo "  1. Create deployment/proxy-with-sidecar/deploy.parameters.sh (copy from .example.sh)"
    echo "  2. Or run: export ACR=myregistry"
    exit 1
fi

# Extract the version from Constants.cs
ver=$(grep -oP 'VERSION = "\K[^"]+' Constants.cs)

# add v if it doesnt start with it already
if [[ ! $ver == v* ]]; then
    ver="v$ver"
fi

# Output the version
echo "========================================"
echo "Building SimpleL7Proxy"
echo "========================================"
echo "ACR: $ACR"
echo "Version: $ver"
echo "Image: $ACR.azurecr.io/myproxy:$ver"
echo "========================================"

# Login to ACR (uses existing Azure CLI credentials)
echo "Logging into ACR..."
az acr login --name $ACR

# Build from parent directory (src) to include Shared project
cd ..
docker build -t $ACR.azurecr.io/myproxy:$ver -f SimpleL7Proxy/Dockerfile .
docker push $ACR.azurecr.io/myproxy:$ver

echo "========================================"
echo "Done! Update PROXY_VERSION in deploy.parameters.sh:"
echo "  export PROXY_VERSION=\"$ver\""
echo "========================================"
