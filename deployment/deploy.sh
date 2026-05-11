#!/bin/bash

# Interactive deployment menu for SimpleL7Proxy.
# Mirrors the steps in deployment/README.md.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

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
step3_image()         { ( cd "${SCRIPT_DIR}/ContainerImage"   && ./deploy.sh   ); }
step4_aca()           { ( cd "${SCRIPT_DIR}/proxy-with-sidecar" && ./deploy.sh ); }
step5_dns()           { ( cd "${SCRIPT_DIR}/DNS"              && ./deploy.sh   ); }
step6_appconfig()     { ( cd "${SCRIPT_DIR}/AppConfiguration" && ./deploy.sh   ); }
step7a_blobstorage()  { ( cd "${SCRIPT_DIR}/BlobStorage"      && ./deploy.sh   ); }
step8_requestapi_create() { ( cd "${SCRIPT_DIR}/RequestAPI"     && ./create.sh   ); }
step9_requestapi_deploy() { ( cd "${SCRIPT_DIR}/RequestAPI"     && ./deploy.sh   ); }

print_menu() {
    clear 2>/dev/null || true
    echo -e "${GREEN}========================================${NC}"
    echo -e "${GREEN} SimpleL7Proxy - Deployment Menu${NC}"
    echo -e "${GREEN}========================================${NC}"
    echo "  1) Prerequisites              (Prereq/validate.sh)"
    echo "  2) Virtual Network            (VNet/deploy.sh)"
    echo "  3) Build Container Image      (ContainerImage/deploy.sh)"
    echo "  4) Azure Container Apps       (proxy-with-sidecar/deploy.sh)"
    echo "  5) Private DNS                (DNS/deploy.sh)"
    echo "  6) App Configuration          (AppConfiguration/deploy.sh)"
    echo "  7) Blob Storage  (optional)   (BlobStorage/deploy.sh)"
    echo "  8) Create RequestAPI Function (RequestAPI/create.sh)"
    echo "  9) Deploy/Update RequestAPI   (RequestAPI/deploy.sh)"
    echo "  q) Quit"
    echo ""
}

while true; do
    print_menu
    read -r -p "Select an option: " choice
    case "${choice}" in
        1)   run_step "Step 1: Prerequisites"           step1_prereq       ;;
        2)   run_step "Step 2: Virtual Network"         step2_vnet         ;;
        3)   run_step "Step 3: Build Container Image"   step3_image        ;;
        4)   run_step "Step 4: Azure Container Apps"    step4_aca          ;;
        5)   run_step "Step 5: Private DNS"             step5_dns          ;;
        6)   run_step "Step 6: App Configuration"       step6_appconfig    ;;
        7)   run_step "Step 7a: Blob Storage"           step7a_blobstorage ;;
        8)   run_step "Step 8: Create RequestAPI Function App" step8_requestapi_create ;;
        9)   run_step "Step 9: Deploy/Update RequestAPI"       step9_requestapi_deploy ;;
        q|Q) echo "Bye."; exit 0 ;;
        *)   echo -e "${YELLOW}Invalid option: ${choice}${NC}"; sleep 1 ;;
    esac
done
