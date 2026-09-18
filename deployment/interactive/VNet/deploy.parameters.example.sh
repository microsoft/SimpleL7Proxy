#!/bin/bash

# Deployment Parameters for VNet and subnet provisioning
#
# 1) Copy this file to deploy.parameters.sh
# 2) Update values for your environment
# 3) Run ./deploy.sh
#
# Do not commit deploy.parameters.sh with real values.

# =============================================================================
# Azure target
# =============================================================================
export RESOURCE_GROUP="rg-myapp-network"
export LOCATION="eastus"

# =============================================================================
# Virtual network
# =============================================================================
export VNET_NAME="vnet-myapp"
export VNET_ADDRESS_PREFIX="10.40.0.0/16"

# =============================================================================
# Required subnets
# =============================================================================
# ACA infrastructure subnet. Keep this large enough for ACA growth.
export SUBNET_ACA_NAME="snet-aca"
export SUBNET_ACA_PREFIX="10.40.0.0/23"

# Client VM subnet for jumpbox/test clients.
export SUBNET_CLIENTVM_NAME="snet-clientvm"
export SUBNET_CLIENTVM_PREFIX="10.40.2.0/24"

# Azure Functions subnet.
export SUBNET_AZUREFUNCTIONS_NAME="snet-azurefunctions"
export SUBNET_AZUREFUNCTIONS_PREFIX="10.40.3.0/24"

# APIM subnet.
export SUBNET_APIM_NAME="snet-apim"
export SUBNET_APIM_PREFIX="10.40.4.0/24"

# Private Endpoint subnet (typically with private endpoint network policies disabled).
export SUBNET_PRIVATEENDPOINTS_NAME="snet-privateendpoints"
export SUBNET_PRIVATEENDPOINTS_PREFIX="10.40.5.0/24"

# Set to "true" to disable private endpoint network policies on the
# PrivateEndpoints subnet.
export DISABLE_PRIVATE_ENDPOINT_NETWORK_POLICIES="true"
