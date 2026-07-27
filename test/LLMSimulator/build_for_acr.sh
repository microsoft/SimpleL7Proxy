#!/bin/bash

# Build and push the LLMSimulator container image

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PARAMS_FILE="$SCRIPT_DIR/../../deployment/proxy-with-sidecar/deploy.parameters.sh"

if [ -f "$PARAMS_FILE" ]; then
    echo "Sourcing deploy.parameters.sh..."
    source "$PARAMS_FILE"
fi

if [ -z "${ACR:-}" ]; then
    echo "Error: ACR environment variable is not set."
    echo "Either:"
    echo "  1. Create deployment/proxy-with-sidecar/deploy.parameters.sh (copy from .example.sh)"
    echo "  2. Or run: export ACR=myregistry"
    exit 1
fi

IMAGE_NAME="${LLM_SIMULATOR_IMAGE_NAME:-llmsimulator}"
VERSION="${LLM_SIMULATOR_VERSION:-latest}"
IMAGE="$ACR.azurecr.io/$IMAGE_NAME:$VERSION"

echo "========================================"
echo "Building LLMSimulator"
echo "========================================"
echo "ACR: $ACR"
echo "Version: $VERSION"
echo "Image: $IMAGE"
echo "========================================"

echo "Logging into ACR..."
az acr login --name "$ACR"

docker build -t "$IMAGE" -f "$SCRIPT_DIR/Dockerfile" "$SCRIPT_DIR"
docker push "$IMAGE"

echo "========================================"
echo "Done! Image pushed: $IMAGE"
echo "========================================"