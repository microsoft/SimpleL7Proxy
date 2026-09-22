#!/bin/bash

# Build and push MetricsServer container image
# Version is extracted from MetricsServer/Constants.cs

set -euo pipefail

# Source parameters file if it exists (for ACR variable)
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PARAMS_FILE="$SCRIPT_DIR/../../deployment/interactive/deploy.parameters.sh"

if [[ -f "$PARAMS_FILE" ]]; then
    echo "Sourcing deploy.parameters.sh..."
    source "$PARAMS_FILE"
fi

# The current deployment parameters use ACR_NAME; retain ACR compatibility.
ACR="${ACR:-${ACR_NAME:-}}"

# Validate ACR is set
if [[ -z "$ACR" ]]; then
    echo "Error: ACR environment variable is not set."
    echo "Either:"
    echo "  1. Create deployment/interactive/deploy.parameters.sh (copy from .example.sh)"
    echo "  2. Or run: export ACR=myregistry"
    exit 1
fi

# Extract the version from MetricsServer/Constants.cs
ver=$(grep -oP 'VERSION = "\K[^"]+' "$SCRIPT_DIR/Constants.cs")

# Add v if it does not start with it already
if [[ $ver != v* ]]; then
    ver="v$ver"
fi

image="$ACR.azurecr.io/metricsserver:$ver"

echo "========================================"
echo "Building MetricsServer"
echo "========================================"
echo "ACR: $ACR"
echo "Version: $ver"
echo "Image: $image"
echo "========================================"

# Login to ACR (uses existing Azure CLI credentials)
echo "Logging into ACR..."
if [[ -n "${ACRGROUP:-}" ]]; then
    az acr login --name "$ACR" --resource-group "$ACRGROUP"
else
    az acr login --name "$ACR"
fi

# Build from the src directory so the Dockerfile paths resolve
cd "$SCRIPT_DIR/.."
docker build -t "$image" -f MetricsServer/Dockerfile .
docker push "$image"

echo "========================================"
echo "Published MetricsServer image:"
echo "  $image"
echo "========================================"
