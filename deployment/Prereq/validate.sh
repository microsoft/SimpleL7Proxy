#!/bin/bash

# Validate SimpleL7Proxy Deployment Prerequisites
# This script checks that all required tools are installed and configured.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Load deployment parameters if available
if [[ -f "${SCRIPT_DIR}/../deploy.parameters.sh" ]]; then
    # shellcheck source=../deploy.parameters.sh
    source "${SCRIPT_DIR}/../deploy.parameters.sh"
fi

# Load derived values (computed from parameters)
if [[ -f "${SCRIPT_DIR}/../deploy.derived.sh" ]]; then
    # shellcheck source=../deploy.derived.sh
    source "${SCRIPT_DIR}/../deploy.derived.sh"
fi

GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
BLUE='\033[0;34m'
NC='\033[0m'

PASSED=0
FAILED=0
WARNINGS=0

echo -e "${BLUE}========================================${NC}"
echo -e "${BLUE}Validating Deployment Prerequisites${NC}"
echo -e "${BLUE}========================================${NC}"
echo ""

# =============================================================================
# Helper Functions
# =============================================================================

pass() {
    echo -e "${GREEN}✓ $1${NC}"
    PASSED=$((PASSED + 1))
}

fail() {
    echo -e "${RED}✗ $1${NC}"
    FAILED=$((FAILED + 1))
}

warn() {
    echo -e "${YELLOW}⚠ $1${NC}"
    WARNINGS=$((WARNINGS + 1))
}

info() {
    echo -e "${BLUE}ℹ $1${NC}"
}

# =============================================================================
# Check: Git
# =============================================================================
echo -e "${BLUE}[1/10] Checking Git${NC}"
if command -v git >/dev/null 2>&1; then
    GIT_VERSION=$(git --version)
    pass "Git installed: $GIT_VERSION"
else
    fail "Git is not installed"
fi
echo ""

# =============================================================================
# Check: Bash
# =============================================================================
echo -e "${BLUE}[2/10] Checking Bash${NC}"
if [ -n "${BASH_VERSION:-}" ]; then
    pass "Bash shell: $BASH_VERSION"
else
    fail "Bash shell not available"
fi
echo ""

# =============================================================================
# Check: Python3
# =============================================================================
echo -e "${BLUE}[3/10] Checking Python3${NC}"
if command -v python3 >/dev/null 2>&1; then
    PYTHON_VERSION=$(python3 --version)
    pass "Python3: $PYTHON_VERSION"
else
    fail "Python3 is not installed"
    info "  Install from: https://www.python.org/downloads/"
fi
echo ""

# =============================================================================
# Check: Azure CLI
# =============================================================================
echo -e "${BLUE}[4/10] Checking Azure CLI${NC}"
if command -v az >/dev/null 2>&1; then
    AZ_VERSION=$(az --version | head -n1)
    pass "Azure CLI installed: $AZ_VERSION"
else
    fail "Azure CLI is not installed"
    info "  Install from: https://learn.microsoft.com/cli/azure/install-azure-cli"
    echo ""
fi

# Check Azure CLI login
if command -v az >/dev/null 2>&1; then
    if az account show >/dev/null 2>&1; then
        SUBSCRIPTION=$(az account show --query "name" -o tsv)
        ACCOUNT=$(az account show --query "user.name" -o tsv)
        pass "Azure CLI authenticated as: $ACCOUNT"
        info "  Subscription: $SUBSCRIPTION"
    else
        fail "Azure CLI not authenticated"
        info "  Run: az login"
    fi
fi
echo ""

# =============================================================================
# Check: Azure Developer CLI (azd)
# =============================================================================
echo -e "${BLUE}[5/10] Checking Azure Developer CLI (azd)${NC}"
if command -v azd >/dev/null 2>&1; then
    AZD_VERSION=$(azd version)
    pass "Azure Developer CLI installed: $AZD_VERSION"
else
    warn "Azure Developer CLI (azd) is not installed (optional)"
    info "  Install from: https://learn.microsoft.com/en-us/azure/developer/azure-developer-cli/install-azd"
fi
echo ""

# =============================================================================
# Check: jq
# =============================================================================
echo -e "${BLUE}[6/10] Checking jq${NC}"
if command -v jq >/dev/null 2>&1; then
    JQ_VERSION=$(jq --version)
    pass "jq installed: $JQ_VERSION"
else
    fail "jq is not installed"
    info "  Install from: https://stedolan.github.io/jq/download/"
    info "  Or: brew install jq (macOS), apt-get install jq (Ubuntu), winget install jqlang.jq (Windows)"
fi
echo ""

# =============================================================================
# Check: Azure Subscription Access
# =============================================================================
echo -e "${BLUE}[7/10] Checking Azure Subscription Access${NC}"
if command -v az >/dev/null 2>&1; then
    if az account show >/dev/null 2>&1; then
        SUBSCRIPTION_ID=$(az account show --query "id" -o tsv)
        pass "Subscription accessible: $SUBSCRIPTION_ID"
        
        # Check resource group permissions (if user has any)
        if az group list -o none >/dev/null 2>&1; then
            pass "Can list resource groups"
        else
            warn "Limited permissions (may not be able to create resource groups)"
        fi
    fi
fi
echo ""

# =============================================================================
# Check: Docker (only required when BUILD_METHOD=local)
# =============================================================================
echo -e "${BLUE}[8/10] Checking Docker${NC}"
if [[ "${BUILD_METHOD:-remote}" == "remote" ]]; then
    info "Docker check skipped (BUILD_METHOD=remote — build runs in ACR)"
else
    if command -v docker >/dev/null 2>&1; then
        DOCKER_VERSION=$(docker --version)
        pass "Docker installed: $DOCKER_VERSION"
    else
        fail "Docker is not installed (required for BUILD_METHOD=local)"
        info "  Install from: https://www.docker.com/products/docker-desktop"
    fi
fi
echo ""

# =============================================================================
# Check: .NET SDK (optional for deployment, required for local development)
# =============================================================================
echo -e "${BLUE}[9/10] Checking .NET SDK${NC}"
if command -v dotnet >/dev/null 2>&1; then
    DOTNET_VERSION=$(dotnet --version)
    pass ".NET SDK installed: $DOTNET_VERSION"
    info "  (Optional for deployment, useful for local development)"
else
    warn ".NET SDK is not installed (optional)"
    info "  Required only for local development/building"
    info "  Install .NET 10+ from: https://dotnet.microsoft.com/download"
fi
echo ""

# =============================================================================
# Check: SSH Key (optional, useful for Git operations)
# =============================================================================
echo -e "${BLUE}[10/10] Checking SSH Configuration${NC}"
if [ -f ~/.ssh/id_rsa ] || [ -f ~/.ssh/id_ed25519 ]; then
    pass "SSH key found (optional)"
    info "  Useful for Git over SSH operations"
else
    warn "No SSH key found (optional)"
    info "  Only needed if using Git over SSH"
    info "  Generate with: ssh-keygen -t ed25519"
fi
echo ""

# =============================================================================
# Summary
# =============================================================================
echo -e "${BLUE}========================================${NC}"
echo -e "${BLUE}Validation Summary${NC}"
echo -e "${BLUE}========================================${NC}"
echo -e "${GREEN}Passed: $PASSED${NC}"
echo -e "${RED}Failed: $FAILED${NC}"
echo -e "${YELLOW}Warnings: $WARNINGS${NC}"
echo ""

if [ $FAILED -eq 0 ]; then
    echo -e "${GREEN}✓ All required prerequisites are met!${NC}"
    exit 0
else
    echo -e "${RED}✗ Some prerequisites are missing.${NC}"
    echo ""
    echo "Please install the missing tools before proceeding:"
    echo "  - See the error messages above for download links"
    echo "  - Refer to: deployment/Prereq/README.md"
    exit 1
fi
