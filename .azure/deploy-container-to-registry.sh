#!/bin/bash
# Build and push SimpleL7Proxy image to the specified ACR
# Usage: deploy-container-to-registry.sh <acr-name> [--acr-build]

set -euo pipefail

if [ "$#" -lt 1 ]; then
  echo "Usage: $0 <acr-name> [--acr-build]"
  exit 2
fi

acr_name="$1"
use_acr_build=false
if [ "${2-}" = "--acr-build" ]; then
  use_acr_build=true
fi

echo "Deploying container to ACR: $acr_name (use_acr_build=$use_acr_build)"

# Get login server
login_server=$(az acr show -n "$acr_name" --query "loginServer" -o tsv)
if [ -z "$login_server" ]; then
  echo "Failed to determine ACR login server for $acr_name"
  exit 1
fi

if [ "$use_acr_build" = true ]; then
  echo "Using 'az acr build' to build and push in Azure"
  az acr build --registry "$acr_name" --image simple-l7-proxy:latest --file Dockerfile .
else
  echo "Building locally and pushing to $login_server"
  docker build -t simple-l7-proxy:latest .
  docker tag simple-l7-proxy:latest "$login_server/simple-l7-proxy:latest"
  az acr login -n "$acr_name"
  docker push "$login_server/simple-l7-proxy:latest"
fi

echo "✅ Image pushed to $login_server/simple-l7-proxy:latest"
echo "You can verify with: az acr repository show-tags -n $acr_name --repository simple-l7-proxy --output table"
