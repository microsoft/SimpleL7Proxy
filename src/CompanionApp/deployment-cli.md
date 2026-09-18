# Deploy CompanionApp with Azure CLI

Automate CompanionApp deployment to Azure App Service using the Azure CLI. This path is suitable for repeatable deployments and CI/CD integration.

## Before you begin
- Azure Cloud Shell or local Azure CLI installation
- Azure CLI + Bash + `jq`
- The included [CompanionApp package](artifacts/companion-app-appservice.zip)

## 1. Sign in

```bash
az login
```

## 2. Set deployment values

```bash
subscription_id='<subscription-id>'
resource_group='rg-companion-app'
location='eastus'
plan_name='asp-companion-app'
webapp_name='<globally-unique-web-app-name>'

app_config_resource_group='<app-config-resource-group>'
app_config_name='<app-config-store-name>'

az account set \
    --subscription "$subscription_id"
```

## 3. Create the App Service

```bash
az group create \
    --name "$resource_group" \
    --location "$location"

az appservice plan create \
    --name "$plan_name" \
    --resource-group "$resource_group" \
    --location "$location" \
    --sku B1 \
    --is-linux

az webapp create \
    --name "$webapp_name" \
    --resource-group "$resource_group" \
    --plan "$plan_name" \
    --runtime 'DOTNETCORE|10.0'

az webapp update \
    --name "$webapp_name" \
    --resource-group "$resource_group" \
    --https-only true
```

## 4. Upload CompanionApp

```bash
cd src/CompanionApp

bash upload_zip.sh \
    -n "$webapp_name" \
    -g "$resource_group"
```

To package source changes before uploading:

```bash
bash make-zip.sh
```

## 5. Turn on managed identity

```bash
az webapp identity assign \
    --name "$webapp_name" \
    --resource-group "$resource_group"

principal_id=$(az webapp show \
    --name "$webapp_name" \
    --resource-group "$resource_group" \
    --query identity.principalId \
    --output tsv)

test -n "$principal_id"
```

## 6. Assign the App Configuration role

```bash
app_config_id=$(az appconfig show \
    --name "$app_config_name" \
    --resource-group "$app_config_resource_group" \
    --query id \
    --output tsv)

az role assignment create \
    --assignee-object-id "$principal_id" \
    --assignee-principal-type ServicePrincipal \
    --role "App Configuration Data Owner" \
    --scope "$app_config_id"
```

## 7. Assign optional roles

### Event Hubs or Blob Storage

```bash
resource_id='<resource-id>'
role_name='Azure Event Hubs Data Receiver'

az role assignment create \
    --assignee-object-id "$principal_id" \
    --assignee-principal-type ServicePrincipal \
    --role "$role_name" \
    --scope "$resource_id"
```

For Blob Storage:

```bash
role_name='Storage Blob Data Contributor'
```

### Cosmos DB

```bash
cosmos_resource_group='<cosmos-rg>'
cosmos_account_name='<cosmos-account>'

cosmos_role_id=$(az cosmosdb sql role definition list \
    --account-name "$cosmos_account_name" \
    --resource-group "$cosmos_resource_group" \
    --query "[?roleName=='Cosmos DB Built-in Data Contributor'].id | [0]" \
    --output tsv)

az cosmosdb sql role assignment create \
    --account-name "$cosmos_account_name" \
    --resource-group "$cosmos_resource_group" \
    --role-definition-id "$cosmos_role_id" \
    --principal-id "$principal_id" \
    --scope '/'
```

## 8. Publish settings

```bash
bash update_settings.sh \
    -n "$webapp_name" \
    -g "$resource_group"
```

For false-valued settings:

```bash
az webapp config appsettings set \
    --name "$webapp_name" \
    --resource-group "$resource_group" \
    --settings \
    "CompanionApp__FeatureEnabled=false"
```

## Verify the deployment

```bash
app_host=$(az webapp show \
    --name "$webapp_name" \
    --resource-group "$resource_group" \
    --query defaultHostName \
    --output tsv)

curl --fail \
     --show-error \
     --location \
     "https://$app_host/"
```

Open:

```text
https://<app-host>
```

Verify:

- Home page loads.
- Proxy Configuration loads.
- App Configuration store can be accessed.
