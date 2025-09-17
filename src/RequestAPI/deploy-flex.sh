#!/bin/bash

set -e  # Exit on error

# Color codes for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Variables
PROJECT_PATH=$(pwd)  # Get absolute path
SHARED_PROJECT_PATH="../Shared"
PUBLISH_DIR="$PROJECT_PATH/bin/publish"
ZIP_FILE="$PROJECT_PATH/RequestAPI.zip"
RESOURCE_GROUP="nvmrequestapi"
FUNCTION_APP="nvmrequestapi"

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

# Verify Azure CLI login
log "INFO" "Verifying Azure CLI login..."
if ! az account show &> /dev/null; then
    log "ERROR" "Not logged into Azure CLI. Please run 'az login' first."
    exit 1
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

# Build the Shared project
log "INFO" "Building Shared project..."
if ! dotnet build "$SHARED_PROJECT_PATH/Shared.csproj" -c Release; then
    log "ERROR" "Failed to build Shared project"
    exit 1
fi

# Clean and build the main project first
log "INFO" "Building RequestAPI project..."
if ! dotnet clean "$PROJECT_PATH/RequestAPI.csproj" -c Release; then
    log "ERROR" "Failed to clean RequestAPI project"
    exit 1
fi

if ! dotnet build "$PROJECT_PATH/RequestAPI.csproj" -c Release; then
    log "ERROR" "Failed to build RequestAPI project"
    exit 1
fi

# Check for required build output files
if [ ! -f "$PROJECT_PATH/bin/Release/net9.0/RequestAPI.dll" ] || \
   [ ! -f "$PROJECT_PATH/bin/Release/net9.0/RequestAPI.deps.json" ] || \
   [ ! -f "$PROJECT_PATH/bin/Release/net9.0/RequestAPI.runtimeconfig.json" ]; then
    log "ERROR" "Required build output files are missing"
    exit 1
fi

# Publish the project
log "INFO" "Publishing RequestAPI project..."
if ! dotnet publish "$PROJECT_PATH/RequestAPI.csproj" -c Release -o "$PUBLISH_DIR" --no-build; then
    log "ERROR" "Failed to publish RequestAPI project"
    exit 1
fi

# Verify publish output
log "INFO" "Verifying publish output..."
if [ ! -f "$PUBLISH_DIR/RequestAPI.dll" ] || \
   [ ! -f "$PUBLISH_DIR/RequestAPI.deps.json" ] || \
   [ ! -f "$PUBLISH_DIR/RequestAPI.runtimeconfig.json" ]; then
    log "ERROR" "Required files missing in publish directory"
    exit 1
fi

# Copy configuration files
log "INFO" "Copying configuration files..."
if ! cp "$PROJECT_PATH/host.json" "$PUBLISH_DIR/host.json"; then
    log "ERROR" "Failed to copy host.json"
    exit 1
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

# For ProcessDocuments function
mkdir -p "$PUBLISH_DIR/.azurefunctions/ProcessDocuments"
cat > "$PUBLISH_DIR/.azurefunctions/ProcessDocuments/function.json" << EOLINNER
{
  "bindings": [
    {
      "type": "serviceBusTrigger",
      "direction": "in",
      "name": "message",
      "queueName": "%sbqueuename%",
      "connection": "ServiceBusConnection"
    }
  ],
  "scriptFile": "../RequestAPI.dll",
  "entryPoint": "RequestAPI.DocumentProcessor.Run"
}
EOLINNER

# Create .csproj.buildWithDotNet file for Flex Consumption
log "INFO" "Creating buildWithDotNet marker..."
touch "$PUBLISH_DIR/RequestAPI.csproj.buildWithDotNet"

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

if ! unzip -l "$ZIP_FILE" | grep -q "RequestAPI.dll"; then
    log "ERROR" "Deployment package verification failed - missing RequestAPI.dll"
    exit 1
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
