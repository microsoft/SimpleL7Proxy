#!/bin/bash

# Build Parameters for Container Image Build
#
# 1) Copy this file to build.parameters.sh
# 2) Update values for your environment
# 3) Run ./build.sh
#
# To see the actual image version from Constants.cs, run:
#   ./get-version.sh
#
# Do not commit build.parameters.sh with real values.

# =============================================================================
# Azure Container Registry (ACR)
# =============================================================================
export ACR_NAME="myregistry"

# =============================================================================
# Container Image
# =============================================================================
# Image name (without registry server or tag)
# Tag will be automatically appended from src/SimpleL7Proxy/Constants.cs as :vX.Y.Z
export IMAGE_NAME="simple-l7-proxy"

# =============================================================================
# Build Method
# =============================================================================
# Options: "remote" or "local"
# - remote: Uses Azure Container Registry build service (no Docker required) - RECOMMENDED
# - local:  Requires Docker installed and running (faster feedback on your machine)
export BUILD_METHOD="remote"

# =============================================================================
# Dockerfile Path (relative to src/ directory)
# =============================================================================
export DOCKERFILE_PATH="SimpleL7Proxy/Dockerfile"
