#!/bin/bash


# Function to display usage information
usage() {
    cat <<EOF
Configures the container app to use the service principal for Oauth2 and grants 
access to the console app.  


Usage: $0 -g <resource_group> -n <container_app_name> -t <tenant_id> -c <client_id> -s <client_secret> -a <new_app_id>

Options:
  -g    The resource group where the container app is deployed
  -n    The name of the container app
  -t    The tenant ID of the Azure AD
  -c    The client ID of the service principal the conatiner app will use
  -s    The client secret of the service principal the container app will use
  -a    The app ID of the console app that will be granted access to the container app

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
if [ "$#" -ne 12 ]; then
    usage
fi

# Parse the input arguments
while getopts "g:n:t:c:s:a:" opt; do
    case $opt in
        g) RESOURCE_GROUP=$OPTARG ;;
        n) CONTAINER_APP_NAME=$OPTARG ;;
        t) TENANT_ID=$OPTARG ;;
        c) CLIENT_ID=$OPTARG ;;
        s) CLIENT_SECRET=$OPTARG ;;
        a) NEW_APP_ID=$OPTARG ;;
        \?) echo "Invalid option: $OPTARG" >&2
            usage ;;
        :) echo "Option -$OPTARG requires an argument." >&2
            usage ;;
    esac
done

# Verify if the application exists
APP_EXISTS=$(az ad app show --id $CLIENT_ID --query "appId" --output tsv)

if [ -z "$APP_EXISTS" ]; then
    echo "Error: Application with CLIENT_ID $CLIENT_ID does not exist."
    exit 1
else
    #echo "Application with CLIENT_ID $CLIENT_ID found."
    :

fi

# Get the allowed token audience
ALLOWED_TOKEN_AUDIENCES=$(az ad app show --id $CLIENT_ID --query "identifierUris[0]" --output tsv)

if [ -z "$ALLOWED_TOKEN_AUDIENCES" ]; then
    echo "Error: No allowed token audiences found for CLIENT_ID $CLIENT_ID."
    exit 1
else
    #echo "Allowed token audiences: $ALLOWED_TOKEN_AUDIENCES"
    :
fi

APPLICATION_PERMISSION_IDS='["311a71cc-e848-46a1-bdf8-97ff7156d8e6"]'  # User.Read
echo "Retrieving delegated permission IDs..."
DELEGATED_PERMISSION_IDS=$(az rest --method GET --uri "https://graph.microsoft.com/v1.0/oauth2PermissionGrants" --query "value[?clientId=='$CLIENT_ID'].id" --output json)

# Verify if the container app exists
CONTAINER_APP_EXISTS=$(az containerapp show --name $CONTAINER_APP_NAME --resource-group $RESOURCE_GROUP --query "name" --output tsv)

if [ -z "$CONTAINER_APP_EXISTS" ]; then
    echo "Error: Container app $CONTAINER_APP_NAME does not exist in resource group $RESOURCE_GROUP."
    exit 1
else
    #echo "Container app $CONTAINER_APP_NAME found in resource group $RESOURCE_GROUP."
    :
fi

# Update Microsoft identity provider settings for the container app
echo "Updating Microsoft identity provider settings for the container app..."
az containerapp auth microsoft update --name $CONTAINER_APP_NAME --resource-group $RESOURCE_GROUP  -y\
 --client-id $CLIENT_ID --client-secret $CLIENT_SECRET --tenant-id $TENANT_ID\
 --allowed-token-audiences $ALLOWED_TOKEN_AUDIENCES 

# Retrieve the current configuration of the container app's identity provider
CONFIG=$(az containerapp auth show --name $CONTAINER_APP_NAME --resource-group $RESOURCE_GROUP --query "identityProviders.azureActiveDirectory" --output json)
echo "CONFIG: $CONFIG"

# Extract the current allowed applications and ensure it's a dictionary
CURRENT_ALLOWED_APPS=$(echo $CONFIG | jq '.validation.defaultAuthorizationPolicy.allowedApplications | map({(.): 1}) | add')
echo "CURRENT_ALLOWED_APPS: $CURRENT_ALLOWED_APPS"

# Only proceed if there are elements in the array
if [ "$CURRENT_ALLOWED_APPS" != "null" ]; then
    CURRENT_ALLOWED_APPS=$(echo $CURRENT_ALLOWED_APPS | jq 'with_entries(.key |= gsub("^\"|\"$"; ""))')
    UPDATED_ALLOWED_APPS=$(echo $CURRENT_ALLOWED_APPS | jq --arg newAppId "$NEW_APP_ID" '. + {($newAppId): 1}' | jq -c 'keys' | sed 's/\"//g')
else
    UPDATED_ALLOWED_APPS=$(echo "[$NEW_APP_ID]")
fi

echo "UPDATED_ALLOWED_APPS: $UPDATED_ALLOWED_APPS"
az containerapp auth update --name $CONTAINER_APP_NAME --resource-group $RESOURCE_GROUP --set identityProviders.azureActiveDirectory.validation.defaultAuthorizationPolicy.allowedApplications=$UPDATED_ALLOWED_APPS
