#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SETTINGS_FILE="$SCRIPT_DIR/appsettings.json"
WEBAPP_NAME=""
RESOURCE_GROUP=""

usage() {
    cat <<USAGE
Usage: ./update_settings.sh -n <name> -g <resource-group> [-f appsettings-file]

Updates Azure App Service application settings from appsettings.json.
Nested JSON keys are flattened with '__' so ASP.NET Core can bind them.

Options:
  -n <name>              Azure App Service web app name
  -g <resource-group>    Resource group containing the web app
  -f <appsettings-file>  Settings file to read. Default: ./appsettings.json
  -h                    Show this help

Example:
  ./update_settings.sh -n my-chat-tester -g my-resource-group
USAGE
}

while getopts ":n:g:f:h" option; do
    case "$option" in
        n) WEBAPP_NAME="$OPTARG" ;;
        g) RESOURCE_GROUP="$OPTARG" ;;
        f) SETTINGS_FILE="$OPTARG" ;;
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

if [[ "$SETTINGS_FILE" != /* ]]; then
    SETTINGS_FILE="$PWD/$SETTINGS_FILE"
fi

if [[ ! -f "$SETTINGS_FILE" ]]; then
    echo "Settings file not found: $SETTINGS_FILE" >&2
    exit 1
fi

if ! command -v az >/dev/null 2>&1; then
    echo "Azure CLI 'az' was not found on PATH." >&2
    exit 1
fi

if ! command -v jq >/dev/null 2>&1; then
    echo "jq was not found on PATH. Install jq and run this script again." >&2
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

declare -a SETTINGS=()
while IFS=$'\t' read -r key value; do
    SETTINGS+=("$key=$value")
done < <(
    jq -r '
        paths(scalars) as $path
        | [
            ($path | map(tostring) | join("__")),
            (getpath($path) | if type == "string" then . else tostring end)
          ]
        | @tsv
    ' "$SETTINGS_FILE"
)

if [[ ${#SETTINGS[@]} -eq 0 ]]; then
    echo "No scalar settings found in $SETTINGS_FILE." >&2
    exit 1
fi

echo "Updating ${#SETTINGS[@]} app settings from $SETTINGS_FILE..."
az webapp config appsettings set \
    --name "$WEBAPP_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --settings "${SETTINGS[@]}" \
    >/dev/null

echo "Updated app settings for '$WEBAPP_NAME'."