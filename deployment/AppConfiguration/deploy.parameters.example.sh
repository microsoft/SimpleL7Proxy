#!/bin/bash

# Deployment Parameters for App Configuration warm settings sync
#
# 1) Copy this file to deploy.parameters.sh
# 2) Update values for your environment
# 3) Run ./deploy.sh
#
# The script reads the live Container App to discover env vars and
# container name.  Values not found on the Container App fall back to
# local shell variables.
#
# Do not commit deploy.parameters.sh with real values.

# =============================================================================
# Container App (source of env var values)
# =============================================================================
export CONTAINER_APP_NAME="myapp"
export CONTAINER_APP_RESOURCE_GROUP="rg-myapp-prod"

# =============================================================================
# App Configuration store
# =============================================================================
export RESOURCE_GROUP="rg-myapp-appconfig"
export LOCATION="eastus"
export APPCONFIG_NAME="myapp-appcfg"
export APPCONFIG_SKU="standard"

# Label applied to Warm:* keys. Use '\0' for null label.
export APPCONFIG_LABEL=""

# Refresh interval (seconds) written to Warm:RefreshSeconds
export AZURE_APPCONFIG_REFRESH_SECONDS="30"

# Set to "true" to push AZURE_APPCONFIG_ENDPOINT/LABEL/REFRESH_SECONDS
# env vars onto the Container App after publishing keys.
export UPDATE_CONTAINER_APP_ENV="true"
