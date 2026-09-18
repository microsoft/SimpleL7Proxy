#!/bin/bash

# Deployment Parameters for Azure Container Apps (ACA) Environment
#
# 1) Copy this file to deploy.parameters.sh
# 2) Update values for your environment
# 3) Run ./deploy.sh
#
# This creates an internal ACA environment that only accepts traffic from the
# VNet and does not expose a public endpoint.
#
# Do not commit deploy.parameters.sh with real values.

# =============================================================================
# Azure target (should match VNet deployment)
# =============================================================================
export RESOURCE_GROUP="rg-myapp-network"
export LOCATION="eastus"

# =============================================================================
# VNet and subnet (from VNet deployment)
# =============================================================================
export VNET_NAME="vnet-myapp"
export SUBNET_ACA_NAME="snet-aca"

# =============================================================================
# ACA Environment and Container App
# =============================================================================
export ACA_ENVIRONMENT_NAME="cae-myapp"
export CONTAINER_APP_NAME="ca-myapp-proxy"
 
# =============================================================================
# Container Image
# =============================================================================
export REGISTRY_SERVER="myregistry.azurecr.io"
export IMAGE_NAME="${REGISTRY_SERVER}/simple-l7-proxy:latest"

# =============================================================================
# Resource Allocation
# =============================================================================
export CPU="0.5"
export MEMORY="1.0Gi"
export MIN_REPLICAS="1"
export MAX_REPLICAS="5"

# =============================================================================
# Ingress Configuration (INTERNAL ONLY)
# =============================================================================
# For internal-only access, set INGRESS_VISIBILITY="Internal"
export INGRESS_VISIBILITY="Internal"
export INGRESS_PORT="8000"

# =============================================================================
# Proxy Configuration
# =============================================================================
# Backend host URLs (comma-separated or single)
export BACKEND_HOST="host=https://apim.contoso.com;mode=apim;path=/;probe=/health"

# =============================================================================
# Managed Identity
# =============================================================================
# Set to "true" to enable system-assigned managed identity
export ENABLE_MANAGED_IDENTITY="true"

# =============================================================================
# Observability
# =============================================================================
export ENABLE_APP_INSIGHTS="true"
export LOG_ANALYTICS_WORKSPACE_NAME="log-myapp"
