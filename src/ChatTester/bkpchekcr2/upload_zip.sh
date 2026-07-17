#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ZIP_FILE="$SCRIPT_DIR/artifacts/chat-tester-appservice.zip"
WEBAPP_NAME=""
RESOURCE_GROUP=""

usage() {
    cat <<USAGE
Usage: ./upload_zip.sh -n <name> -g <resource-group> [-z zip-file]

Deploys a zip package to an Azure App Service web app.
Build the package first with ./make-zip.sh.

Options:
  -n <name>              Azure App Service web app name
  -g <resource-group>    Resource group containing the web app
  -z <zip-file>          Zip package to deploy. Default: ./artifacts/chat-tester-appservice.zip
  -h                    Show this help

Example:
  ./upload_zip.sh -n my-chat-tester -g my-resource-group
USAGE
}

while getopts ":n:g:z:h" option; do
    case "$option" in
        n) WEBAPP_NAME="$OPTARG" ;;
        g) RESOURCE_GROUP="$OPTARG" ;;
        z) ZIP_FILE="$OPTARG" ;;
        h)
            usage
            exit 0
            ;;
        :)
            echo "Option -$OPTARG requires a value." >&2
            usage >&2
            exit 2
            ;;
        \?)
            echo "Unknown option: -$OPTARG" >&2
            usage >&2
            exit 2
            ;;
    esac
done

shift $((OPTIND - 1))

if [[ $# -ne 0 || -z "$WEBAPP_NAME" || -z "$RESOURCE_GROUP" ]]; then
    usage >&2
    exit 2
fi

if [[ "$ZIP_FILE" != /* ]]; then
    ZIP_FILE="$PWD/$ZIP_FILE"
fi

if [[ ! -f "$ZIP_FILE" ]]; then
    echo "Zip package not found: $ZIP_FILE" >&2
    echo "Run ./make-zip.sh to build the package first." >&2
    exit 1
fi

if ! command -v az >/dev/null 2>&1; then
    echo "Azure CLI 'az' was not found on PATH." >&2
    exit 1
fi

if ! az account show >/dev/null 2>&1; then
    echo "Azure CLI is not signed in. Run 'az login' and try again." >&2
    exit 1
fi

echo "Checking web app '$WEBAPP_NAME' in resource group '$RESOURCE_GROUP'..."
az webapp show \
    --name "$WEBAPP_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    >/dev/null

echo "Deploying $ZIP_FILE to '$WEBAPP_NAME'..."
az webapp deploy \
    --name "$WEBAPP_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --type zip \
    --src-path "$ZIP_FILE" \
    >/dev/null

echo "Deployed '$ZIP_FILE' to '$WEBAPP_NAME'."
