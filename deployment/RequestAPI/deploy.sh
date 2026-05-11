#!/bin/bash
#
# Deploy code updates to the RequestAPI Azure Function (Flex Consumption).
#
# Delegates the actual build/zip/upload to src/RequestAPI/deploy-flex.sh,
# but injects the RG/app name from deploy.parameters.sh via env vars so the
# same script can target multiple environments.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PARENT_PARAMS="${SCRIPT_DIR}/../deploy.parameters.sh"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
SRC_DEPLOY="${REPO_ROOT}/src/RequestAPI/deploy-flex.sh"

GREEN='\033[0;32m'; RED='\033[0;31m'; NC='\033[0m'

if [ ! -f "${PARENT_PARAMS}" ]; then
    echo -e "${RED}Error:${NC} ${PARENT_PARAMS} not found."
    echo "Copy deploy.parameters.example.sh to deploy.parameters.sh and edit values."
    exit 1
fi
# shellcheck disable=SC1091
source "${PARENT_PARAMS}"

: "${REQUESTAPI_RESOURCE_GROUP:?must be set in deploy.parameters.sh}"
: "${REQUESTAPI_FUNCTION_APP:?must be set in deploy.parameters.sh}"

if [ ! -x "${SRC_DEPLOY}" ]; then
    if [ -f "${SRC_DEPLOY}" ]; then
        chmod +x "${SRC_DEPLOY}"
    else
        echo -e "${RED}Error:${NC} ${SRC_DEPLOY} not found."
        exit 1
    fi
fi

echo -e "${GREEN}[INFO]${NC} Deploying RequestAPI to ${REQUESTAPI_FUNCTION_APP} in ${REQUESTAPI_RESOURCE_GROUP}..."

# The src script reads REQUESTAPI_RESOURCE_GROUP / REQUESTAPI_FUNCTION_APP from
# the environment and falls back to RESOURCE_GROUP / FUNCTION_APP, then to its
# hard-coded defaults. We export both forms to be explicit.
export REQUESTAPI_RESOURCE_GROUP REQUESTAPI_FUNCTION_APP

# Run from the project directory so PROJECT_PATH inside the source script
# resolves to src/RequestAPI as designed.
cd "${REPO_ROOT}/src/RequestAPI"
exec ./deploy-flex.sh
