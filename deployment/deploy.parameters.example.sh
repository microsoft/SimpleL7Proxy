#!/bin/bash

# =============================================================================
# Consolidated Deployment Parameters for SimpleL7Proxy
# =============================================================================
# Copy this file to deploy.parameters.sh in this same folder, edit values,
# then run any subfolder's deploy.sh / setup.sh. They will all
# read from this single file.
#
#   cp deploy.parameters.example.sh deploy.parameters.sh
#   vi deploy.parameters.sh
#   ./VNet/deploy.sh
#   ./ContainerImage/deploy.sh
#   ./ACA/deploy.sh
#   ...
#
# Do not commit deploy.parameters.sh with real values. Add it to .gitignore.
# =============================================================================

# =============================================================================
# =============================================================================
# EDIT THE VALUES BELOW FOR YOUR ENVIRONMENT
# =============================================================================
# =============================================================================

# -----------------------------------------------------------------------------
# Common
# -----------------------------------------------------------------------------
export LOCATION="eastus"

# -----------------------------------------------------------------------------
# Resource groups (one per concern; can all be the same RG if preferred)
# -----------------------------------------------------------------------------
export NETWORK_RESOURCE_GROUP="rg-myapp-network"        # VNet, DNS, ACA env
export CONTAINER_APP_RESOURCE_GROUP="rg-myapp-prod"     # Container App
export STORAGE_RESOURCE_GROUP="rg-myapp-storage"
export APPCONFIG_RESOURCE_GROUP="rg-myapp-appconfig"

# -----------------------------------------------------------------------------
# Azure Container Registry / Images
# -----------------------------------------------------------------------------
export ACR_NAME="myregistry"
export PROXY_IMAGE_NAME="simple-l7-proxy"
export HEALTH_IMAGE_NAME="healthprobe"

# Image build settings (used by ContainerImage/deploy.sh)
# Options: "remote" (ACR build service) or "local" (Docker required)
export BUILD_METHOD="remote"
export DOCKERFILE_PATH="SimpleL7Proxy/Dockerfile"

# Optional version overrides (leave blank to auto-extract from Constants.cs)
export PROXY_VERSION_OVERRIDE=""
export HEALTHPROBE_VERSION_OVERRIDE=""

# -----------------------------------------------------------------------------
# VNet and subnets (used by VNet/deploy.sh, ACA/deploy.sh, DNS/deploy.sh)
# -----------------------------------------------------------------------------
export VNET_NAME="vnet-myapp"
export VNET_ADDRESS_PREFIX="10.40.0.0/16"

export SUBNET_ACA_NAME="snet-aca"
export SUBNET_ACA_PREFIX="10.40.0.0/23"

export SUBNET_CLIENTVM_NAME="snet-clientvm"
export SUBNET_CLIENTVM_PREFIX="10.40.2.0/24"

export SUBNET_AZUREFUNCTIONS_NAME="snet-azurefunctions"
export SUBNET_AZUREFUNCTIONS_PREFIX="10.40.3.0/24"

export SUBNET_APIM_NAME="snet-apim"
export SUBNET_APIM_PREFIX="10.40.4.0/24"

export SUBNET_PRIVATEENDPOINTS_NAME="snet-privateendpoints"
export SUBNET_PRIVATEENDPOINTS_PREFIX="10.40.5.0/24"

export DISABLE_PRIVATE_ENDPOINT_NETWORK_POLICIES="true"

# -----------------------------------------------------------------------------
# Private DNS (used by DNS/deploy.sh)
# -----------------------------------------------------------------------------
export DNS_ZONE_NAME="internal.contoso.com"
export ACA_INTERNAL_FQDN=""              # e.g. ca-myapp-proxy.internal.eastus.azurecontainerapps.io
export ACA_RECORD_NAME="ca-myapp-proxy"
export APIM_PRIVATE_IP=""                # leave blank if APIM not deployed yet
export APIM_RECORD_NAME="apim"

# -----------------------------------------------------------------------------
# ACA Environment + Container App (used by ACA/deploy.sh)
# -----------------------------------------------------------------------------
export ACA_ENVIRONMENT_NAME="cae-myapp"
export CONTAINER_APP_NAME="ca-myapp-proxy"

export CPU="0.5"
export MEMORY="1.0Gi"
export MIN_REPLICAS="1"
export MAX_REPLICAS="5"

export INGRESS_VISIBILITY="Internal"     # Internal | External
export INGRESS_PORT="8000"

# Primary backend host. Used by both ACA/deploy.sh and proxy-with-sidecar/deploy.sh.
# Format: host=<url>;mode=<apim|...>;path=<route>;probe=<healthcheck>
export HOST1="host=https://your-api.azure-api.net;mode=apim;path=/;probe=/status-0123456789abcdef"

export ENABLE_MANAGED_IDENTITY="true"
export ENABLE_APP_INSIGHTS="true"
export LOG_ANALYTICS_WORKSPACE_NAME="log-myapp"

# -----------------------------------------------------------------------------
# proxy-with-sidecar (alternate ACA deployment with sidecar health probe)
# -----------------------------------------------------------------------------
export ENVIRONMENT_NAME="myapp-env"

export WEB_CPU=0.5
export WEB_MEMORY=1.0
export HEALTH_CPU=0.25
export HEALTH_MEMORY=0.5

export WEB_PORT=8000
export HEALTH_PORT=9000
export INGRESS_TYPE="external"           # external | internal
export ENABLE_HTTPS=true
export REVISION_MODE="single"            # single | multiple

# -----------------------------------------------------------------------------
# Blob Storage (used by BlobStorage/deploy.sh)
# -----------------------------------------------------------------------------
export STORAGE_ACCOUNT_NAME="myappstorage"
export STORAGE_SKU="Standard_LRS"        # Standard_LRS | Standard_GRS | Standard_ZRS | Standard_RAGRS
export CREATE_CONTAINERS="true"
export BLOB_CONTAINERS="templates simplel7proxy"
export CA_BLOB_ROLE="Storage Blob Data Contributor"

# -----------------------------------------------------------------------------
# App Configuration (used by AppConfiguration/deploy.sh)
# -----------------------------------------------------------------------------
export APPCONFIG_NAME="myapp-appcfg"
export APPCONFIG_SKU="standard"
export APPCONFIG_LABEL="prod"
export AZURE_APPCONFIG_REFRESH_SECONDS="30"
export UPDATE_CONTAINER_APP_ENV="true"

# -----------------------------------------------------------------------------
# RequestAPI Azure Function (used by RequestAPI/create.sh and RequestAPI/deploy.sh)
# Flex Consumption, .NET 9 isolated worker.
# -----------------------------------------------------------------------------
export REQUESTAPI_RESOURCE_GROUP="rg-myapp"
export REQUESTAPI_FUNCTION_APP="myapprequestapi"         # globally unique
export REQUESTAPI_LOCATION="${LOCATION}"
export REQUESTAPI_STORAGE_ACCOUNT="myapprequestapifn"    # globally unique, 3-24 lowercase
export REQUESTAPI_APPINSIGHTS_NAME="myapprequestapi-ai"
export REQUESTAPI_RUNTIME_NAME="dotnet-isolated"
export REQUESTAPI_RUNTIME_VERSION="9.0"
export REQUESTAPI_INSTANCE_MEMORY_MB="2048"
export REQUESTAPI_MAX_INSTANCE_COUNT="100"

# External dependencies the function binds to (must already exist;
# create.sh assigns RBAC on these but does not create them).
export REQUESTAPI_SERVICEBUS_NAMESPACE="myapp-sb"
export REQUESTAPI_SERVICEBUS_QUEUE="requestqueue"
export REQUESTAPI_SERVICEBUS_FEEDER_QUEUE="feederqueue"
export REQUESTAPI_COSMOS_ACCOUNT="myapp-cosmos"
export REQUESTAPI_COSMOS_DATABASE="RequestAPI"
export REQUESTAPI_COSMOS_CONTAINER="Documents"

# =============================================================================
# =============================================================================
# DERIVED VALUES (auto-computed - no need to edit below this line)
# =============================================================================
# =============================================================================

# Registry server FQDN
export REGISTRY_SERVER="${ACR_NAME}.azurecr.io"

# Resolve repository root relative to this file
_PARAMS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" 2>/dev/null && pwd)"
_REPO_ROOT="${_PARAMS_DIR}/.."

# Extract proxy version from Constants.cs
if [ -f "${_REPO_ROOT}/src/SimpleL7Proxy/Constants.cs" ]; then
    _PROXY_VERSION_FROM_CODE=$(grep -oP 'VERSION = "\K[^"]+' "${_REPO_ROOT}/src/SimpleL7Proxy/Constants.cs" 2>/dev/null || echo "")
    if [ -n "${_PROXY_VERSION_FROM_CODE}" ] && [[ ! ${_PROXY_VERSION_FROM_CODE} == v* ]]; then
        _PROXY_VERSION_FROM_CODE="v${_PROXY_VERSION_FROM_CODE}"
    fi
fi

# Extract health probe version from Constants.cs
if [ -f "${_REPO_ROOT}/src/HealthProbe/Constants.cs" ]; then
    _HEALTHPROBE_VERSION_FROM_CODE=$(grep -oP 'VERSION = "\K[^"]+' "${_REPO_ROOT}/src/HealthProbe/Constants.cs" 2>/dev/null || echo "")
    if [ -n "${_HEALTHPROBE_VERSION_FROM_CODE}" ] && [[ ! ${_HEALTHPROBE_VERSION_FROM_CODE} == v* ]]; then
        _HEALTHPROBE_VERSION_FROM_CODE="v${_HEALTHPROBE_VERSION_FROM_CODE}"
    fi
fi

# Final versions: override > extracted > fallback
export PROXY_VERSION="${PROXY_VERSION_OVERRIDE:-${_PROXY_VERSION_FROM_CODE:-v1.0.0}}"
export HEALTHPROBE_VERSION="${HEALTHPROBE_VERSION_OVERRIDE:-${_HEALTHPROBE_VERSION_FROM_CODE:-v1.0.0}}"

# Full image references
export PROXY_IMAGE="${REGISTRY_SERVER}/${PROXY_IMAGE_NAME}:${PROXY_VERSION}"
export HEALTH_IMAGE="${REGISTRY_SERVER}/${HEALTH_IMAGE_NAME}:${HEALTHPROBE_VERSION}"

# Backwards-compat aliases (legacy variable names used by some scripts)
export WEB_IMAGE="${PROXY_IMAGE}"
export IMAGE_NAME="${PROXY_IMAGE}"
