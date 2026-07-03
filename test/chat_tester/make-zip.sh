#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_FILE="$SCRIPT_DIR/chat_tester.csproj"
ARTIFACTS_DIR="$SCRIPT_DIR/artifacts"
PUBLISH_DIR="$ARTIFACTS_DIR/publish"
ZIP_FILE="$ARTIFACTS_DIR/chat-tester-appservice.zip"
CONFIGURATION="${CONFIGURATION:-Release}"

usage() {
    cat <<USAGE
Usage: ./make-zip.sh [output-zip]

Creates an Azure App Service deployment package for chat-tester.

Environment variables:
  CONFIGURATION   Build configuration to publish. Default: Release

Examples:
  ./make-zip.sh
  ./make-zip.sh ./artifacts/chat-tester.zip
  CONFIGURATION=Debug ./make-zip.sh
USAGE
}

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
    usage
    exit 0
fi

if [[ $# -gt 1 ]]; then
    usage >&2
    exit 2
fi

if [[ $# -eq 1 ]]; then
    ZIP_FILE="$1"
    if [[ "$ZIP_FILE" != /* ]]; then
        ZIP_FILE="$PWD/$ZIP_FILE"
    fi
fi

if ! command -v dotnet >/dev/null 2>&1; then
    echo "dotnet was not found on PATH." >&2
    exit 1
fi

if ! command -v zip >/dev/null 2>&1; then
    echo "zip was not found on PATH. Install zip and run this script again." >&2
    exit 1
fi

mkdir -p "$ARTIFACTS_DIR"
rm -rf "$PUBLISH_DIR"
rm -f "$ZIP_FILE"
mkdir -p "$PUBLISH_DIR" "$(dirname "$ZIP_FILE")"

echo "Publishing $PROJECT_FILE ($CONFIGURATION)..."
# Remove stale build artifacts so the Razor source generator regenerates
# component classes from scratch (avoids intermittent Components.* namespace errors).
rm -rf "$SCRIPT_DIR/obj" "$SCRIPT_DIR/bin"
dotnet restore "$PROJECT_FILE"
dotnet publish "$PROJECT_FILE" \
    --configuration "$CONFIGURATION" \
    --output "$PUBLISH_DIR" \
    --no-restore \
    -p:UseAppHost=false

echo "Creating Azure App Service zip package..."
(
    cd "$PUBLISH_DIR"
    zip -qr "$ZIP_FILE" .
)

echo "Package created: $ZIP_FILE"