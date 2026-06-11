#!/bin/bash

# Interactive deployment menu for SimpleL7Proxy.
# Mirrors the steps in deployment/README.md.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Load deployment parameters if available
if [[ -f "${SCRIPT_DIR}/deploy.parameters.sh" ]]; then
    # shellcheck source=deploy.parameters.sh
    source "${SCRIPT_DIR}/deploy.parameters.sh"
fi

# Load derived values (computed from parameters)
if [[ -f "${SCRIPT_DIR}/deploy.derived.sh" ]]; then
    # shellcheck source=deploy.derived.sh
    source "${SCRIPT_DIR}/deploy.derived.sh"
fi

GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
BLUE='\033[0;34m'
NC='\033[0m'

run_step() {
    local title="$1"
    shift
    echo ""
    echo -e "${BLUE}========================================${NC}"
    echo -e "${BLUE} ${title}${NC}"
    echo -e "${BLUE}========================================${NC}"
    if "$@"; then
        echo -e "${GREEN}✓ ${title} complete${NC}"
    else
        local rc=$?
        echo -e "${RED}✗ ${title} failed (exit ${rc})${NC}"
    fi
    echo ""
    local reply
    read -r -p "Press Enter to return to the menu, or 'q' to quit: " reply
    case "${reply}" in
        q|Q|quit|exit) echo "Bye."; exit 0 ;;
    esac
}

step1_prereq()        { ( cd "${SCRIPT_DIR}/Prereq"           && ./validate.sh ); }
step2_vnet()          { ( cd "${SCRIPT_DIR}/VNet"             && ./deploy.sh   ); }
step3_acr()           { ( cd "${SCRIPT_DIR}/ContainerImage"   && ./validate-acr.sh ); }
step4_image()         { ( cd "${SCRIPT_DIR}/ContainerImage"   && ./deploy.sh   ); }
step5_aca()           { ( cd "${SCRIPT_DIR}/proxy" && ./deploy.sh ); }
step6_dns()           { ( cd "${SCRIPT_DIR}/DNS"              && ./deploy.sh   ); }
step7_appconfig()     { ( cd "${SCRIPT_DIR}/AppConfiguration" && ./deploy.sh   ); }
step8_blobstorage()   { ( cd "${SCRIPT_DIR}/BlobStorage"      && ./deploy.sh   ); }
step9_requestapi_create() { ( cd "${SCRIPT_DIR}/RequestAPI"     && ./create.sh   ); }
step10_requestapi_deploy() { ( cd "${SCRIPT_DIR}/RequestAPI"     && ./deploy.sh   ); }

# ---------------------------------------------------------------------------
# Step registry — one source of truth for every step.
# Format: "NUM|MENU_LABEL|TITLE|FN_NAME|REQUIRED_VAR"
#   MENU_LABEL  : displayed in the menu (padded for alignment)
#   TITLE       : used in the run_step header
#   FN_NAME     : bash function to call (must be defined above)
#   REQUIRED_VAR: env var that must equal "yes" to enable; empty = always on
#
# To add a step: append a line here. Nothing else needs to change.
# To add a new flag condition: set REQUIRED_VAR to the new variable name.
# ---------------------------------------------------------------------------
STEPS=(
    # NUM | MENU_LABEL                                              | TITLE                         | FN_NAME                  | REQUIRED_VAR
    "1 | Prerequisites              (Prereq/validate.sh)           | Prerequisites                 | step1_prereq             | "
    "2 | Virtual Network            (VNet/deploy.sh)               | Virtual Network               | step2_vnet               | PRIVATE_NETWORK_DEPLOYMENT"
    "3 | Validate/Create ACR        (ContainerImage/validate-acr.sh)| Validate/Create ACR          | step3_acr                | "
    "4 | Build Container Image      (ContainerImage/deploy.sh)     | Build Container Image         | step4_image              | "
    "5 | Azure Container Apps       (proxy/deploy.sh)              | Azure Container Apps          | step5_aca                | "
    "6 | Private DNS                (DNS/deploy.sh)                | Private DNS                   | step6_dns                | PRIVATE_NETWORK_DEPLOYMENT"
    "7 | App Configuration          (AppConfiguration/deploy.sh)   | App Configuration             | step7_appconfig          | "
    "8 | Blob Storage  (optional)   (BlobStorage/deploy.sh)        | Blob Storage                  | step8_blobstorage        | ASYNC_DEPLOYMENT"
    "9 | Create RequestAPI Function (RequestAPI/create.sh)         | Create RequestAPI Function App| step9_requestapi_create  | ASYNC_DEPLOYMENT"
    "10| Deploy/Update RequestAPI   (RequestAPI/deploy.sh)         | Deploy/Update RequestAPI      | step10_requestapi_deploy | ASYNC_DEPLOYMENT"
)

# Returns 0 (enabled) if REQUIRED_VAR is empty or equals "yes"
step_enabled() {
    local req_var="${1// /}"
    [[ -z "${req_var}" ]] && return 0
    [[ "${!req_var:-no}" == "yes" ]] && return 0
    return 1
}

# Parse a STEPS entry into named variables: _num _label _title _fn _req
parse_step() {
    local raw="$1"
    _num="${raw%%|*}";  raw="${raw#*|}"
    _label="${raw%%|*}"; raw="${raw#*|}"
    _title="${raw%%|*}"; raw="${raw#*|}"
    _fn="${raw%%|*}";   raw="${raw#*|}"
    _req="${raw}"
    # trim whitespace
    _num="${_num// /}"; _fn="${_fn// /}"; _req="${_req// /}"
    _title="${_title#"${_title%%[! ]*}"}"; _title="${_title%"${_title##*[! ]}"}"
}

print_menu() {
    clear 2>/dev/null || true
    echo -e "${GREEN}========================================${NC}"
    echo -e "${GREEN} SimpleL7Proxy - Deployment Menu${NC}"
    echo -e "${GREEN}========================================${NC}"

    local entry _num _label _title _fn _req pad
    for entry in "${STEPS[@]}"; do
        parse_step "${entry}"
        pad=""; [[ ${#_num} -eq 1 ]] && pad=" "
        if step_enabled "${_req}"; then
            echo "  ${pad}${_num}) ${_label}"
        else
            echo -e "  ${YELLOW}${pad}${_num}) ${_label} [disabled - ${_req}=no]${NC}"
        fi
    done

    echo "  q) Quit"
    echo ""
}

run_choice() {
    local choice="$1"
    local entry _num _label _title _fn _req
    for entry in "${STEPS[@]}"; do
        parse_step "${entry}"
        if [[ "${_num}" == "${choice}" ]]; then
            if step_enabled "${_req}"; then
                run_step "Step ${_num}: ${_title}" "${_fn}"
            else
                echo -e "${YELLOW}Step ${_num} (${_title}) is disabled because ${_req} is not set to 'yes'.${NC}"
                sleep 2
            fi
            return
        fi
    done
    echo -e "${YELLOW}Invalid option: ${choice}${NC}"; sleep 1
}

while true; do
    print_menu
    read -r -p "Select an option: " choice
    case "${choice}" in
        q|Q) echo "Bye."; exit 0 ;;
        *)   run_choice "${choice}" ;;
    esac
done
