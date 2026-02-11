#!/bin/bash

# Deployment Parameters
# Copy this file to deploy.parameters.sh and update with your values
# Make sure to add deploy.parameters.sh to .gitignore
#
# This file is used by BOTH build scripts and deployment scripts.
# Set values here ONCE and they will be used everywhere.

# =============================================================================
# Azure Container Registry - SET THIS FIRST (used for build AND deployment)
# =============================================================================
export ACR="myregistry"

# Derived values (no need to change these)
export REGISTRY_SERVER="${ACR}.azurecr.io"

# =============================================================================
# Container Image Versions (auto-extracted from Constants.cs)
# =============================================================================
# Get the directory where this script is located
PARAMS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" 2>/dev/null && pwd)"
REPO_ROOT="${PARAMS_DIR}/../.."

# Extract versions from Constants.cs files
if [ -f "${REPO_ROOT}/src/SimpleL7Proxy/Constants.cs" ]; then
    PROXY_VERSION=$(grep -oP 'VERSION = "\K[^"]+' "${REPO_ROOT}/src/SimpleL7Proxy/Constants.cs" 2>/dev/null || echo "")
    # Add v prefix if not present
    if [ -n "$PROXY_VERSION" ] && [[ ! $PROXY_VERSION == v* ]]; then
        PROXY_VERSION="v$PROXY_VERSION"
    fi
fi

if [ -f "${REPO_ROOT}/src/HealthProbe/Constants.cs" ]; then
    HEALTHPROBE_VERSION=$(grep -oP 'VERSION = "\K[^"]+' "${REPO_ROOT}/src/HealthProbe/Constants.cs" 2>/dev/null || echo "")
    # Add v prefix if not present
    if [ -n "$HEALTHPROBE_VERSION" ] && [[ ! $HEALTHPROBE_VERSION == v* ]]; then
        HEALTHPROBE_VERSION="v$HEALTHPROBE_VERSION"
    fi
fi

# Fallback to manual versions if extraction failed
export PROXY_VERSION="${PROXY_VERSION:-v1.0.0}"
export HEALTHPROBE_VERSION="${HEALTHPROBE_VERSION:-v1.0.0}"

# Derived image names (no need to change these)
export WEB_IMAGE="${REGISTRY_SERVER}/myproxy:${PROXY_VERSION}"
export HEALTH_IMAGE="${REGISTRY_SERVER}/healthprobe:${HEALTHPROBE_VERSION}"

# =============================================================================
# Azure Configuration
# =============================================================================
export RESOURCE_GROUP="rg-myapp-prod"
export LOCATION="eastus"
export CONTAINER_APP_NAME="myapp"
export ENVIRONMENT_NAME="myapp-env"

# =============================================================================
# Backend Host Configuration
# =============================================================================
# Format: host=<url>;mode=<mode>;path=<path>;probe=<probe_path>
export HOST1="host=https://your-api.azure-api.net;mode=apim;path=/;probe=/status-0123456789abcdef"

# =============================================================================
# Resource Allocation
# =============================================================================
export WEB_CPU=0.5
export WEB_MEMORY=1.0
export HEALTH_CPU=0.25
export HEALTH_MEMORY=0.5

# =============================================================================
# Network Configuration
# =============================================================================
export WEB_PORT=8000
export HEALTH_PORT=9000
export INGRESS_TYPE="external"  # Options: external, internal
export ENABLE_HTTPS=true
export REVISION_MODE="single"  # Options: single, multiple
