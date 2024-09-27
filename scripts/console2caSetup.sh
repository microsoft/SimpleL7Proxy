#!/bin/bash

# Function to display usage information
usage() {
    cat <<EOF
Create an app registration and assign an app role to a service principal.
The script sets up the console app's service principal to access the CA. 

Usage: $0 <appname>

<appname>       The name of the app to be registered in Azure AD


Options:
  -h    Show this help message
EOF
    exit 0
}

# Check if the -h or --help flag is specified
if [ "$1" == "-h" ] || [ "$1" == "--help" ]; then
    usage
fi

# Check if the correct number of arguments are provided
if [ "$#" -ne 1 ]; then
    usage
fi

# Assign input arguments to variables
appname=$1

# ============================== SERVICE PRINCIPAL ==============================

# Create the service principal, its client secret and client_ID ( Used to call the Container App )
sp_output=$(az ad sp create-for-rbac --name "$appname-SP" --skip-assignment) #2> /dev/null
client_ID=$(jq -r '.appId' <<< "$sp_output")

# Calculate the expiration date (90 days from now)
expiry_date=$(date -d "90 days" '+%Y-%m-%dT%H:%M:%SZ')

# Reset the credentials to set the expiration date .. 
sp_out=$(az ad sp credential reset --id $client_ID --end-date $expiry_date)
password=$(jq -r '.password' <<< "$sp_output")
#export password=$(echo $credential_output | jq -r '.password')

# Get the directory object ID for the service principal
directory_object_id=$(az ad sp show --id "$client_ID" --query "id" --output tsv)

#===============================================================================



# ============================== CONTAINER APP =================================

# Create the app registration for the container app:
APP_ID=$(az ad app create --display-name "$appname-CA" --query "appId" --output tsv)
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

# look up the resource id for the app
RESOURCE_ID=$(az ad sp show --id $APP_ID --query "id" --output tsv)

# Assign the app role to the service principal
az rest --method POST --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$directory_object_id/appRoleAssignments" --body '{
  "principalId": "'$directory_object_id'",
  "resourceId": "'$RESOURCE_ID'",
  "appRoleId": "'$ROLE_ID'"
}'

# Create a password for the APP_ID
# Reset the credentials to set the expiration date
app_sp_out=$(az ad sp credential reset --id $APP_ID --end-date $expiry_date)
app_password=$(jq -r '.password' <<< "$app_sp_out")
#export app_password=$(echo $credential_output | jq -r '.password')

echo ""
echo ""
## output the next set of commands to run
echo "The app registration and service principal have been created."
echo "Please run the following commands to complete the setup:"
echo "========================================================"
echo ""
echo "export SP_CLIENT_ID=$client_ID"
echo "export SP_CLIENT_SECRET=$password"
echo "export CONTAINER_APP_ID=$APP_ID"
echo "export CONTAINER_APP_PASSWORD=$app_password"
echo "export ROLE_ID=$ROLE_ID"
echo "export SCOPE_ID=$scopeid"
echo "export DIRECTORY_OBJECT_ID=$directory_object_id"
echo "export APP_NAME=$appname"
echo "export RESOURCE_ID=$RESOURCE_ID"

echo 'enableContainerAppAuth.sh -g <resourcegroup> -n <container_app_name> -t $DIRECTORY_OBJECT_ID -c $CONTAINER_APP_ID -s $CONTAINER_APP_PASSWORD -a $SP_CLIENT_ID'



 
