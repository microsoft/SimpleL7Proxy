#!/bin/bash

# Deployment Parameters for Private DNS Zone
#
# 1) Copy this file to deploy.parameters.sh
# 2) Update values for your environment
# 3) Run ./deploy.sh
#
# Creates a private DNS zone for internal service discovery within the VNet.
# This allows the Container App and other services to resolve internal FQDNs.
#
# Do not commit deploy.parameters.sh with real values.

# =============================================================================
# Azure target (should match VNet and ACA deployments)
# =============================================================================
export RESOURCE_GROUP="rg-myapp-network"
export LOCATION="eastus"

# =============================================================================
# VNet (from VNet deployment)
# =============================================================================
export VNET_NAME="vnet-myapp"

# =============================================================================
# Private DNS Zone
# =============================================================================
# Private DNS zone name (typically a domain you control or a subdomain)
export DNS_ZONE_NAME="internal.contoso.com"

# =============================================================================
# DNS Records
# =============================================================================
# ACA internal FQDN to register (optional; can be added manually after ACA deployment)
# The format is typically: <container-app-name>.internal.<location>.azurecontainerapps.io
export ACA_INTERNAL_FQDN="ca-myapp-proxy.internal.eastus.azurecontainerapps.io"
export ACA_RECORD_NAME="ca-myapp-proxy"

# APIM private IP (if APIM is deployed in the APIM subnet)
# This is optional; set to empty string if APIM is not deployed yet
export APIM_PRIVATE_IP=""
export APIM_RECORD_NAME="apim"

# =============================================================================
# Optional: Additional DNS records
# =============================================================================
# You can add more records by defining them here
# For example:
# export APP_RECORD_NAME="api"
# export APP_PRIVATE_IP="10.40.6.10"

