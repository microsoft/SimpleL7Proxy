#!/usr/bin/env bash
# secureProxy.sh
#
# Configure Entra ID app registration for a proxy Container App.
# Idempotent: safe to re-run. Each step checks current state before modifying.
#
# Three modes:
#
#   -m ACA   (default)
#       Creates app registration + delegated scope 'api.access' + client secret
#       and enables Container Apps EasyAuth (Microsoft provider) on the proxy.
#       Use when the platform should enforce auth on the proxy's ingress.
#
#   -m APIM
#       Creates app registration + app role 'API.Caller' and assigns that role
#       to the proxy Container App's managed identity. ACA auth is NOT modified.
#       Use when the proxy calls APIM with its managed identity and APIM
#       validates the token via policy.
#
#   -m ADDCLIENT  (or pass -z <client-id> with no -m)
#       Adds an arbitrary client app ID to the API app's preAuthorizedApplications
#       for the 'api.access' scope. If -n and -g are also given, also adds the
#       client ID to the Container App's EasyAuth allowedApplications so the
#       client's tokens are accepted at runtime (otherwise the proxy returns 403).
#
# Usage:
#   secureProxy.sh -a <entra-app-name> [-n <container-app-name>] [-g <resource-group>] [-m <mode>] [-z <client-id>]

set -euo pipefail

# ----------------------------------------------------------------------------
# Logging helpers
# ----------------------------------------------------------------------------
log()   { printf '\033[0;36m[%s]\033[0m %s\n' "$(date +%H:%M:%S)" "$*" >&2; }
ok()    { printf '\033[0;32m[ OK ]\033[0m %s\n' "$*" >&2; }
warn()  { printf '\033[0;33m[WARN]\033[0m %s\n' "$*" >&2; }
err()   { printf '\033[0;31m[FAIL]\033[0m %s\n' "$*" >&2; }

# die <message> [remediation hint...]
# Prints a clearly formatted failure block and exits 1.
die() {
  err "$1"
  shift
  if [[ $# -gt 0 ]]; then
    printf '\033[0;31m       \u2192 %s\033[0m\n' "$@" >&2
  fi
  exit 1
}

# run_az <human-readable description> <az command...>
# Captures stderr so the real Azure error is surfaced verbatim when something fails.
run_az() {
  local description="$1"; shift
  local stderr_file
  stderr_file="$(mktemp)"
  if ! "$@" 2>"$stderr_file"; then
    err "$description failed"
    printf '\033[0;31m       command: %s\033[0m\n' "$*" >&2
    printf '\033[0;31m       azure error:\033[0m\n' >&2
    sed 's/^/         /' "$stderr_file" >&2
    rm -f "$stderr_file"
    exit 1
  fi
  rm -f "$stderr_file"
}

trap 'err "Aborted at line $LINENO while running: $BASH_COMMAND"' ERR

# ============================================================================
# Global state — populated by functions; intentionally module-scoped so each
# step is small and readable. Keep this list as the single source of truth.
# ============================================================================

# Inputs (set by parse_args)
CONTAINER_APP_NAME=""
RG=""
ENTRA_APP_NAME=""
MODE="ACA"
EXTRA_CLIENT_ID=""   # set by -z / --authorize; client app ID to pre-authorize

# Azure context (set by require_logged_in)
CURRENT_SUB=""
TENANT_ID=""

# Container App facts (set by discover_container_app)
APP_FQDN=""
HEALTH_URL=""
MI_PRINCIPAL_ID=""
MI_TYPE=""

# App registration (set by ensure_app_registration / ensure_service_principal)
APP_ID=""
SP_OID=""

# ACA-only state
CLIENT_SECRET=""

# APIM-only state
readonly APP_ROLE_VALUE="API.Caller"
readonly APP_ROLE_DISPLAY="API Caller"
readonly APP_ROLE_DESC="Applications assigned this role may invoke the proxy via APIM."
APP_ROLE_ID=""
API_SP_OID=""

# ============================================================================
# CLI plumbing
# ============================================================================

usage() {
  cat <<EOF >&2
Configures an Entra ID app registration that secures a SimpleL7Proxy Container App.
Pick a mode to:
  - secure inbound traffic to the proxy via ACA EasyAuth (ACA mode)
  - secure outbound calls from the proxy to APIM, where the proxy authenticates
    with its managed identity and APIM validates the token (APIM mode)
  - pre-authorize additional client apps to call the proxy (ADDCLIENT mode)
Idempotent: safe to re-run.

Usage:
  $(basename "$0") -a <entra-app-name> [-n <container-app-name>] [-g <resource-group>]
                  [-m <mode>] [-z <client-id>]

Options:
  -a, --app-name <name>        Display name for the Entra app registration to create/reuse.
  -n, --container-app <name>   Container App name (the proxy). Required for ACA and APIM.
  -g, --resource-group <name>  Resource group of the Container App. Required for ACA and APIM.
  -m, --mode <mode>            One of: ACA (default), APIM, ADDCLIENT.
                                 ACA       configure ACA EasyAuth (Microsoft provider) on the proxy.
                                 APIM      create app role and assign it to the proxy managed identity;
                                           ACA auth is left untouched (APIM validates the token).
                                 ADDCLIENT add a client app ID to the API app's preAuthorizedApplications
                                           for the 'api.access' scope, and (if -n/-g are also given) to
                                           the Container App's EasyAuth allowedApplications list.
  -z, --authorize <client-id>  Client app ID (GUID) to pre-authorize. Implies --mode ADDCLIENT.
  -h, --help                   Show this help.
EOF
  exit 1
}

parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      -n|--container-app)  CONTAINER_APP_NAME="${2:-}"; shift 2 ;;
      -g|--resource-group) RG="${2:-}"; shift 2 ;;
      -a|--app-name)       ENTRA_APP_NAME="${2:-}"; shift 2 ;;
      -m|--mode)           MODE="${2:-}"; shift 2 ;;
      -z|--authorize)      EXTRA_CLIENT_ID="${2:-}"; shift 2 ;;
      -h|--help)           usage ;;
      --)                  shift; break ;;
      -*)                  err "Unknown option: $1"; usage ;;
      *)                   err "Unexpected positional argument: $1"; usage ;;
    esac
  done

  # -z implies ADDCLIENT mode when -m wasn't explicitly given a non-default value.
  if [[ -n "$EXTRA_CLIENT_ID" && "$MODE" == "ACA" ]]; then
    MODE="ADDCLIENT"
  fi

  local script_name
  script_name="$(basename "$0")"
  # Each line below becomes its own '→ ...' remediation hint.
  local ex_desc1="Configures an Entra ID app registration that secures a SimpleL7Proxy Container App." \
        ex_desc2="Pick a mode:" \
        ex_desc3="  ACA       - secure inbound traffic to the proxy via EasyAuth." \
        ex_desc4="  APIM      - secure outbound calls from the proxy to APIM via managed identity." \
        ex_desc5="  ADDCLIENT - pre-authorize additional client apps to call the proxy." \
        ex_blank="" \
        ex_header="Examples:" \
        ex_aca="  ACA       : $script_name -a <entra-app-name> -n <container-app-name> -g <resource-group>" \
        ex_apimmi="  APIM      : $script_name -a <entra-app-name> -n <container-app-name> -g <resource-group> -m APIM" \
        ex_authorize="  ADDCLIENT : $script_name -a <entra-app-name> -z <client-app-id-guid>" \
        ex_help="Run '$script_name -h' for the full option reference."

  local desc_args=("$ex_desc1" "$ex_desc2" "$ex_desc3" "$ex_desc4" "$ex_desc5" "$ex_blank")

  [[ -z "$ENTRA_APP_NAME" ]] && die "Missing required argument: -a <entra-app-name>" \
    "${desc_args[@]}" "$ex_header" "$ex_aca" "$ex_apimmi" "$ex_authorize" "$ex_help"

  case "$MODE" in
    ACA|APIM)
      [[ -z "$CONTAINER_APP_NAME" ]] && die "Missing required argument: -n <container-app-name> (mode=$MODE)" \
        "${desc_args[@]}" "$ex_header" "$ex_aca" "$ex_apimmi" "$ex_help"
      [[ -z "$RG" ]] && die "Missing required argument: -g <resource-group> (mode=$MODE)" \
        "${desc_args[@]}" "$ex_header" "$ex_aca" "$ex_apimmi" "$ex_help"
      ;;
    ADDCLIENT)
      [[ -z "$EXTRA_CLIENT_ID" ]] && die "Mode 'ADDCLIENT' requires -z <client-id>" \
        "${desc_args[@]}" "$ex_header" "$ex_authorize" "$ex_help"
      if [[ ! "$EXTRA_CLIENT_ID" =~ ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$ ]]; then
        die "--authorize value is not a GUID: '$EXTRA_CLIENT_ID'" \
            "Pass the client's appId (a GUID), not its display name." \
            "$ex_authorize"
      fi
      ;;
    *) die "Invalid mode: '$MODE'" "Valid values: ACA, APIM, ADDCLIENT" ;;
  esac
  log "Mode: $MODE"
}

# ============================================================================
# Environment / context
# ============================================================================

check_prerequisites() {
  command -v az >/dev/null || die \
    "Azure CLI ('az') is not installed or not on PATH." \
    "Install from https://learn.microsoft.com/cli/azure/install-azure-cli"

  command -v jq >/dev/null || die \
    "'jq' is required to manipulate app-registration JSON but was not found." \
    "Install with: sudo apt-get install jq   (or: brew install jq)"

  command -v uuidgen >/dev/null || die \
    "'uuidgen' is required to generate scope IDs but was not found." \
    "Install the 'uuid-runtime' package (Debian/Ubuntu) or use a host that ships it."
}

require_logged_in() {
  if ! az account show >/dev/null 2>&1; then
    die "Not logged in to Azure (or token expired)." \
        "Run: az login" \
        "If you have multiple subscriptions: az account set --subscription <id-or-name>"
  fi
  CURRENT_SUB="$(az account show --query name -o tsv | tr -d '\r\n')"
  TENANT_ID="$(az account show --query tenantId -o tsv | tr -d '\r\n')"
  [[ -z "$TENANT_ID" ]] && die \
    "Could not read tenantId from the current az context." \
    "Try: az logout && az login"
  log "Active subscription: $CURRENT_SUB"
}

require_resource_group() {
  [[ -z "$RG" ]] && return  # ADDCLIENT mode does not need a resource group
  if ! az group show --name "$RG" >/dev/null 2>&1; then
    die "Resource group '$RG' not found in subscription '$CURRENT_SUB'." \
        "List available groups: az group list --query \"[].name\" -o tsv" \
        "Switch subscription:   az account set --subscription <id-or-name>"
  fi
}

# Sets APP_FQDN, HEALTH_URL, MI_PRINCIPAL_ID, MI_TYPE.
discover_container_app() {
  if [[ "$MODE" == "ADDCLIENT" ]]; then
    log "Skipping Container App discovery (mode=ADDCLIENT)"
    return
  fi
  log "Looking up Container App '$CONTAINER_APP_NAME'..."
  local stderr_file ca_json fqdn user_assigned_first
  stderr_file="$(mktemp)"
  ca_json="$(az containerapp show \
    --name "$CONTAINER_APP_NAME" \
    --resource-group "$RG" \
    -o json 2>"$stderr_file" || true)"

  if [[ -z "$ca_json" || "$ca_json" == "null" ]]; then
    local ca_err; ca_err="$(cat "$stderr_file")"; rm -f "$stderr_file"
    die "Container App '$CONTAINER_APP_NAME' does not exist in resource group '$RG'." \
        "List Container Apps in this group: az containerapp list -g '$RG' --query \"[].name\" -o tsv" \
        "Azure error: $ca_err"
  fi
  rm -f "$stderr_file"

  fqdn="$(echo "$ca_json"               | jq -r '.properties.configuration.ingress.fqdn // empty')"
  MI_PRINCIPAL_ID="$(echo "$ca_json"    | jq -r '.identity.principalId // empty')"
  MI_TYPE="$(echo "$ca_json"            | jq -r '.identity.type // "None"')"
  user_assigned_first="$(echo "$ca_json" | jq -r '(.identity.userAssignedIdentities // {}) | to_entries | .[0].value.principalId // empty')"

  if [[ -n "$fqdn" ]]; then
    APP_FQDN="https://$fqdn"
    HEALTH_URL="$APP_FQDN/health"
    ok "Container App FQDN: $APP_FQDN"
  elif [[ "$MODE" == "ACA" ]]; then
    die "Container App '$CONTAINER_APP_NAME' has no ingress FQDN configured." \
        "EasyAuth requires ingress. Enable it and re-run, e.g.:" \
        "  az containerapp ingress enable -n '$CONTAINER_APP_NAME' -g '$RG' --type external --target-port <port>"
  else
    warn "Container App has no ingress FQDN (OK for APIM mode; proxy is callee-only)."
  fi

  # In APIM mode, prefer system-assigned identity; fall back to first user-assigned.
  if [[ "$MODE" == "APIM" ]]; then
    [[ -z "$MI_PRINCIPAL_ID" ]] && MI_PRINCIPAL_ID="$user_assigned_first"
    if [[ -z "$MI_PRINCIPAL_ID" ]]; then
      die "Container App '$CONTAINER_APP_NAME' has no managed identity (identity.type='$MI_TYPE')." \
          "Enable a managed identity and re-run, e.g.:" \
          "  az containerapp identity assign -n '$CONTAINER_APP_NAME' -g '$RG' --system-assigned"
    fi
    ok "Proxy managed identity principalId: $MI_PRINCIPAL_ID (identity.type=$MI_TYPE)"
  fi
}

# ============================================================================
# Entra app registration (shared by both modes)
# ============================================================================

# Sets APP_ID. Creates the app registration if it does not exist.
ensure_app_registration() {
  log "Looking up Entra app registration '$ENTRA_APP_NAME'..."

  local match_count
  match_count="$(az ad app list --display-name "$ENTRA_APP_NAME" --query "length(@)" -o tsv 2>/dev/null | tr -d '\r\n' || echo 0)"
  if [[ "$match_count" -gt 1 ]]; then
    die "Multiple ($match_count) Entra app registrations are named '$ENTRA_APP_NAME'." \
        "This script cannot safely pick one. Resolve the ambiguity in the portal" \
        "(Entra ID \u2192 App registrations) or pass a different -a value."
  fi

  APP_ID="$(az ad app list --display-name "$ENTRA_APP_NAME" --query "[0].appId" -o tsv 2>/dev/null | tr -d '\r\n' || true)"
  if [[ -n "$APP_ID" ]]; then
    ok "Reusing existing app registration. APP_ID=$APP_ID"
    return
  fi

  log "Creating Entra app registration '$ENTRA_APP_NAME'..."
  local stderr_file err_msg
  stderr_file="$(mktemp)"
  APP_ID="$(az ad app create \
    --display-name "$ENTRA_APP_NAME" \
    --sign-in-audience AzureADMyOrg \
    --query appId -o tsv 2>"$stderr_file" | tr -d '\r\n' || true)"
  if [[ -z "$APP_ID" ]]; then
    err_msg="$(cat "$stderr_file")"; rm -f "$stderr_file"
    die "Failed to create Entra app registration '$ENTRA_APP_NAME'." \
        "You likely lack 'Application.ReadWrite.All' or equivalent in this tenant." \
        "Ask a tenant admin to either create the app or grant you the role." \
        "Azure error: $err_msg"
  fi
  rm -f "$stderr_file"
  ok "Created app registration. APP_ID=$APP_ID"
}

ensure_identifier_uri() {
  local expected="api://$APP_ID"
  local current
  current="$(az ad app show --id "$APP_ID" --query "identifierUris" -o json)"
  if echo "$current" | jq -e --arg u "$expected" 'index($u)' >/dev/null; then
    ok "Identifier URI already set: $expected"
    return
  fi
  log "Setting identifier URI: $expected"
  run_az "Setting identifier URI on $APP_ID" \
    az ad app update --id "$APP_ID" --identifier-uris "$expected"
  ok "Identifier URI set"
}

# Sets SP_OID. Creates the service principal if missing.
ensure_service_principal() {
  SP_OID="$(az ad sp list --filter "appId eq '$APP_ID'" --query "[0].id" -o tsv 2>/dev/null | tr -d '\r\n' || true)"
  if [[ -n "$SP_OID" ]]; then
    ok "Service principal already exists ($SP_OID)"
    return
  fi
  log "Creating service principal for $APP_ID..."
  run_az "Creating service principal for $APP_ID" \
    az ad sp create --id "$APP_ID" --output none
  SP_OID="$(az ad sp list --filter "appId eq '$APP_ID'" --query "[0].id" -o tsv 2>/dev/null | tr -d '\r\n' || true)"
  ok "Service principal created ($SP_OID)"
}

# ============================================================================
# ACA mode steps
# ============================================================================

ensure_delegated_scope() {
  local api_obj has_scope scope_id updated
  api_obj="$(az ad app show --id "$APP_ID" --query api -o json)"
  has_scope="$(echo "$api_obj" | jq -r '[.oauth2PermissionScopes[]? | select(.value == "api.access")] | length')"
  if [[ "$has_scope" != "0" ]]; then
    ok "Scope 'api.access' already exists"
    return
  fi

  log "Adding 'api.access' delegated scope..."
  scope_id="$(uuidgen | tr '[:upper:]' '[:lower:]')"
  updated="$(echo "$api_obj" | jq --arg id "$scope_id" '.oauth2PermissionScopes = [{
    adminConsentDescription: "Access the API",
    adminConsentDisplayName: "Admin Access",
    id: $id,
    isEnabled: true,
    type: "Admin",
    userConsentDescription: "Access the API",
    userConsentDisplayName: "User Access",
    value: "api.access"
  }]')"
  run_az "Adding 'api.access' scope to $APP_ID" \
    az ad app update --id "$APP_ID" --set api="$updated"
  ok "Scope 'api.access' added"
}

# Ensure the app registration issues v2.0 access tokens.
#
# Without requestedAccessTokenVersion=2, Entra issues v1.0 tokens when a caller
# requests them via `--resource api://<guid>`, with aud="api://<guid>". The ACA
# EasyAuth config we set uses the bare GUID as allowedAudiences, which only
# matches v2.0 tokens (aud=<guid>). Setting v2 here aligns the two so a token
# acquired via either `--resource api://<guid>` or `--scope api://<guid>/.default`
# is accepted by EasyAuth.
ensure_v2_access_tokens() {
  local current api_obj updated
  current="$(az ad app show --id "$APP_ID" --query "api.requestedAccessTokenVersion" -o tsv 2>/dev/null || echo "")"
  if [[ "$current" == "2" ]]; then
    ok "App registration already issues v2.0 access tokens"
    return
  fi

  log "Setting requestedAccessTokenVersion=2 on app registration..."
  api_obj="$(az ad app show --id "$APP_ID" --query api -o json)"
  updated="$(echo "$api_obj" | jq '.requestedAccessTokenVersion = 2')"
  run_az "Setting requestedAccessTokenVersion=2 on $APP_ID" \
    az ad app update --id "$APP_ID" --set api="$updated"
  ok "App registration set to issue v2.0 access tokens (aud will be bare GUID)"
}

# Pre-authorize a client app ID on the API's 'api.access' delegated scope.
# Used by ACA mode (for Azure CLI) and by ADDCLIENT mode (for arbitrary clients).
preauthorize_client() {
  local client_id="$1"
  local client_label="${2:-$client_id}"
  local api_obj scope_id existing updated
  api_obj="$(az ad app show --id "$APP_ID" --query api -o json)"
  scope_id="$(echo "$api_obj" | jq -r '[.oauth2PermissionScopes[]? | select(.value == "api.access")][0].id')"
  if [[ -z "$scope_id" || "$scope_id" == "null" ]]; then
    die "Cannot pre-authorize $client_label: 'api.access' scope not found on $APP_ID" \
        "Re-run ensure_delegated_scope, then retry"
  fi
  existing="$(echo "$api_obj" | jq -r --arg cli "$client_id" --arg sid "$scope_id" \
    '[.preAuthorizedApplications[]? | select(.appId == $cli) | .delegatedPermissionIds[]? | select(. == $sid)] | length')"
  if [[ "$existing" != "0" ]]; then
    ok "$client_label already pre-authorized for 'api.access'"
    return
  fi
  log "Pre-authorizing $client_label for 'api.access' scope..."
  updated="$(echo "$api_obj" | jq --arg cli "$client_id" --arg sid "$scope_id" '
    .preAuthorizedApplications = ((.preAuthorizedApplications // []) | map(select(.appId != $cli))) + [{
      appId: $cli,
      delegatedPermissionIds: [$sid]
    }]')"
  run_az "Pre-authorizing $client_label on $APP_ID" \
    az ad app update --id "$APP_ID" --set api="$updated"
  ok "$client_label pre-authorized for 'api.access'"
}

# Well-known Azure CLI public client ID. Used both as a pre-authorized client
# on the app registration (so `az account get-access-token` works without
# interactive consent) and as an allowed calling application on EasyAuth (so
# the bearer token's azp claim is accepted at authorization time).
AZURE_CLI_CLIENT_ID="04b07795-8ddb-461a-bbee-02f9e1bf7b46"

ensure_azure_cli_preauthorized() {
  # Pre-authorize Azure CLI on the api.access scope so token acquisition does
  # not trigger interactive consent (which fails with AADSTS650057 on a fresh
  # app reg).
  preauthorize_client "$AZURE_CLI_CLIENT_ID" "Azure CLI"
}

authorize_extra_client() {
  preauthorize_client "$EXTRA_CLIENT_ID" "client $EXTRA_CLIENT_ID"
}

ensure_id_token_issuance() {
  local enabled
  enabled="$(az ad app show --id "$APP_ID" --query "web.implicitGrantSettings.enableIdTokenIssuance" -o tsv | tr -d '\r\n')"
  if [[ "$enabled" == "true" ]]; then
    ok "ID token issuance already enabled"
    return
  fi
  log "Enabling ID token issuance..."
  run_az "Enabling ID token issuance on $APP_ID" \
    az ad app update --id "$APP_ID" --enable-id-token-issuance true
  ok "ID token issuance enabled"
}

# Sets CLIENT_SECRET. Intentionally NOT idempotent — credential reset --append
# always creates a new secret value; existing secrets are preserved.
create_client_secret() {
  log "Creating a new client secret (valid 30 days)..."
  local stderr_file err_msg
  stderr_file="$(mktemp)"
  CLIENT_SECRET="$(az ad app credential reset \
    --id "$APP_ID" \
    --display-name "proxy-auth-secret" \
    --append \
    --end-date "$(date -d '+30 days' '+%Y-%m-%d')" \
    --query password -o tsv 2>"$stderr_file" | tr -d '\r\n' || true)"
  if [[ -z "$CLIENT_SECRET" ]]; then
    err_msg="$(cat "$stderr_file")"; rm -f "$stderr_file"
    die "Failed to create client secret for $APP_ID." \
        "Common causes: insufficient permissions on the app, or the tenant blocks secret creation." \
        "Azure error: $err_msg"
  fi
  rm -f "$stderr_file"
  ok "Client secret created (held in memory only)"
}

# Step 3 in the POC doc: register Microsoft provider with bare-GUID audience.
configure_aca_provider() {
  log "Enabling EasyAuth (Microsoft provider) on Container App..."
  run_az "Enabling EasyAuth on Container App '$CONTAINER_APP_NAME'" \
    az containerapp auth microsoft update \
      --name "$CONTAINER_APP_NAME" \
      --resource-group "$RG" \
      --client-id "$APP_ID" \
      --client-secret "$CLIENT_SECRET" \
      --tenant-id "$TENANT_ID" \
      --allowed-audiences "$APP_ID" \
      --yes --output none
  ok "EasyAuth Microsoft provider configured (audience=$APP_ID)"
}

# After EasyAuth validates the bearer token, it enforces an authorization
# policy: by default only the registered clientId itself is an allowed
# 'calling application' (azp claim). Tokens minted by other clients (e.g. the
# Azure CLI, or an arbitrary client added via ADDCLIENT mode) are rejected
# with HTTP 403 / SubStatusCode 76 even though authentication succeeded,
# unless their app ID is on this list.
#
# Idempotent merge: reads the existing list, appends the given client ID if
# absent, dedupes, and writes back. Existing entries are preserved across
# repeated invocations (e.g. ACA mode adds the CLI, ADDCLIENT mode then adds
# another client without erasing the CLI).
#
# Args: <client-id> [<label>]
configure_allowed_applications() {
  local new_client_id="$1"
  local label="${2:-$new_client_id}"
  local current_json set_value

  current_json="$(az containerapp auth show \
    --name "$CONTAINER_APP_NAME" \
    --resource-group "$RG" \
    --query "identityProviders.azureActiveDirectory.validation.defaultAuthorizationPolicy.allowedApplications" \
    -o json 2>/dev/null || echo '[]')"
  [[ -z "$current_json" || "$current_json" == "null" ]] && current_json='[]'

  if echo "$current_json" | jq -e --arg c "$new_client_id" 'index($c)' >/dev/null; then
    ok "$label already in EasyAuth allowedApplications"
    return
  fi

  log "Adding $label to EasyAuth allowedApplications..."
  # az --set parses the value as a bare comma-separated list inside [...].
  # Build [id1,id2,...] from the merged + deduped JSON array.
  set_value="$(echo "$current_json" | jq -r --arg c "$new_client_id" \
    '(. + [$c]) | unique | "[" + join(",") + "]"')"
  run_az "Setting allowedApplications on '$CONTAINER_APP_NAME'" \
    az containerapp auth update \
      --name "$CONTAINER_APP_NAME" \
      --resource-group "$RG" \
      --set "identityProviders.azureActiveDirectory.validation.defaultAuthorizationPolicy.allowedApplications=$set_value" \
      --output none
  ok "allowedApplications now: $set_value"
}

# Step 4 in the POC doc: flip platform on and require auth.
enable_auth_platform() {
  log "Enabling auth platform and setting unauthenticated action to Return401..."
  run_az "Setting platform.enabled=true on '$CONTAINER_APP_NAME'" \
    az containerapp auth update \
      --name "$CONTAINER_APP_NAME" \
      --resource-group "$RG" \
      --enabled true \
      --unauthenticated-client-action Return401 \
      --output none
  ok "Auth platform enabled (Return401 on unauthenticated requests)"
}

# Reads the live config back via 'az containerapp auth show' and asserts each
# expected field. Aggregates failures so the user sees all problems at once.
verify_aca() {
  log "Verifying EasyAuth configuration..."

  local stderr_file auth_json err_msg
  stderr_file="$(mktemp)"
  auth_json="$(az containerapp auth show \
    --name "$CONTAINER_APP_NAME" \
    --resource-group "$RG" \
    -o json 2>"$stderr_file" || true)"

  if [[ -z "$auth_json" || "$auth_json" == "null" ]]; then
    err_msg="$(cat "$stderr_file")"; rm -f "$stderr_file"
    die "Could not read EasyAuth configuration after update." \
        "Try: az containerapp auth show -n '$CONTAINER_APP_NAME' -g '$RG'" \
        "Azure error: $err_msg"
  fi
  rm -f "$stderr_file"

  local enabled unauth_action aad_client_id audiences_json audience audience_count
  local allowed_apps_json allowed_apps_has_cli
  enabled="$(echo "$auth_json"          | jq -r '.platform.enabled // empty')"
  unauth_action="$(echo "$auth_json"    | jq -r '.globalValidation.unauthenticatedClientAction // empty')"
  aad_client_id="$(echo "$auth_json"    | jq -r '.identityProviders.azureActiveDirectory.registration.clientId // empty')"
  audiences_json="$(echo "$auth_json"   | jq -c '.identityProviders.azureActiveDirectory.validation.allowedAudiences // []')"
  audience="$(echo "$audiences_json"    | jq -r '.[0] // empty')"
  audience_count="$(echo "$audiences_json" | jq -r 'length')"
  allowed_apps_json="$(echo "$auth_json" | jq -c '.identityProviders.azureActiveDirectory.validation.defaultAuthorizationPolicy.allowedApplications // []')"
  allowed_apps_has_cli="$(echo "$allowed_apps_json" | jq -r --arg cli "$AZURE_CLI_CLIENT_ID" 'index($cli) // ""')"

  echo "  platform.enabled                     = $enabled"
  echo "  globalValidation.unauthenticated     = $unauth_action"
  echo "  aad.registration.clientId            = $aad_client_id"
  echo "  aad.validation.allowedAudiences      = $audiences_json"
  echo "  aad.validation.allowedApplications   = $allowed_apps_json"

  local fail=0
  if [[ "$enabled" != "true" ]]; then
    err "EasyAuth platform is not enabled (platform.enabled='$enabled')."
    printf '\033[0;31m       \u2192 %s\033[0m\n' \
      "Enable it: az containerapp auth update -n '$CONTAINER_APP_NAME' -g '$RG' --enabled true" >&2
    fail=1
  fi
  if [[ "$unauth_action" != "Return401" ]]; then
    err "Unauthenticated client action is '$unauth_action', expected 'Return401'."
    printf '\033[0;31m       \u2192 %s\033[0m\n' \
      "Fix: az containerapp auth update -n '$CONTAINER_APP_NAME' -g '$RG' --unauthenticated-client-action Return401" >&2
    fail=1
  fi
  if [[ "$aad_client_id" != "$APP_ID" ]]; then
    err "EasyAuth clientId mismatch: expected '$APP_ID', got '$aad_client_id'."
    printf '\033[0;31m       \u2192 %s\033[0m\n' \
      "Re-run the Microsoft provider update step (or inspect via 'az containerapp auth show')." >&2
    fail=1
  fi
  if [[ "$audience_count" -eq 0 ]]; then
    err "No allowedAudiences entries are configured on the AAD provider."
    printf '\033[0;31m       \u2192 %s\033[0m\n' \
      "Set the bare GUID audience (v2.0 tokens):" \
      "  az containerapp auth microsoft update -n '$CONTAINER_APP_NAME' -g '$RG' --allowed-audiences '$APP_ID'" >&2
    fail=1
  elif [[ "$audience" != "$APP_ID" ]]; then
    err "allowedAudiences[0]='$audience', expected the bare GUID '$APP_ID'."
    printf '\033[0;31m       \u2192 %s\033[0m\n' \
      "v2.0 tokens carry the bare GUID in 'aud'; api://<guid> form will be rejected." \
      "Fix: az containerapp auth microsoft update -n '$CONTAINER_APP_NAME' -g '$RG' --allowed-audiences '$APP_ID'" >&2
    fail=1
  fi
  if [[ -z "$allowed_apps_has_cli" || "$allowed_apps_has_cli" == "null" ]]; then
    err "defaultAuthorizationPolicy.allowedApplications is missing the Azure CLI ($AZURE_CLI_CLIENT_ID)."
    printf '\033[0;31m       \u2192 %s\033[0m\n' \
      "Without this, tokens minted by 'az account get-access-token' are rejected with HTTP 403 (SubStatusCode 76)" \
      "  because the token's azp claim does not match the registered clientId." \
      "Fix: az containerapp auth update -n '$CONTAINER_APP_NAME' -g '$RG' \\" \
      "       --set identityProviders.azureActiveDirectory.validation.defaultAuthorizationPolicy.allowedApplications=\"[$AZURE_CLI_CLIENT_ID]\"" >&2
    fail=1
  fi
  [[ "$fail" -eq 1 ]] && die "EasyAuth verification failed (see errors above)."
  ok "EasyAuth verification passed"
}

# ============================================================================
# APIM mode steps
# ============================================================================

# Sets APP_ROLE_ID. Creates the app role if missing.
ensure_app_role() {
  local app_full updated
  app_full="$(az ad app show --id "$APP_ID" -o json)"
  APP_ROLE_ID="$(echo "$app_full" | jq -r --arg v "$APP_ROLE_VALUE" \
    '[.appRoles[]? | select(.value == $v)] | .[0].id // empty')"

  if [[ -n "$APP_ROLE_ID" ]]; then
    ok "App role '$APP_ROLE_VALUE' already exists (id=$APP_ROLE_ID)"
    return
  fi

  log "Adding app role '$APP_ROLE_VALUE'..."
  APP_ROLE_ID="$(uuidgen | tr '[:upper:]' '[:lower:]')"
  updated="$(echo "$app_full" | jq --arg id "$APP_ROLE_ID" \
                                    --arg v "$APP_ROLE_VALUE" \
                                    --arg d "$APP_ROLE_DISPLAY" \
                                    --arg desc "$APP_ROLE_DESC" \
    '(.appRoles // []) + [{
      id: $id,
      allowedMemberTypes: ["Application"],
      description: $desc,
      displayName: $d,
      isEnabled: true,
      value: $v
    }]')"
  run_az "Adding app role '$APP_ROLE_VALUE' to $APP_ID" \
    az ad app update --id "$APP_ID" --set appRoles="$updated"
  ok "App role '$APP_ROLE_VALUE' added (id=$APP_ROLE_ID)"
}

# Require explicit app-role assignment before tokens carry the `roles` claim.
# Without this, the tenant default may issue tokens without `roles`, and APIM's
# <required-claims> check will reject them with 401.
ensure_app_role_assignment_required() {
  local current
  current="$(az ad sp show --id "$APP_ID" --query "appRoleAssignmentRequired" -o tsv 2>/dev/null | tr -d '\r\n')"
  if [[ "$current" == "true" ]]; then
    ok "appRoleAssignmentRequired already true on SP for $APP_ID"
    return
  fi
  log "Setting appRoleAssignmentRequired=true on service principal..."
  run_az "Enforcing app-role assignment on $APP_ID" \
    az ad sp update --id "$APP_ID" --set appRoleAssignmentRequired=true
  ok "appRoleAssignmentRequired=true"
}

# Idempotent appRoleAssignment via Microsoft Graph.
assign_role_to_managed_identity() {
  API_SP_OID="$SP_OID"
  [[ -z "$API_SP_OID" ]] && die \
    "Could not resolve service principal objectId for $APP_ID." \
    "ensure_service_principal must run before this step."

  log "Checking existing role assignments on managed identity $MI_PRINCIPAL_ID..."
  local existing match_id body stderr_file err_msg
  existing="$(az rest --method GET \
    --url "https://graph.microsoft.com/v1.0/servicePrincipals/$MI_PRINCIPAL_ID/appRoleAssignments" \
    -o json 2>/dev/null || echo '{"value":[]}')"

  match_id="$(echo "$existing" | jq -r --arg rid "$API_SP_OID" --arg roleId "$APP_ROLE_ID" \
    '[.value[]? | select(.resourceId == $rid and .appRoleId == $roleId)] | .[0].id // empty')"

  if [[ -n "$match_id" ]]; then
    ok "Managed identity already has '$APP_ROLE_VALUE' role (assignment id=$match_id)"
    return
  fi

  log "Granting '$APP_ROLE_VALUE' to managed identity..."
  body="$(jq -nc --arg p "$MI_PRINCIPAL_ID" --arg r "$API_SP_OID" --arg roleId "$APP_ROLE_ID" \
    '{principalId:$p, resourceId:$r, appRoleId:$roleId}')"
  stderr_file="$(mktemp)"
  if ! az rest --method POST \
      --url "https://graph.microsoft.com/v1.0/servicePrincipals/$MI_PRINCIPAL_ID/appRoleAssignments" \
      --headers "Content-Type=application/json" \
      --body "$body" \
      -o none 2>"$stderr_file"; then
    err_msg="$(cat "$stderr_file")"; rm -f "$stderr_file"
    die "Failed to assign '$APP_ROLE_VALUE' to managed identity $MI_PRINCIPAL_ID." \
        "You likely need 'AppRoleAssignment.ReadWrite.All' or a Privileged Role Administrator role." \
        "Azure error: $err_msg"
  fi
  rm -f "$stderr_file"
  ok "Role '$APP_ROLE_VALUE' granted to managed identity"
}

# ============================================================================
# Summary
# ============================================================================

print_summary() {
  log "Verifying app registration state..."
  if [[ "$MODE" == "ACA" ]]; then
    az ad app show --id "$APP_ID" \
      --query "{appId:appId,identifierUris:identifierUris,scopes:api.oauth2PermissionScopes[].value}" \
      -o table
  elif [[ "$MODE" == "ADDCLIENT" ]]; then
    az ad app show --id "$APP_ID" \
      --query "{appId:appId,identifierUris:identifierUris,preAuthorized:api.preAuthorizedApplications[].appId}" \
      -o table
  else
    az ad app show --id "$APP_ID" \
      --query "{appId:appId,identifierUris:identifierUris,appRoles:appRoles[].value}" \
      -o table
  fi

  echo
  echo "------------------------------------------------------------"
  case "$MODE" in
    ACA)  echo "EasyAuth configured for Container App '$CONTAINER_APP_NAME'" ;;
    APIM)   echo "App role configured; ACA managed identity assigned" ;;
    ADDCLIENT) echo "Client '$EXTRA_CLIENT_ID' pre-authorized on app '$ENTRA_APP_NAME'" ;;
  esac
  echo "------------------------------------------------------------"
  echo "  MODE        = $MODE"
  echo "  APP_ID      = $APP_ID"
  echo "  TENANT_ID   = $TENANT_ID"
  [[ -n "$HEALTH_URL" ]] && echo "  HEALTH_URL  = $HEALTH_URL"
  if [[ "$MODE" == "APIM" ]]; then
    echo "  MI_PRINCIPAL_ID = $MI_PRINCIPAL_ID"
    echo "  APP_ROLE        = $APP_ROLE_VALUE"
    echo "  AUDIENCE        = api://$APP_ID    (token aud claim will be the bare GUID for v2.0)"
  fi
  if [[ "$MODE" == "ADDCLIENT" ]]; then
    echo "  AUTHORIZED_CLIENT = $EXTRA_CLIENT_ID"
    echo "  SCOPE             = api.access"
  fi
  echo
  echo "Export these for verification:"
  echo
  echo "  export APP_ID=\"$APP_ID\""
  echo "  export TENANT_ID=\"$TENANT_ID\""
  [[ -n "$HEALTH_URL" ]] && echo "  export HEALTH_URL=\"$HEALTH_URL\""
  [[ -n "$CONTAINER_APP_NAME" ]] && echo "  export CONTAINER_APP_NAME=\"$CONTAINER_APP_NAME\""
  [[ -n "$RG" ]]                 && echo "  export RG=\"$RG\""
  if [[ "$MODE" == "APIM" ]]; then
    echo
    echo "Next: configure APIM policy to validate-jwt against:"
    echo "  issuer:   https://login.microsoftonline.com/$TENANT_ID/v2.0"
    echo "  audience: $APP_ID"
    echo "  required role claim: $APP_ROLE_VALUE"
    echo "Proxy should request a token for audience: api://$APP_ID"
    echo
    echo "Paste this <inbound> policy in APIM (All operations -> Inbound processing):"
    echo
    cat <<EOF
  <inbound>
    <base />
    <validate-jwt
      header-name="Authorization"
      failed-validation-httpcode="401"
      failed-validation-error-message="Unauthorized: invalid or missing token">
      <openid-config url="https://login.microsoftonline.com/$TENANT_ID/v2.0/.well-known/openid-configuration" />
      <audiences>
        <audience>api://$APP_ID</audience>
        <audience>$APP_ID</audience>
      </audiences>
      <issuers>
        <issuer>https://login.microsoftonline.com/$TENANT_ID/v2.0</issuer>
      </issuers>
      <required-claims>
        <claim name="roles" match="any">
          <value>$APP_ROLE_VALUE</value>
        </claim>
      </required-claims>
    </validate-jwt>
  </inbound>
EOF
  elif [[ "$MODE" == "ADDCLIENT" ]]; then
    echo
    echo "Client $EXTRA_CLIENT_ID can now request tokens with:"
    echo "  az account get-access-token --resource api://$APP_ID"
  else
    echo
    echo "The client secret has been written to EasyAuth and is not echoed here."
  fi
}

# ============================================================================
# Orchestration
# ============================================================================

main() {
  parse_args "$@"
  check_prerequisites
  require_logged_in
  require_resource_group
  discover_container_app

  # Shared app-registration steps
  ensure_app_registration
  ensure_identifier_uri
  ensure_service_principal

  case "$MODE" in
    ACA)
      ensure_delegated_scope
      ensure_v2_access_tokens
      ensure_azure_cli_preauthorized
      ensure_id_token_issuance
      create_client_secret
      configure_aca_provider
      configure_allowed_applications "$AZURE_CLI_CLIENT_ID" "Azure CLI"
      enable_auth_platform
      verify_aca
      ;;
    APIM)
      ensure_v2_access_tokens
      ensure_app_role
      ensure_app_role_assignment_required
      assign_role_to_managed_identity
      ;;
    ADDCLIENT)
      ensure_delegated_scope
      ensure_v2_access_tokens
      authorize_extra_client
      if [[ -n "$CONTAINER_APP_NAME" && -n "$RG" ]]; then
        configure_allowed_applications "$EXTRA_CLIENT_ID" "client $EXTRA_CLIENT_ID"
      else
        warn "No -n/-g provided; skipping EasyAuth allowedApplications update."
        warn "Client $EXTRA_CLIENT_ID is pre-authorized on the app reg but will receive HTTP 403 from the proxy"
        warn "until it is also added to the Container App's allowedApplications."
        warn "Re-run with: -m ADDCLIENT -a $ENTRA_APP_NAME -z $EXTRA_CLIENT_ID -n <container-app> -g <resource-group>"
      fi
      ;;
  esac

  print_summary
}

main "$@"
