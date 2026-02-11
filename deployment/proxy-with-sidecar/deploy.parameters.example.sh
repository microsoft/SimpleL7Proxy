#!/bin/bash

# Example deployment script with actual values
# Copy this file to deploy.parameters.sh and update with your values
# Make sure to add deploy.parameters.sh to .gitignore

# Azure Configuration
export RESOURCE_GROUP="rg-healthprobe-prod"
export LOCATION="eastus"
export CONTAINER_APP_NAME="healthprobe-app"
export ENVIRONMENT_NAME="cae-healthprobe-prod"

# Container Images
# These names match the output of the build scripts in src/SimpleL7Proxy and src/HealthProbe
# Run: cd src/SimpleL7Proxy && ./build.sh && cd ../HealthProbe && ./build.sh
export WEB_IMAGE="myregistry.azurecr.io/myproxy:v1.0.0"
export HEALTH_IMAGE="myregistry.azurecr.io/healthprobe:v1.0.0"

# Private Registry Configuration (if using Azure Container Registry)
export REGISTRY_SERVER="myregistry.azurecr.io"

# Resource Allocation
export WEB_CPU=0.5
export WEB_MEMORY=1.0
export HEALTH_CPU=0.25
export HEALTH_MEMORY=0.5

# Network Configuration
export WEB_PORT=8000
export HEALTH_PORT=9000
export INGRESS_TYPE="external"  # Options: external, internal
export ENABLE_HTTPS=true
export REVISION_MODE="single"  # Options: single, multiple

# Source this file before running deploy.sh:
# source deploy.parameters.example.sh && ./deploy.sh
