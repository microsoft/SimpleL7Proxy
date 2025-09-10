#!/bin/bash

# Variables
RESOURCE_GROUP="nvmrequestapi"
FUNCTION_APP="nvmrequestapi"
COSMOS_ACCOUNT="nvmtrcosmosdb"  # Replace with your Cosmos DB account name
COSMOS_RESOURCE_GROUP="TR-servicebus"  # Replace with the resource group of your Cosmos DB account
DATABASE_NAME="%CosmosDb:DatabaseName%"    # Using the same placeholder from your function app settings

# Get the function app's managed identity principal ID
echo "Getting function app's managed identity..."
FUNCTION_APP_PRINCIPAL_ID=$(az functionapp identity show \
    --name "$FUNCTION_APP" \
    --resource-group "$RESOURCE_GROUP" \
    --query "principalId" \
    --output tsv)

if [ -z "$FUNCTION_APP_PRINCIPAL_ID" ]; then
    echo "Error: No user-assigned managed identity found"
    exit 1
fi

echo "Found principal ID: $FUNCTION_APP_PRINCIPAL_ID"

az cosmosdb sql role definition list \
    --resource-group "$COSMOS_RESOURCE_GROUP" \
    --account-name "$COSMOS_ACCOUNT"

# Get the cosmosDB Account ID
COSMOS_ID=$(az cosmosdb show \
    --name "$COSMOS_ACCOUNT" \
    --resource-group "$COSMOS_RESOURCE_GROUP" \
    --query id --output tsv)
if [ -z "$COSMOS_ID" ]; then
    echo "Error: Could not find Cosmos DB account $COSMOS_ACCOUNT"
    exit 1
fi

# Get the Contributor role from the cosmosdb
echo "Getting Cosmos DB account details..."
ROLE_DEFINITION_ID=$(az cosmosdb sql role definition list \
    --resource-group "$COSMOS_RESOURCE_GROUP" \
    --account-name "$COSMOS_ACCOUNT" \
    --query "[?name=='00000000-0000-0000-0000-000000000002'].id" \
    --output tsv)

if [ -z "$ROLE_DEFINITION_ID" ]; then
    echo "Error: Could not find Contributor role definition in Cosmos DB account $COSMOS_ACCOUNT"
    exit 1
fi

# Create the role assignment to grant the function app access to Cosmos DB
az cosmosdb sql role assignment create \
    --resource-group "$COSMOS_RESOURCE_GROUP" \
    --account-name "$COSMOS_ACCOUNT" \
    --role-definition-id "$ROLE_DEFINITION_ID" \
    --principal-id "$FUNCTION_APP_PRINCIPAL_ID" \
    --scope "$COSMOS_ID"


# Print success message
echo "Successfully granted Cosmos DB access to function app $FUNCTION_APP"
echo "Make sure you have the following app settings configured in your function app:"
echo "1. CosmosDb:DatabaseName"
echo "2. CosmosDb:ContainerName"
echo "3. CosmosDbConnection with format: AccountEndpoint=https://${COSMOS_ACCOUNT}.documents.azure.com:443/;"
