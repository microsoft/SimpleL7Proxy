#!/bin/bash
#
# Assign data-plane RBAC roles to the Function App's system-assigned managed
# identity so the Functions host (Flex Consumption) can:
#   - Read the deployment package blob (app-package-* container)
#   - Use AzureWebJobsStorage for host state, leases, secrets, and triggers
#
# Storage Blob Data Owner is a superset of Contributor and is required because
# the host needs to read blob metadata/ACLs on the lock blobs (otherwise you
# see HEAD ... 403 AuthorizationPermissionMismatch in the worker logs).

set -euo pipefail

RESOURCE_GROUP="nvmrequestapi"
FUNCTION_APP="nvmrequestapi"
STORAGE_ACCOUNT="nvmrequestapi"

GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m'

log() {
    local level=$1; shift
    local color
    case "$level" in
        INFO)  color=$GREEN ;;
        WARN)  color=$YELLOW ;;
        ERROR) color=$RED ;;
        *)     color=$NC ;;
    esac
    echo -e "${color}[$level]${NC} $(date '+%Y-%m-%d %H:%M:%S') - $*"
}

# Verify Azure CLI login
if ! az account show &>/dev/null; then
    log ERROR "Not logged into Azure CLI. Run 'az login' first."
    exit 1
fi

log INFO "Resolving managed identity principalId for $FUNCTION_APP..."
PRINCIPAL=$(az functionapp identity show \
    -g "$RESOURCE_GROUP" -n "$FUNCTION_APP" \
    --query principalId -o tsv 2>/dev/null || true)

if [ -z "$PRINCIPAL" ] || [ "$PRINCIPAL" = "null" ]; then
    log WARN "No system-assigned managed identity found. Enabling it now..."
    PRINCIPAL=$(az functionapp identity assign \
        -g "$RESOURCE_GROUP" -n "$FUNCTION_APP" \
        --query principalId -o tsv)
fi
log INFO "principalId: $PRINCIPAL"

log INFO "Resolving storage account id for $STORAGE_ACCOUNT..."
STORAGE_ID=$(az storage account show \
    -g "$RESOURCE_GROUP" -n "$STORAGE_ACCOUNT" \
    --query id -o tsv)
log INFO "storage id: $STORAGE_ID"

# Idempotent role assignment helper
assign_role() {
    local role=$1
    local count
    count=$(az role assignment list \
        --assignee "$PRINCIPAL" \
        --scope "$STORAGE_ID" \
        --query "[?roleDefinitionName=='$role'] | length(@)" -o tsv)
    if [ "${count:-0}" -gt 0 ]; then
        log INFO "Role '$role' already assigned. Skipping."
        return 0
    fi

    log INFO "Assigning role '$role'..."
    az role assignment create \
        --assignee "$PRINCIPAL" \
        --role "$role" \
        --scope "$STORAGE_ID" >/dev/null
}

# Storage Blob Data Owner covers both reading the deployment package AND
# all AzureWebJobsStorage blob operations (locks/leases/secrets/metadata).
assign_role "Storage Blob Data Owner"

# Required for the host's queue-based triggers and bookkeeping.
assign_role "Storage Queue Data Contributor"
assign_role "Storage Table Data Contributor"

log INFO "Current role assignments on the storage account:"
az role assignment list \
    --assignee "$PRINCIPAL" \
    --scope "$STORAGE_ID" \
    --query "[].{role:roleDefinitionName, scope:scope}" -o table

log INFO "Restarting Function App so the host picks up the new RBAC..."
az functionapp restart -g "$RESOURCE_GROUP" -n "$FUNCTION_APP" >/dev/null

log INFO "Done. RBAC propagation can take up to ~5 minutes; monitor the app health logs."