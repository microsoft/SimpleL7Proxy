#!/bin/bash

# =============================================================================
# Consolidated Deployment Parameters for SimpleL7Proxy
# =============================================================================
# Copy this file to deploy.parameters.sh in this same folder, edit values,
# then launch the interactive deployment menu:
#
#   cp deploy.parameters.example.sh deploy.parameters.sh
#   vi deploy.parameters.sh
#   ./deploy.sh
#
# Do not commit deploy.parameters.sh with real values. Add it to .gitignore.
# =============================================================================

# =============================================================================
# EDIT THE VALUES BELOW FOR YOUR ENVIRONMENT
# =============================================================================

# -----------------------------------------------------------------------------
# Deployment mode flags
# These two settings control which steps are enabled in the menu.
#   PRIVATE_NETWORK_DEPLOYMENT=yes  enables step 2 (Virtual Network) and step 6 (Private DNS)
#   ASYNC_DEPLOYMENT=yes            enables step 8 (Blob Storage) and steps 9-10 (RequestAPI)
# -----------------------------------------------------------------------------
export PRIVATE_NETWORK_DEPLOYMENT="yes|no"
export ASYNC_DEPLOYMENT="yes|no"

# -----------------------------------------------------------------------------
# Common
# -----------------------------------------------------------------------------
export LOCATION="eastus"

# Resource groups (one per concern; can all be the same RG if preferred)
export NETWORK_RESOURCE_GROUP="rg-myapp-network"        # VNet, DNS, ACA env
export CONTAINER_APP_RESOURCE_GROUP="rg-myapp-prod"     # Container App
export STORAGE_RESOURCE_GROUP="rg-myapp-storage"
export APPCONFIG_RESOURCE_GROUP="rg-myapp-appconfig"

# -----------------------------------------------------------------------------
# Container Registry & Image
# -----------------------------------------------------------------------------
export ACR_NAME="acrsimplel7proxy"
export ACR_SKU="Basic"                   # Basic | Standard | Premium
export PROXY_IMAGE_NAME="simple-l7-proxy"
export HEALTH_IMAGE_NAME="healthprobe"

# "remote" = build runs in ACR, no Docker required (recommended)
# "local"  = build runs on this machine, Docker required (dev/test only)
export BUILD_METHOD="remote"
export DOCKERFILE_PATH="SimpleL7Proxy/Dockerfile"

# Optional version overrides (leave blank to auto-extract from Constants.cs)
export PROXY_VERSION_OVERRIDE=""
export HEALTHPROBE_VERSION_OVERRIDE=""

# -----------------------------------------------------------------------------
# Azure Container Apps
# -----------------------------------------------------------------------------
export ACA_ENVIRONMENT_NAME="cae-myapp"
export CONTAINER_APP_NAME="ca-myapp-proxy"

export CPU="0.5"
export MEMORY="1.0Gi"
export MIN_REPLICAS="1"
export MAX_REPLICAS="5"

export INGRESS_VISIBILITY="Internal"     # Internal | External
export INGRESS_PORT="8000"

# Primary backend host. Format: host=<url>;mode=<apim|...>;path=<route>;probe=<healthcheck>
export HOST1="host=https://your-api.azure-api.net;mode=apim;path=/;probe=/status-0123456789abcdef"

export ENABLE_MANAGED_IDENTITY="true"
export ENABLE_APP_INSIGHTS="true"
export LOG_ANALYTICS_WORKSPACE_NAME="log-myapp"

# proxy-with-sidecar variant
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
export TERMINATION_GRACE_PERIOD_SECONDS="30"

# -----------------------------------------------------------------------------
# App Configuration
# -----------------------------------------------------------------------------
export APPCONFIG_NAME="myapp-appcfg"       # Must be globally unique across Azure
export APPCONFIG_SKU="standard"
export APPCONFIG_LABEL="prod"
export AZURE_APPCONFIG_REFRESH_SECONDS="30"
export UPDATE_CONTAINER_APP_ENV="true"

# =============================================================================
#
#     SKIP THIS ENTIRE SECTION IF YOU ARE NOT DEPLOYING IN A PRIVATE NETWORK.
#
# =============================================================================

# Virtual Network
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

# Private DNS
export DNS_ZONE_NAME="internal.contoso.com"
export ACA_INTERNAL_FQDN=""              # e.g. ca-myapp-proxy.internal.eastus.azurecontainerapps.io
export ACA_RECORD_NAME="ca-myapp-proxy"
export APIM_PRIVATE_IP=""                # leave blank if APIM not deployed yet
export APIM_RECORD_NAME="apim"

# =============================================================================
#
#     SKIP THIS ENTIRE SECTION IF YOU ARE NOT USING ASYNC MODE.
#
# =============================================================================

# Blob Storage
export STORAGE_ACCOUNT_NAME="myappstorage" # Must be globally unique across Azure; lowercase letters and numbers only
export STORAGE_SKU="Standard_LRS"        # Standard_LRS | Standard_GRS | Standard_ZRS | Standard_RAGRS
export CREATE_CONTAINERS="true"
export BLOB_CONTAINERS="templates simplel7proxy"
export CA_BLOB_ROLE="Storage Blob Data Contributor"

# RequestAPI Azure Function (Flex Consumption, .NET 9 isolated worker)
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
