#!/bin/bash

set -e  # Exit on error

# Variables
RESOURCE_GROUP="simplel7-fn"
FUNCTION_APP="simplel7fn"

PROJECT_PATH=$(pwd)  # Get absolute path
PUBLISH_DIR="$PROJECT_PATH/bin/publish"
ZIP_FILE="$PROJECT_PATH/function.zip"

usage() {
    cat <<EOF
Usage: $(basename "$0") [options]

Builds and deploys the Functions project to Azure Flex Consumption.

Options:
  -z            Build the deployment zip only; skip Azure login and deploy.
  -h, --help, -?
                Show this help message and exit.

Variables (edit at the top of the script):
  RESOURCE_GROUP   Azure resource group containing the function app.
  FUNCTION_APP     Name of the target function app.
EOF
}

# Handle long-form help flags before getopts (which only parses short flags).
for arg in "$@"; do
    case "$arg" in
        -h|--help|-\?) usage; exit 0 ;;
    esac
done

# Parse flags
ZIP_ONLY=false
while getopts ":zh" opt; do
    case $opt in
        z) ZIP_ONLY=true ;;
        h) usage; exit 0 ;;
        \?) echo "ERROR: Unknown flag: -$OPTARG" >&2; usage; exit 1 ;;
    esac
done


# Color codes for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Function to log messages
log() {
    local level=$1
    shift
    local message=$@
    timestamp=$(date '+%Y-%m-%d %H:%M:%S')
    case $level in
        "INFO") echo -e "${GREEN}[INFO]${NC} $timestamp - $message" ;;
        "WARN") echo -e "${YELLOW}[WARN]${NC} $timestamp - $message" ;;
        "ERROR") echo -e "${RED}[ERROR]${NC} $timestamp - $message" ;;
    esac
}

# Function to check if a command exists
check_command() {
    if ! command -v $1 &> /dev/null; then
        log "ERROR" "$1 is required but not installed."
        exit 1
    fi
}

# Check required commands
check_command "dotnet"
check_command "az"
check_command "zip"
check_command "unzip"

# Clean previous publish
log "INFO" "Cleaning previous build artifacts..."
rm -rf "$PUBLISH_DIR"
rm -f "$ZIP_FILE"

# Verify Azure CLI login (skip when only building the zip)
if [ "$ZIP_ONLY" = false ]; then
    log "INFO" "Verifying Azure CLI login..."
    if ! az account show &> /dev/null; then
        log "ERROR" "Not logged into Azure CLI. Please run 'az login' first."
        exit 1
    fi
fi

# Create a project.assets.json file to specify the function app runtime version
log "INFO" "Configuring project for Flex Consumption..."
mkdir -p "$PROJECT_PATH/.azure"
cat > "$PROJECT_PATH/.azure/config.json" << EOLINNER
{
  "app": {
    "isFlexConsumption": true,
    "language": "dotnet-isolated",
    "runtime": "dotnet-isolated|9.0"
  }
}
EOLINNER


# Clean and build the main project first
log "INFO" "Building functions project..."
if ! dotnet clean "$PROJECT_PATH/functions.csproj" -c Release; then
    log "ERROR" "Failed to clean functions project"
    exit 1
fi

if ! dotnet build "$PROJECT_PATH/functions.csproj" -c Release; then
    log "ERROR" "Failed to build functions project"
    exit 1
fi

# Check for required build output files
if [ ! -f "$PROJECT_PATH/bin/Release/net9.0/functions.dll" ] || \
   [ ! -f "$PROJECT_PATH/bin/Release/net9.0/functions.deps.json" ] || \
   [ ! -f "$PROJECT_PATH/bin/Release/net9.0/functions.runtimeconfig.json" ]; then
    log "ERROR" "Required build output files are missing"
    exit 1
fi

# Publish the project
log "INFO" "Publishing functions project..."
if ! dotnet publish "$PROJECT_PATH/functions.csproj" -c Release -o "$PUBLISH_DIR" --no-build; then
    log "ERROR" "Failed to publish functions project"
    exit 1
fi

# Verify publish output
log "INFO" "Verifying publish output..."
if [ ! -f "$PUBLISH_DIR/functions.dll" ] || \
   [ ! -f "$PUBLISH_DIR/functions.deps.json" ] || \
   [ ! -f "$PUBLISH_DIR/functions.runtimeconfig.json" ]; then
    log "ERROR" "Required files missing in publish directory"
    exit 1
fi

# Copy configuration files
log "INFO" "Copying configuration files..."
if ! cp "$PROJECT_PATH/host.json" "$PUBLISH_DIR/host.json"; then
    log "ERROR" "Failed to copy host.json"
    exit 1
fi

# Explicitly copy Samples folder (dotnet publish --no-build may not copy None content items)
log "INFO" "Copying Samples folder..."
if [ -d "$PROJECT_PATH/Samples" ]; then
    cp -r "$PROJECT_PATH/Samples" "$PUBLISH_DIR/Samples"
    log "INFO" "Samples folder copied ($(ls "$PROJECT_PATH/Samples" | wc -l | tr -d ' ') files)"
else
    log "WARN" "Samples folder not found at $PROJECT_PATH/Samples"
fi

# For Flex Consumption, ensure functions.metadata exists
log "INFO" "Creating functions.metadata..."
if [ ! -f "$PUBLISH_DIR/functions.metadata" ]; then
    echo "{}" > "$PUBLISH_DIR/functions.metadata"
fi

# Create the .azurefunctions directory
log "INFO" "Creating .azurefunctions directory structure..."
mkdir -p "$PUBLISH_DIR/.azurefunctions"

# Create function.json files for each function
log "INFO" "Creating function.json files..."


# Create .csproj.buildWithDotNet file for Flex Consumption
log "INFO" "Creating buildWithDotNet marker..."
touch "$PUBLISH_DIR/functions.csproj.buildWithDotNet"

# Create deployment package
log "INFO" "Creating deployment package..."
cd "$PUBLISH_DIR" || exit 1
if ! zip -r "$ZIP_FILE" * .azurefunctions -x "*.pdb" "*.xml"; then
    log "ERROR" "Failed to create deployment package"
    cd "$PROJECT_PATH"
    exit 1
fi
cd "$PROJECT_PATH"

# Verify package contents
log "INFO" "Verifying deployment package..."
if ! unzip -l "$ZIP_FILE" | grep -q "host.json"; then
    log "ERROR" "Deployment package verification failed - missing host.json"
    exit 1
fi

if ! unzip -l "$ZIP_FILE" | grep -q "functions.dll"; then
    log "ERROR" "Deployment package verification failed - missing functions.dll"
    exit 1
fi

if ! unzip -l "$ZIP_FILE" | grep -q "Samples/"; then
    log "WARN" "Deployment package does not contain a Samples/ directory"
fi

if [ "$ZIP_ONLY" = true ]; then
    log "INFO" "Zip-only mode: package ready at $ZIP_FILE"
    exit 0
fi

# Check if function app exists
log "INFO" "Verifying function app exists..."
if ! az functionapp show --name "$FUNCTION_APP" --resource-group "$RESOURCE_GROUP" &> /dev/null; then
    log "ERROR" "Function app $FUNCTION_APP not found in resource group $RESOURCE_GROUP"
    exit 1
fi

# Deploy to Azure
log "INFO" "Deploying to Azure Functions Flex Consumption..."
if ! az functionapp deployment source config-zip \
    --resource-group "$RESOURCE_GROUP" \
    --name "$FUNCTION_APP" \
    --src "$ZIP_FILE" \
    --build-remote false; then
    log "ERROR" "Deployment failed"
    exit 1
fi

log "INFO" "Deployment completed successfully"

# Verify deployment
log "INFO" "Verifying deployment..."
sleep 10  # Wait for deployment to stabilize
if ! az functionapp show --name "$FUNCTION_APP" --resource-group "$RESOURCE_GROUP" --query "state" -o tsv | grep -q "Running"; then
    log "WARN" "Function app may not be running properly. Please check the Azure portal"
fi
