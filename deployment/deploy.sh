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
step3_acr()           { ( cd "${SCRIPT_DIR}/ContainerImage"   && ./validate-acr.sh ); }
step4_image()         { ( cd "${SCRIPT_DIR}/ContainerImage"   && ./deploy.sh   ); }
step5_aca()           { ( cd "${SCRIPT_DIR}/proxy-with-sidecar" && ./deploy.sh ); }
step6_dns()           { ( cd "${SCRIPT_DIR}/DNS"              && ./deploy.sh   ); }
step7_appconfig()     { ( cd "${SCRIPT_DIR}/AppConfiguration" && ./deploy.sh   ); }
step8_blobstorage()   { ( cd "${SCRIPT_DIR}/BlobStorage"      && ./deploy.sh   ); }
step9_requestapi_create() { ( cd "${SCRIPT_DIR}/RequestAPI"     && ./create.sh   ); }
step10_requestapi_deploy() { ( cd "${SCRIPT_DIR}/RequestAPI"     && ./deploy.sh   ); }

print_menu() {
    clear 2>/dev/null || true
    echo -e "${GREEN}========================================${NC}"
    echo -e "${GREEN} SimpleL7Proxy - Deployment Menu${NC}"
    echo -e "${GREEN}========================================${NC}"
    echo "  1) Prerequisites              (Prereq/validate.sh)"
    echo "  2) Virtual Network            (VNet/deploy.sh)"
    echo "  3) Validate/Create ACR        (ContainerImage/validate-acr.sh)"
    echo "  4) Build Container Image      (ContainerImage/deploy.sh)"
    echo "  5) Azure Container Apps       (proxy-with-sidecar/deploy.sh)"
    echo "  6) Private DNS                (DNS/deploy.sh)"
    echo "  7) App Configuration          (AppConfiguration/deploy.sh)"
    echo "  8) Blob Storage  (optional)   (BlobStorage/deploy.sh)"
    echo "  9) Create RequestAPI Function (RequestAPI/create.sh)"
    echo " 10) Deploy/Update RequestAPI   (RequestAPI/deploy.sh)"
    echo "  q) Quit"
    echo ""
}

while true; do
    print_menu
    read -r -p "Select an option: " choice
    case "${choice}" in
        1)   run_step "Step 1: Prerequisites"           step1_prereq       ;;
        2)   run_step "Step 2: Virtual Network"         step2_vnet         ;;
        3)   run_step "Step 3: Validate/Create ACR"      step3_acr          ;;
        4)   run_step "Step 4: Build Container Image"    step4_image        ;;
        5)   run_step "Step 5: Azure Container Apps"     step5_aca          ;;
        6)   run_step "Step 6: Private DNS"              step6_dns          ;;
        7)   run_step "Step 7: App Configuration"        step7_appconfig    ;;
        8)   run_step "Step 8: Blob Storage"             step8_blobstorage  ;;
        9)   run_step "Step 9: Create RequestAPI Function App" step9_requestapi_create ;;
        10)  run_step "Step 10: Deploy/Update RequestAPI"      step10_requestapi_deploy ;;
        q|Q) echo "Bye."; exit 0 ;;
        *)   echo -e "${YELLOW}Invalid option: ${choice}${NC}"; sleep 1 ;;
    esac
done
