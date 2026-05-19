#!/bin/bash
#
# Provision the RequestAPI Azure Function App (Flex Consumption, .NET 9 isolated).
#
# What this creates / configures:
#   1. Storage account (for AzureWebJobsStorage + Flex deployment package container)
#   2. Application Insights (linked to the function app)
#   3. Flex Consumption Function App (dotnet-isolated 9.0) with system-assigned MI
#   4. App settings for identity-based connections to:
#        - AzureWebJobsStorage  (Blob, Queue, Table)
#        - Service Bus          (queue trigger + output binding)
#        - Cosmos DB            (input + output binding)
#      plus Application Insights connection string.
#   5. RBAC role assignments for the function app's managed identity:
#        - Storage Blob Data Owner / Queue Data Contributor / Table Data Contributor
#        - Azure Service Bus Data Receiver + Sender (on the SB namespace)
#        - Cosmos DB SQL Data Contributor (data-plane role, on the Cosmos account)
#
# External dependencies that must already exist (created elsewhere):
#   - Service Bus namespace with the request + feeder queues
#   - Cosmos DB account, database, container
#
# Idempotent: safe to re-run. Existing resources are reused; missing ones
# are created; role assignments are skipped if already present.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PARENT_PARAMS="${SCRIPT_DIR}/../deploy.parameters.sh"

if [ ! -f "${PARENT_PARAMS}" ]; then
    echo "Error: ${PARENT_PARAMS} not found."
    echo "Copy deploy.parameters.example.sh to deploy.parameters.sh and edit values."
    exit 1
fi
# shellcheck disable=SC1091
source "${PARENT_PARAMS}"

GREEN='\033[0;32m'; YELLOW='\033[1;33m'; RED='\033[0;31m'; NC='\033[0m'
log() {
    local lvl=$1; shift
    local c=$NC
    case "$lvl" in INFO) c=$GREEN ;; WARN) c=$YELLOW ;; ERROR) c=$RED ;; esac
    echo -e "${c}[$lvl]${NC} $(date '+%H:%M:%S') - $*"
}

# Prompt the user to confirm reuse of an existing globally-unique resource
# that was found in a different resource group than configured.
# Usage: confirm_reuse <resource-kind> <name> <found-rg> <configured-rg>
# Returns 0 if user accepts, 1 otherwise.
confirm_reuse() {
    local kind=$1 name=$2 found_rg=$3 configured_rg=$4
    echo ""
    log WARN "${kind} '${name}' already exists in resource group '${found_rg}',"
    log WARN "but deploy.parameters.sh has REQUESTAPI_RESOURCE_GROUP='${configured_rg}'."
    log WARN "${kind} names are globally unique, so a new one cannot be created with this name."
    echo ""
    local reply
    read -r -p "Reuse the existing ${kind} in '${found_rg}'? [y/N] " reply
    case "${reply}" in
        y|Y|yes|YES) return 0 ;;
        *)           return 1 ;;
    esac
}

# ----------------------------------------------------------------------------
# Required parameters
# ----------------------------------------------------------------------------
: "${REQUESTAPI_RESOURCE_GROUP:?must be set in deploy.parameters.sh}"
: "${REQUESTAPI_FUNCTION_APP:?must be set in deploy.parameters.sh}"
: "${REQUESTAPI_LOCATION:?must be set in deploy.parameters.sh}"
: "${REQUESTAPI_STORAGE_ACCOUNT:?must be set in deploy.parameters.sh}"
: "${REQUESTAPI_APPINSIGHTS_NAME:?must be set in deploy.parameters.sh}"
: "${REQUESTAPI_SERVICEBUS_NAMESPACE:?must be set in deploy.parameters.sh}"
: "${REQUESTAPI_SERVICEBUS_QUEUE:?must be set in deploy.parameters.sh}"
: "${REQUESTAPI_SERVICEBUS_FEEDER_QUEUE:?must be set in deploy.parameters.sh}"
: "${REQUESTAPI_COSMOS_ACCOUNT:?must be set in deploy.parameters.sh}"
: "${REQUESTAPI_COSMOS_DATABASE:?must be set in deploy.parameters.sh}"
: "${REQUESTAPI_COSMOS_CONTAINER:?must be set in deploy.parameters.sh}"

RUNTIME_NAME="${REQUESTAPI_RUNTIME_NAME:-dotnet-isolated}"
RUNTIME_VERSION="${REQUESTAPI_RUNTIME_VERSION:-9.0}"
INSTANCE_MEMORY_MB="${REQUESTAPI_INSTANCE_MEMORY_MB:-2048}"
MAX_INSTANCE_COUNT="${REQUESTAPI_MAX_INSTANCE_COUNT:-100}"

# ----------------------------------------------------------------------------
# Preconditions
# ----------------------------------------------------------------------------
command -v az >/dev/null || { log ERROR "Azure CLI not installed"; exit 1; }
az account show >/dev/null 2>&1 || { log ERROR "Run 'az login' first"; exit 1; }

SUBSCRIPTION_ID="$(az account show --query id -o tsv)"
log INFO "Subscription: ${SUBSCRIPTION_ID}"

# ----------------------------------------------------------------------------
# 1. Resource group
# ----------------------------------------------------------------------------
if ! az group show -n "${REQUESTAPI_RESOURCE_GROUP}" >/dev/null 2>&1; then
    log INFO "Creating resource group ${REQUESTAPI_RESOURCE_GROUP}..."
    az group create -n "${REQUESTAPI_RESOURCE_GROUP}" -l "${REQUESTAPI_LOCATION}" >/dev/null
else
    log INFO "Resource group ${REQUESTAPI_RESOURCE_GROUP} already exists."
fi

# ----------------------------------------------------------------------------
# 2. Storage account
# ----------------------------------------------------------------------------
STORAGE_RG=""
if az storage account show -g "${REQUESTAPI_RESOURCE_GROUP}" -n "${REQUESTAPI_STORAGE_ACCOUNT}" >/dev/null 2>&1; then
    log INFO "Storage account ${REQUESTAPI_STORAGE_ACCOUNT} already exists in ${REQUESTAPI_RESOURCE_GROUP}."
    STORAGE_RG="${REQUESTAPI_RESOURCE_GROUP}"
else
    # Storage account names are globally unique. Check if it exists anywhere
    # in our subscription before attempting to create.
    EXISTING_SA_RG=$(az storage account list \
        --query "[?name=='${REQUESTAPI_STORAGE_ACCOUNT}'].resourceGroup | [0]" -o tsv 2>/dev/null || true)
    if [ -z "${EXISTING_SA_RG}" ]; then
        # Fallback: az resource list sees resources the caller can read even
        # when az storage account list filters them out.
        EXISTING_SA_RG=$(az resource list \
            --name "${REQUESTAPI_STORAGE_ACCOUNT}" \
            --resource-type "Microsoft.Storage/storageAccounts" \
            --query "[0].resourceGroup" -o tsv 2>/dev/null || true)
    fi
    if [ -n "${EXISTING_SA_RG}" ]; then
        if confirm_reuse "Storage account" "${REQUESTAPI_STORAGE_ACCOUNT}" "${EXISTING_SA_RG}" "${REQUESTAPI_RESOURCE_GROUP}"; then
            log INFO "Reusing storage account ${REQUESTAPI_STORAGE_ACCOUNT} from ${EXISTING_SA_RG}."
            STORAGE_RG="${EXISTING_SA_RG}"
        else
            log ERROR "Aborted. Pick a different REQUESTAPI_STORAGE_ACCOUNT name in deploy.parameters.sh and retry."
            exit 1
        fi
    else
        log INFO "Creating storage account ${REQUESTAPI_STORAGE_ACCOUNT}..."
        az storage account create \
            -g "${REQUESTAPI_RESOURCE_GROUP}" \
            -n "${REQUESTAPI_STORAGE_ACCOUNT}" \
            -l "${REQUESTAPI_LOCATION}" \
            --sku Standard_LRS \
            --kind StorageV2 \
            --allow-shared-key-access true \
            --public-network-access Enabled \
            --min-tls-version TLS1_2 >/dev/null
        STORAGE_RG="${REQUESTAPI_RESOURCE_GROUP}"
    fi
fi

STORAGE_ID=$(az storage account show \
    -g "${STORAGE_RG}" -n "${REQUESTAPI_STORAGE_ACCOUNT}" \
    --query id -o tsv 2>/dev/null || true)
if [ -z "${STORAGE_ID}" ]; then
    log ERROR "Could not resolve storage account ${REQUESTAPI_STORAGE_ACCOUNT} in ${STORAGE_RG}."
    exit 1
fi

# ----------------------------------------------------------------------------
# 3. Application Insights (workspace-based fallback to classic if needed)
# ----------------------------------------------------------------------------
if ! az extension show -n application-insights >/dev/null 2>&1; then
    log INFO "Installing application-insights CLI extension..."
    az extension add -n application-insights >/dev/null
fi

if ! az monitor app-insights component show \
        -g "${REQUESTAPI_RESOURCE_GROUP}" -a "${REQUESTAPI_APPINSIGHTS_NAME}" >/dev/null 2>&1; then
    log INFO "Creating Application Insights ${REQUESTAPI_APPINSIGHTS_NAME}..."
    az monitor app-insights component create \
        -g "${REQUESTAPI_RESOURCE_GROUP}" \
        -a "${REQUESTAPI_APPINSIGHTS_NAME}" \
        -l "${REQUESTAPI_LOCATION}" \
        --kind web \
        --application-type web >/dev/null
else
    log INFO "Application Insights ${REQUESTAPI_APPINSIGHTS_NAME} already exists."
fi

AI_CONNECTION_STRING=$(az monitor app-insights component show \
    -g "${REQUESTAPI_RESOURCE_GROUP}" -a "${REQUESTAPI_APPINSIGHTS_NAME}" \
    --query connectionString -o tsv)

# ----------------------------------------------------------------------------
# 4. Flex Consumption Function App
# ----------------------------------------------------------------------------
# Function App names are globally unique. Search the whole subscription so we
# don't try to create one that already exists in a different resource group.
EXISTING_FA_RG=$(az functionapp list \
    --query "[?name=='${REQUESTAPI_FUNCTION_APP}'].resourceGroup | [0]" -o tsv 2>/dev/null || true)

if [ -z "${EXISTING_FA_RG}" ]; then
    log INFO "Creating Flex Consumption Function App ${REQUESTAPI_FUNCTION_APP}..."
    az functionapp create \
        -g "${REQUESTAPI_RESOURCE_GROUP}" \
        -n "${REQUESTAPI_FUNCTION_APP}" \
        --storage-account "${REQUESTAPI_STORAGE_ACCOUNT}" \
        --flexconsumption-location "${REQUESTAPI_LOCATION}" \
        --runtime "${RUNTIME_NAME}" \
        --runtime-version "${RUNTIME_VERSION}" \
        --instance-memory "${INSTANCE_MEMORY_MB}" \
        --maximum-instance-count "${MAX_INSTANCE_COUNT}" \
        --assign-identity '[system]' >/dev/null
else
    if [ "${EXISTING_FA_RG}" != "${REQUESTAPI_RESOURCE_GROUP}" ]; then
        if confirm_reuse "Function App" "${REQUESTAPI_FUNCTION_APP}" "${EXISTING_FA_RG}" "${REQUESTAPI_RESOURCE_GROUP}"; then
            log INFO "Reusing Function App ${REQUESTAPI_FUNCTION_APP} from ${EXISTING_FA_RG}."
            log INFO "Switching REQUESTAPI_RESOURCE_GROUP to '${EXISTING_FA_RG}' for the rest of this run."
            log INFO "Edit deploy.parameters.sh to make this permanent."
            REQUESTAPI_RESOURCE_GROUP="${EXISTING_FA_RG}"
        else
            log ERROR "Aborted. Pick a different REQUESTAPI_FUNCTION_APP name in deploy.parameters.sh and retry."
            exit 1
        fi
    else
        log INFO "Function App ${REQUESTAPI_FUNCTION_APP} already exists in ${REQUESTAPI_RESOURCE_GROUP}."
    fi
    # Make sure system identity is enabled
    az functionapp identity assign \
        -g "${REQUESTAPI_RESOURCE_GROUP}" -n "${REQUESTAPI_FUNCTION_APP}" >/dev/null
fi

PRINCIPAL_ID=$(az functionapp identity show \
    -g "${REQUESTAPI_RESOURCE_GROUP}" -n "${REQUESTAPI_FUNCTION_APP}" \
    --query principalId -o tsv)
log INFO "Function App MI principalId: ${PRINCIPAL_ID}"

# ----------------------------------------------------------------------------
# 5. App settings (identity-based connections)
# ----------------------------------------------------------------------------
log INFO "Setting app settings (identity-based connections)..."
SB_NAMESPACE_FQDN="${REQUESTAPI_SERVICEBUS_NAMESPACE}.servicebus.windows.net"
COSMOS_ENDPOINT="https://${REQUESTAPI_COSMOS_ACCOUNT}.documents.azure.com:443/"

az functionapp config appsettings set \
    -g "${REQUESTAPI_RESOURCE_GROUP}" -n "${REQUESTAPI_FUNCTION_APP}" \
    --settings \
        "APPLICATIONINSIGHTS_CONNECTION_STRING=${AI_CONNECTION_STRING}" \
        "AzureWebJobsStorage__accountName=${REQUESTAPI_STORAGE_ACCOUNT}" \
        "ServiceBusConnection__fullyQualifiedNamespace=${SB_NAMESPACE_FQDN}" \
        "ServiceBusQueue=${REQUESTAPI_SERVICEBUS_QUEUE}" \
        "SBFeederQueue=${REQUESTAPI_SERVICEBUS_FEEDER_QUEUE}" \
        "CosmosDbConnection__accountEndpoint=${COSMOS_ENDPOINT}" \
        "CosmosDb__DatabaseName=${REQUESTAPI_COSMOS_DATABASE}" \
        "CosmosDb__ContainerName=${REQUESTAPI_COSMOS_CONTAINER}" \
    >/dev/null

az functionapp config appsettings delete \
    -g "${REQUESTAPI_RESOURCE_GROUP}" \
    -n "${REQUESTAPI_FUNCTION_APP}" \
    --setting-names AzureWebJobsStorage DEPLOYMENT_STORAGE_CONNECTION_STRING \
    >/dev/null 2>&1 || true

# ----------------------------------------------------------------------------
# 6. RBAC
# ----------------------------------------------------------------------------
assign_role() {
    local role=$1 scope=$2
    local count
    count=$(az role assignment list --assignee "${PRINCIPAL_ID}" --scope "${scope}" \
        --query "[?roleDefinitionName=='${role}'] | length(@)" -o tsv 2>/dev/null || echo 0)
    if [ "${count:-0}" -gt 0 ]; then
        log INFO "  '${role}' already assigned on $(basename "${scope}"). Skipping."
        return
    fi
    log INFO "  Assigning '${role}' on $(basename "${scope}")..."
    az role assignment create --assignee "${PRINCIPAL_ID}" --role "${role}" --scope "${scope}" >/dev/null
}

log INFO "Granting storage roles..."
assign_role "Storage Blob Data Owner"        "${STORAGE_ID}"
assign_role "Storage Queue Data Contributor" "${STORAGE_ID}"
assign_role "Storage Table Data Contributor" "${STORAGE_ID}"

DEPLOYMENT_STORAGE_URL=$(az functionapp deployment config show \
    -g "${REQUESTAPI_RESOURCE_GROUP}" \
    -n "${REQUESTAPI_FUNCTION_APP}" \
    --query storage.value -o tsv 2>/dev/null || true)
DEPLOYMENT_STORAGE_CONTAINER="${DEPLOYMENT_STORAGE_URL##*/}"
if [ -z "${DEPLOYMENT_STORAGE_CONTAINER}" ] || [ "${DEPLOYMENT_STORAGE_CONTAINER}" = "${DEPLOYMENT_STORAGE_URL}" ]; then
    DEPLOYMENT_STORAGE_CONTAINER="app-package-${REQUESTAPI_FUNCTION_APP}"
fi

if ! az storage container-rm exists \
        --resource-group "${STORAGE_RG}" \
        --storage-account "${REQUESTAPI_STORAGE_ACCOUNT}" \
        --name "${DEPLOYMENT_STORAGE_CONTAINER}" \
        --query exists -o tsv 2>/dev/null | grep -qx "true"; then
    log INFO "Creating deployment storage container ${DEPLOYMENT_STORAGE_CONTAINER}..."
    az storage container-rm create \
        --resource-group "${STORAGE_RG}" \
        --storage-account "${REQUESTAPI_STORAGE_ACCOUNT}" \
        --name "${DEPLOYMENT_STORAGE_CONTAINER}" \
        --public-access off \
        >/dev/null
fi

log INFO "Configuring deployment storage to use system-assigned managed identity..."
az functionapp deployment config set \
    -g "${REQUESTAPI_RESOURCE_GROUP}" \
    -n "${REQUESTAPI_FUNCTION_APP}" \
    --deployment-storage-name "${REQUESTAPI_STORAGE_ACCOUNT}" \
    --deployment-storage-container-name "${DEPLOYMENT_STORAGE_CONTAINER}" \
    --deployment-storage-auth-type SystemAssignedIdentity \
    >/dev/null

# Service Bus namespace (must exist)
SB_NS_ID=$(az servicebus namespace show \
    -g "${REQUESTAPI_RESOURCE_GROUP}" -n "${REQUESTAPI_SERVICEBUS_NAMESPACE}" \
    --query id -o tsv 2>/dev/null || true)
if [ -z "${SB_NS_ID}" ]; then
    # Try resolving by name across the subscription in case it lives elsewhere
    SB_NS_ID=$(az resource list --resource-type Microsoft.ServiceBus/namespaces \
        --name "${REQUESTAPI_SERVICEBUS_NAMESPACE}" --query "[0].id" -o tsv 2>/dev/null || true)
fi
if [ -n "${SB_NS_ID}" ]; then
    log INFO "Granting Service Bus roles on namespace ${REQUESTAPI_SERVICEBUS_NAMESPACE}..."
    assign_role "Azure Service Bus Data Receiver" "${SB_NS_ID}"
    assign_role "Azure Service Bus Data Sender"   "${SB_NS_ID}"
else
    log WARN "Service Bus namespace ${REQUESTAPI_SERVICEBUS_NAMESPACE} not found. Skipping SB RBAC."
fi

# Cosmos DB SQL data-plane role (NOT regular RBAC)
COSMOS_RG=$(az cosmosdb list --query "[?name=='${REQUESTAPI_COSMOS_ACCOUNT}'].resourceGroup | [0]" -o tsv 2>/dev/null || true)
if [ -n "${COSMOS_RG}" ]; then
    log INFO "Granting Cosmos DB SQL data-plane Contributor on account ${REQUESTAPI_COSMOS_ACCOUNT}..."
    COSMOS_ACCOUNT_ID=$(az cosmosdb show -g "${COSMOS_RG}" -n "${REQUESTAPI_COSMOS_ACCOUNT}" --query id -o tsv)
    # Built-in role: "Cosmos DB Built-in Data Contributor" = 00000000-0000-0000-0000-000000000002
    if az cosmosdb sql role assignment list \
            -g "${COSMOS_RG}" -a "${REQUESTAPI_COSMOS_ACCOUNT}" \
            --query "[?principalId=='${PRINCIPAL_ID}'] | length(@)" -o tsv | grep -q '^[1-9]'; then
        log INFO "  Cosmos data-plane role already assigned. Skipping."
    else
        az cosmosdb sql role assignment create \
            -g "${COSMOS_RG}" -a "${REQUESTAPI_COSMOS_ACCOUNT}" \
            --role-definition-id "00000000-0000-0000-0000-000000000002" \
            --principal-id "${PRINCIPAL_ID}" \
            --scope "${COSMOS_ACCOUNT_ID}" >/dev/null
    fi
else
    log WARN "Cosmos DB account ${REQUESTAPI_COSMOS_ACCOUNT} not found. Skipping Cosmos RBAC."
fi

log INFO "Restarting Function App so the host picks up the new settings/RBAC..."
az functionapp restart -g "${REQUESTAPI_RESOURCE_GROUP}" -n "${REQUESTAPI_FUNCTION_APP}" >/dev/null

log INFO "Done. Next: run ./deploy.sh to publish the code."
