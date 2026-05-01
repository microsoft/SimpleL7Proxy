#!/bin/bash

# Deployment Parameters for SimpleL7Proxy Blob Storage provisioning
#
# 1) Copy this file to deploy.parameters.sh
# 2) Update values for your environment
# 3) Run ./deploy.sh
#
# The script reads the Container App so it can grant its managed
# identity read access to the storage account.
#
# Do not commit deploy.parameters.sh with real values.

# =============================================================================
# Container App (consumer that needs read access to the storage account)
# =============================================================================
export CONTAINER_APP_NAME="myapp"
export CONTAINER_APP_RESOURCE_GROUP="rg-myapp-prod"

# =============================================================================
# Storage account
# =============================================================================
export RESOURCE_GROUP="rg-myapp-storage"
export LOCATION="eastus"
export STORAGE_ACCOUNT_NAME="myappstorage"

# Storage SKU. Accepted: Standard_LRS, Standard_GRS, Standard_ZRS, Standard_RAGRS
# (short forms "lrs", "grs", "zrs", "ragrs" are also accepted).
export STORAGE_SKU="Standard_LRS"

# Set to "true" to create the blob containers listed in BLOB_CONTAINERS.
export CREATE_CONTAINERS="true"

# Space-separated list of blob containers to create when CREATE_CONTAINERS=true.
export BLOB_CONTAINERS="templates simplel7proxy"

# Role granted to the Container App's managed identity on the storage account.
# Use "Storage Blob Data Contributor" if the proxy must also write blobs.
export CA_BLOB_ROLE="Storage Blob Data Contributor"
