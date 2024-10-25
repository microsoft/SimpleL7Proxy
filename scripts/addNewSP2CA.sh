#!/bin/bash

# Function to display usage information
usage() {
    cat <<EOF
Create a new Service Principal and give it access it to the container app.

Usage: $0 -a <Container App ID> -r <App Role ID> -n <ServicePrincipal Name>


Options:
  -a    The application ID for the container APP
  -r    The app role ID to assign into
  -n    The name if the new service principal to be created

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
if [ "$#" -ne 6 ]; then
    usage
fi

# Parse the input arguments
while getopts "a:r:n:" opt; do
    case $opt in
        a) APP_ID=$OPTARG ;;
        r) ROLE_ID=$OPTARG ;;
        n) SP_NAME=$OPTARG ;;
        \?) echo "Invalid option: $OPTARG" >&2
            usage ;;
        :) echo "Option -$OPTARG requires an argument." >&2
            usage ;;
    esac
done

# ============================== SERVICE PRINCIPAL ==============================

# Create the service principal, its client secret and client_ID ( Used to call the Container App )
sp_output=$(az ad sp create-for-rbac --name "$SP_NAME" --skip-assignment) #2> /dev/null
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

# look up the resource id for the app
RESOURCE_ID=$(az ad sp show --id $APP_ID --query "id" --output tsv)


az rest --method POST --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$directory_object_id/appRoleAssignments" --body '{
  "principalId": "'$directory_object_id'",
  "resourceId": "'$RESOURCE_ID'",
  "appRoleId": "'$ROLE_ID'"
}'
