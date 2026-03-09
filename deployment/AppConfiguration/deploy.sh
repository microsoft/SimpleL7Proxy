#!/bin/bash

# Deploy/Update Azure App Configuration for BackendOptions
#
# Goals:
#   1. Migration  – seed App Configuration from a live Container App's
#                   env vars (with fallback to local shell variables).
#   2. Catalog    – every publishable setting is always written so that
#                   operators can see the full list in the portal.
#                   When no env value exists, the C# default from
#                   BackendOptions.cs is used. If even that is empty,
#                   a "-" placeholder is written, meaning "use the
#                   built-in code default".
#
# Discovers publishable keys dynamically from [ConfigOption("...")]
# decorations in BackendOptions.cs.
#
# Three modes (ConfigMode enum):
#   Warm   – published under "Warm:" prefix, hot-reloaded (~30 s)
#   Cold   – published under "Cold:" prefix, requires Container App restart
#   Hidden – not published (skipped by this script)
#
# Key prefix convention:
#   Warm settings → Warm:<Section>:<Key>  (e.g. Warm:Logging:LogConsole)
#   Cold settings → Cold:<Section>:<Key>  (e.g. Cold:Server:Workers)
#   The prefix itself tells you the reload mode at a glance in the portal.
#
# Sources env var values from the live Container App deployment, falling
# back to local shell variables when not defined on the Container App.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="${SCRIPT_DIR}/../.."

if [ -f "${SCRIPT_DIR}/deploy.parameters.sh" ]; then
    echo "Sourcing deploy.parameters.sh..."
    # shellcheck disable=SC1091
    source "${SCRIPT_DIR}/deploy.parameters.sh"
elif [ -f "${SCRIPT_DIR}/deploy.parameters.example.sh" ]; then
    echo "deploy.parameters.sh not found."
    echo "Copy deploy.parameters.example.sh to deploy.parameters.sh and update values."
    echo "Example: cp deploy.parameters.example.sh deploy.parameters.sh"
fi

# ----------------------------------------------------------------------------
# Required parameters
# ----------------------------------------------------------------------------
CONTAINER_APP_NAME="${CONTAINER_APP_NAME:?'CONTAINER_APP_NAME must be set'}"
CONTAINER_APP_RESOURCE_GROUP="${CONTAINER_APP_RESOURCE_GROUP:?'CONTAINER_APP_RESOURCE_GROUP must be set'}"
RESOURCE_GROUP="${RESOURCE_GROUP:?'RESOURCE_GROUP must be set (App Configuration resource group)'}"
LOCATION="${LOCATION:?'LOCATION must be set (App Configuration location)'}"
APPCONFIG_NAME="${APPCONFIG_NAME:?'APPCONFIG_NAME must be set'}"

# ----------------------------------------------------------------------------
# Optional overrides
# ----------------------------------------------------------------------------
APPCONFIG_SKU="${APPCONFIG_SKU:-standard}"
APPCONFIG_LABEL="${APPCONFIG_LABEL:-}"
AZURE_APPCONFIG_REFRESH_SECONDS="${AZURE_APPCONFIG_REFRESH_SECONDS:-30}"
UPDATE_CONTAINER_APP_ENV="${UPDATE_CONTAINER_APP_ENV:-true}"

BACKEND_OPTIONS_FILE="${BACKEND_OPTIONS_FILE:-${REPO_ROOT}/src/SimpleL7Proxy/Config/BackendOptions.cs}"

GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m'

# ----------------------------------------------------------------------------
# Preconditions
# ----------------------------------------------------------------------------
if ! command -v az >/dev/null 2>&1; then
    echo -e "${RED}Error: Azure CLI is not installed.${NC}"
    exit 1
fi

if [ ! -f "${BACKEND_OPTIONS_FILE}" ]; then
    echo -e "${RED}Error: BackendOptions file not found: ${BACKEND_OPTIONS_FILE}${NC}"
    exit 1
fi

echo -e "${YELLOW}Checking Azure login status...${NC}"
az account show >/dev/null 2>&1 || az login >/dev/null

SUBSCRIPTION_ID="$(az account show --query id -o tsv)"
echo -e "${GREEN}Using subscription: ${SUBSCRIPTION_ID}${NC}"

# ----------------------------------------------------------------------------
# Read the live Container App deployment
# ----------------------------------------------------------------------------
echo -e "${YELLOW}Reading Container App '${CONTAINER_APP_NAME}' from '${CONTAINER_APP_RESOURCE_GROUP}'...${NC}"
CA_JSON="$(az containerapp show \
    --name "${CONTAINER_APP_NAME}" \
    --resource-group "${CONTAINER_APP_RESOURCE_GROUP}" \
    -o json)" || { echo -e "${RED}Error: Could not read Container App.${NC}"; exit 1; }

# Derive the container name from the live deployment
CONTAINER_APP_CONTAINER_NAME="$(echo "${CA_JSON}" | jq -r '.properties.template.containers[0].name')"
echo -e "${GREEN}Container: ${CONTAINER_APP_CONTAINER_NAME}${NC}"

# Build an associative array of the Container App's current env vars
declare -A CA_ENV_VARS
while IFS=$'\t' read -r ename evalue; do
    [ -n "${ename}" ] && CA_ENV_VARS["${ename}"]="${evalue}"
done < <(echo "${CA_JSON}" | jq -r '
    .properties.template.containers[0].env[]?
    | select(.value != null)
    | [.name, .value]
    | @tsv
')
echo -e "${GREEN}Loaded ${#CA_ENV_VARS[@]} env vars from Container App${NC}"

# ----------------------------------------------------------------------------
# Create or reuse App Configuration store
# ----------------------------------------------------------------------------
echo -e "${YELLOW}Ensuring resource group '${RESOURCE_GROUP}' exists...${NC}"
az group create --name "${RESOURCE_GROUP}" --location "${LOCATION}" >/dev/null

EXISTING_APP_CONFIG="$(az appconfig show --name "${APPCONFIG_NAME}" --resource-group "${RESOURCE_GROUP}" --query name -o tsv 2>/dev/null || true)"
if [ -z "${EXISTING_APP_CONFIG}" ]; then
    echo -e "${YELLOW}Creating App Configuration store '${APPCONFIG_NAME}'...${NC}"
    az appconfig create \
        --name "${APPCONFIG_NAME}" \
        --resource-group "${RESOURCE_GROUP}" \
        --location "${LOCATION}" \
        --sku "${APPCONFIG_SKU}" \
        >/dev/null
else
    echo -e "${GREEN}Using existing App Configuration store: ${APPCONFIG_NAME}${NC}"
fi

APPCONFIG_ENDPOINT="$(az appconfig show --name "${APPCONFIG_NAME}" --resource-group "${RESOURCE_GROUP}" --query endpoint -o tsv)"
APPCONFIG_RESOURCE_ID="$(az appconfig show --name "${APPCONFIG_NAME}" --resource-group "${RESOURCE_GROUP}" --query id -o tsv)"

# ----------------------------------------------------------------------------
# Ensure the logged-in identity has data-plane write access (RBAC)
# ----------------------------------------------------------------------------
PRINCIPAL_ID="$(az ad signed-in-user show --query id -o tsv 2>/dev/null || true)"
if [ -n "${PRINCIPAL_ID}" ]; then
    EXISTING_ROLE="$(az role assignment list \
        --assignee "${PRINCIPAL_ID}" \
        --role "App Configuration Data Owner" \
        --scope "${APPCONFIG_RESOURCE_ID}" \
        --query "[0].id" -o tsv 2>/dev/null || true)"

    if [ -z "${EXISTING_ROLE}" ]; then
        echo -e "${YELLOW}Assigning 'App Configuration Data Owner' role to current user...${NC}"
        az role assignment create \
            --assignee "${PRINCIPAL_ID}" \
            --role "App Configuration Data Owner" \
            --scope "${APPCONFIG_RESOURCE_ID}" \
            >/dev/null
        echo -e "${YELLOW}Waiting for RBAC propagation (30s)...${NC}"
        sleep 30
    else
        echo -e "${GREEN}RBAC role already assigned.${NC}"
    fi
else
    echo -e "${YELLOW}Warning: Could not determine signed-in user principal. Ensure you have 'App Configuration Data Owner' role.${NC}"
fi

# ----------------------------------------------------------------------------
# Ensure the Container App's managed identity has data-plane read access
# ----------------------------------------------------------------------------
CA_PRINCIPAL_ID="$(echo "${CA_JSON}" | jq -r '.identity.principalId // empty')"
if [ -n "${CA_PRINCIPAL_ID}" ]; then
    EXISTING_CA_ROLE="$(az role assignment list \
        --assignee "${CA_PRINCIPAL_ID}" \
        --role "App Configuration Data Reader" \
        --scope "${APPCONFIG_RESOURCE_ID}" \
        --query "[0].id" -o tsv 2>/dev/null || true)"

    if [ -z "${EXISTING_CA_ROLE}" ]; then
        echo -e "${YELLOW}Assigning 'App Configuration Data Reader' role to Container App managed identity (${CA_PRINCIPAL_ID})...${NC}"
        az role assignment create \
            --assignee "${CA_PRINCIPAL_ID}" \
            --role "App Configuration Data Reader" \
            --scope "${APPCONFIG_RESOURCE_ID}" \
            >/dev/null
        echo -e "${GREEN}✓ Role assigned. RBAC propagation may take a few minutes.${NC}"
    else
        echo -e "${GREEN}Container App managed identity already has 'App Configuration Data Reader' role.${NC}"
    fi
else
    echo -e "${YELLOW}Warning: Container App has no system-assigned managed identity. Skipping RBAC assignment.${NC}"
fi

# ----------------------------------------------------------------------------
# Discover config options dynamically from [ConfigOption("...")] decorations.
# Handles:
#   [ConfigOption("Key:Path")]                                → Mode = Warm (default), ConfigName = PropertyName
#   [ConfigOption("Key:Path", ConfigName = "EnvVar")]         → Mode = Warm, ConfigName = EnvVar
#   [ConfigOption("Key:Path", Mode = ConfigMode.Cold)]        → Mode = Cold, ConfigName = PropertyName
#   [ConfigOption("Key:Path", Mode = ConfigMode.Hidden)]      → Skipped (not published)
# Emit: Property|KeyPath|ConfigName|Mode
# ----------------------------------------------------------------------------
mapfile -t CONFIG_ENTRIES < <(
    awk '
        /\[ConfigOption\("/ {
            # Skip entries marked Mode = ConfigMode.Hidden
            if ($0 ~ /Mode[[:space:]]*=[[:space:]]*ConfigMode\.Hidden/) next;

            key = "";
            configName = "";
            mode = "Warm";
            prop = "";
            defVal = "";

            # Extract KeyPath (first positional arg)
            if (match($0, /\[ConfigOption\("([^"]+)"/, m)) {
                key = m[1];
            }

            # Extract optional ConfigName = "..." on the same line
            if (match($0, /ConfigName[[:space:]]*=[[:space:]]*"([^"]+)"/, c)) {
                configName = c[1];
            }

            # Extract optional Mode = ConfigMode.Cold on the same line
            if ($0 ~ /Mode[[:space:]]*=[[:space:]]*ConfigMode\.Cold/) {
                mode = "Cold";
            }

            # Read ahead to find the property declaration
            while (getline > 0) {
                # Skip other attributes
                if ($0 ~ /^[[:space:]]*\[/) continue;

                if ($0 ~ /^[[:space:]]*public[[:space:]]+/) {
                    if (match($0, /^[[:space:]]*public[[:space:]]+[^ ]+[[:space:]]+([A-Za-z_][A-Za-z0-9_]*)[[:space:]]*\{/, p)) {
                        prop = p[1];
                        # Extract default value from "} = VALUE;" pattern
                        defVal = "";
                        if (match($0, /\}[[:space:]]*=[[:space:]]*(.+);/, dv)) {
                            defVal = dv[1];
                            sub(/^[[:space:]]+/, "", defVal);
                            sub(/[[:space:]]+$/, "", defVal);
                            if (match(defVal, /^"(.*)"$/, q)) {
                                # Quoted string — extract content directly.
                                # Do NOT strip // comments here; the URL
                                # inside (e.g. https://) is part of the value.
                                defVal = q[1];
                            } else {
                                # Non-string (number, bool, enum) — safe to
                                # strip trailing C# inline // comments.
                                sub(/[[:space:]]*\/\/.*$/, "", defVal);
                                sub(/^[[:space:]]+/, "", defVal);
                                sub(/[[:space:]]+$/, "", defVal);
                            }
                        }
                        break;
                    }
                }
            }

            if (key != "" && prop != "") {
                # Default ConfigName to the property name
                if (configName == "") configName = prop;
                print prop "|" key "|" configName "|" mode "|" defVal;
            }
        }
    ' "${BACKEND_OPTIONS_FILE}" | tr -d '\r'
)

if [ "${#CONFIG_ENTRIES[@]}" -eq 0 ]; then
    echo -e "${RED}No [ConfigOption(...)] decorations found. Nothing to deploy.${NC}"
    exit 1
fi

# Placeholder written when no env value AND no C# default exist.
# The proxy treats "-" as "use the built-in code default".
DEFAULT_PLACEHOLDER="-"

echo -e "${YELLOW}Publishing config keys to App Configuration...${NC}"
SET_COUNT=0
DEFAULT_COUNT=0
WARM_COUNT=0
COLD_COUNT=0

# Build a single JSON file for batch import (all keys, single label).
IMPORT_JSON_FILE="$(mktemp)"
trap 'rm -f "${IMPORT_JSON_FILE}"' EXIT

echo "{" > "${IMPORT_JSON_FILE}"
JSON_FIRST=true

for entry in "${CONFIG_ENTRIES[@]}"; do
    PROP_NAME="$(echo "${entry}" | cut -d'|' -f1)"
    KEY_PATH="$(echo "${entry}" | cut -d'|' -f2)"
    CONFIG_NAME="$(echo "${entry}" | cut -d'|' -f3)"
    MODE="$(echo "${entry}" | cut -d'|' -f4)"
    CS_DEFAULT="$(echo "${entry}" | cut -d'|' -f5-)"
    # Prefix matches the mode: Warm:Section:Key or Cold:Section:Key
    APP_CONFIG_KEY="${MODE}:${KEY_PATH}"

    ENV_NAME="${CONFIG_NAME:-${PROP_NAME}}"
    VALUE=""
    SOURCE=""

    # 1) Look up the value from the live Container App env vars
    if [ -n "${CA_ENV_VARS[${ENV_NAME}]+x}" ]; then
        VALUE="${CA_ENV_VARS[${ENV_NAME}]}"
        SOURCE="container-app"
    fi

    # 2) Fallback to local shell env vars
    if [ -z "${VALUE}" ] && [[ "${ENV_NAME}" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]]; then
        VALUE="${!ENV_NAME-}"
        [ -n "${VALUE}" ] && SOURCE="local-env"
    fi

    # 3) Fallback to C# default from BackendOptions.cs
    if [ -z "${VALUE}" ] && [ -n "${CS_DEFAULT}" ]; then
        VALUE="${CS_DEFAULT}"
        SOURCE="cs-default"
        # Handle enum defaults like "TypeName.Value" → "Value"
        # Only match Identifier.Identifier (e.g. IterationModeEnum.SinglePass)
        # Avoid mangling URLs, file paths, or floats that also contain dots
        if [[ "${VALUE}" =~ ^[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*$ ]]; then
            VALUE="${VALUE##*.}"
        fi
    fi

    # 4) No value at all → write placeholder so the key is still visible
    if [ -z "${VALUE}" ]; then
        VALUE="${DEFAULT_PLACEHOLDER}"
        SOURCE="placeholder"
    fi

    # Strip wrapping double quotes that may survive from C# string literal
    # defaults (AWK quote-strip can fail on non-gawk) or from Container App
    # env vars / local env vars that were set with accidental quoting.
    if [[ "${VALUE}" == '"'*'"' ]]; then
        VALUE="${VALUE#\"}"
        VALUE="${VALUE%\"}"
    fi

    # Escape for JSON (handle backslashes, quotes, newlines)
    JSON_VALUE="$(printf '%s' "${VALUE}" | sed 's/\\/\\\\/g; s/"/\\"/g')"

    # Append to the JSON file
    if [ "${JSON_FIRST}" = true ]; then JSON_FIRST=false; else echo "," >> "${IMPORT_JSON_FILE}"; fi
    printf '  "%s": "%s"' "${APP_CONFIG_KEY}" "${JSON_VALUE}" >> "${IMPORT_JSON_FILE}"

    if [ "${MODE}" = "Cold" ]; then
        COLD_COUNT=$((COLD_COUNT + 1))
    else
        WARM_COUNT=$((WARM_COUNT + 1))
    fi

    echo -e "${GREEN}  ${APP_CONFIG_KEY} = ${VALUE} (${SOURCE}) [${MODE}]${NC}"
    SET_COUNT=$((SET_COUNT + 1))
    if [ "${SOURCE}" = "cs-default" ] || [ "${SOURCE}" = "placeholder" ]; then
        DEFAULT_COUNT=$((DEFAULT_COUNT + 1))
    fi
done

# Add Sentinel and RefreshSeconds to the import batch (always Warm)
echo "," >> "${IMPORT_JSON_FILE}"
printf '  "Warm:Sentinel": "%s",\n' "$(date -u +%s)" >> "${IMPORT_JSON_FILE}"
printf '  "Warm:RefreshSeconds": "%s"' "${AZURE_APPCONFIG_REFRESH_SECONDS}" >> "${IMPORT_JSON_FILE}"

echo "" >> "${IMPORT_JSON_FILE}"
echo "}" >> "${IMPORT_JSON_FILE}"

# Import all settings in a single batch call
echo -e "${YELLOW}Importing ${SET_COUNT} settings (Warm: ${WARM_COUNT}, Cold: ${COLD_COUNT}) + Sentinel, RefreshSeconds...${NC}"
az appconfig kv import \
    --name "${APPCONFIG_NAME}" \
    --source file \
    --path "${IMPORT_JSON_FILE}" \
    --format json \
    --label "${APPCONFIG_LABEL}" \
    --yes \
    --auth-mode login \
    >/dev/null
echo -e "${GREEN}✓ Import complete${NC}"

# Optionally wire container app env vars
if [ "${UPDATE_CONTAINER_APP_ENV}" = "true" ]; then
    echo -e "${YELLOW}Updating Container App env vars...${NC}"
    az containerapp update \
        --name "${CONTAINER_APP_NAME}" \
        --resource-group "${CONTAINER_APP_RESOURCE_GROUP}" \
        --container-name "${CONTAINER_APP_CONTAINER_NAME}" \
        --set-env-vars \
            "AZURE_APPCONFIG_ENDPOINT=${APPCONFIG_ENDPOINT}" \
            "AZURE_APPCONFIG_LABEL=${APPCONFIG_LABEL}" \
            "AZURE_APPCONFIG_REFRESH_SECONDS=${AZURE_APPCONFIG_REFRESH_SECONDS}" \
        >/dev/null || echo -e "${YELLOW}Warning: Could not update Container App env vars (continuing).${NC}"
fi

echo -e "${GREEN}======================================${NC}"
echo -e "${GREEN}App Configuration deployment complete${NC}"
echo -e "${GREEN}======================================${NC}"
echo -e "${GREEN}Store: ${APPCONFIG_NAME}${NC}"
echo -e "${GREEN}Endpoint: ${APPCONFIG_ENDPOINT}${NC}"
echo -e "${GREEN}Label: ${APPCONFIG_LABEL:-(none)}${NC}"
echo -e "${GREEN}Config keys published: ${SET_COUNT} (Warm: ${WARM_COUNT}, Cold: ${COLD_COUNT})${NC}"
echo -e "${GREEN}  of which ${DEFAULT_COUNT} used C# default or '${DEFAULT_PLACEHOLDER}' placeholder${NC}"
echo -e "${GREEN}======================================${NC}"
