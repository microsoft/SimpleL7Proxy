#!/bin/bash

# Get SimpleL7Proxy version from Constants.cs

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../" && pwd)"
CONSTANTS_FILE="${REPO_ROOT}/src/SimpleL7Proxy/Constants.cs"

if [ ! -f "${CONSTANTS_FILE}" ]; then
    echo "Error: Could not find ${CONSTANTS_FILE}" >&2
    exit 1
fi

VERSION=$(grep -oP 'VERSION = "\K[^"]+' "${CONSTANTS_FILE}" 2>/dev/null || echo "")

if [ -z "${VERSION}" ]; then
    echo "Error: Could not extract version from Constants.cs" >&2
    exit 1
fi

# Add v prefix if not present
if [[ ! $VERSION == v* ]]; then
    VERSION="v$VERSION"
fi

echo "${VERSION}"
