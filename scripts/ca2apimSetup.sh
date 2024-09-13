#!/bin/bash

# Function to display usage information
usage() {
    echo "Create an app registration and assign an app role to a service principal."
    echo "The script can be used to setup the container app's managed identity to have access to the APIM."
    echo ""
    echo "Usage: $0 <appname> <principal_id>"
    echo " <appname>       The name of the app to be registered in Azure AD"
    echo " <principal_id>  The principal ID of the service principal to assign the app role"
    echo ""
    echo "Options:"
    echo "  -h    Show this help message"
    exit 0
}

# Check if the -h or --help flag is specified
if [ "$1" == "-h" ] || [ "$1" == "--help" ]; then
    usage
fi

# Check if the correct number of arguments are provided
if [ "$#" -ne 2 ]; then
    usage
fi

# Assign input arguments to variables
appname=$1
PRINCIPAL_ID=$2

#export appname="app-reg-test"

# Check if the app already exists
existing_app_id=$(az ad app list --display-name "$appname" --query "[].appId" --output tsv)
if [ -n "$existing_app_id" ]; then
    echo "Error: An app with the name '$appname' already exists with App ID: $existing_app_id"
    exit 1
fi

# Create the app and service principal
APP_ID=$(az ad app create --display-name "$appname" --query "appId" --output tsv)
az ad sp create --id "$APP_ID"

# Generate UUIDs for role and scope
ROLE_ID=$(uuidgen)
scopeid=$(uuidgen)

az ad app update --id $APP_ID --app-roles '[ {"allowedMemberTypes": ["User", "Application" ], "description": "Caller", "displayName": "Caller", "id": "'$ROLE_ID'","isEnabled": true,"origin": "Application","value": "API.Caller"}]'
az ad app update --id $APP_ID --identifier-uris "api://$APP_ID"
az ad sp update --id $APP_ID --set appRoleAssignmentRequired=true

# Define the JSON for oauth2PermissionScopes
json="[{
    \"adminConsentDescription\": \"Access the API\",
    \"adminConsentDisplayName\": \"Admin Access\",
    \"id\": \"$scopeid\",
    \"isEnabled\": true,
    \"type\": \"Admin\",
    \"userConsentDescription\": \"Access the API\",
    \"userConsentDisplayName\": \"User Access\",
    \"value\": \"api.access\"
}]"

# Update the app with the oauth2PermissionScopes
inp=$(az ad app show --id $APP_ID --query "api" )
out=$(echo "$inp" | jq --argjson x "$json" '.oauth2PermissionScopes = $x')
az ad app update --id $APP_ID --set api="$out"

# Assign the app role to the service principal
RESOURCE_ID=$(az ad sp show --id $APP_ID --query "id" --output tsv)

az rest --method POST --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$PRINCIPAL_ID/appRoleAssignments" --body '{
  "principalId": "'$PRINCIPAL_ID'",
  "resourceId": "'$RESOURCE_ID'",
  "appRoleId": "'$ROLE_ID'"
}'
