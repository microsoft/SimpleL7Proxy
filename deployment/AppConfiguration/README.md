# Deploy Azure App Configuration

Step 7 of `deployment/deploy.sh` creates or reuses an App Configuration store, imports the proxy settings, assigns RBAC, and connects the Container App.

For setting names, precedence, and runtime changes, see [Using Azure App Configuration with SimpleL7Proxy](../../docs/AZURE_APP_CONFIGURATION.md).

## Requirements

- Deployment step 5 completed with `ENABLE_MANAGED_IDENTITY=true`.
- Azure CLI authenticated to the target subscription.
- Permission to create the store, update the Container App, and assign roles.
- `deployment/deploy.parameters.sh` configured for the environment.

Use `deployment/deploy.sh` as the entry point. It invokes `deployment/AppConfiguration/deploy.sh` for step 7.

## Parameters

Set these values in `deployment/deploy.parameters.sh`:

| Variable | Required/default | Use |
|---|---|---|
| `CONTAINER_APP_NAME` | Required | Existing Container App |
| `CONTAINER_APP_RESOURCE_GROUP` | Required | Resource group containing the Container App |
| `APPCONFIG_RESOURCE_GROUP` | Required | Resource group for App Configuration |
| `LOCATION` | Required | Region for a new resource group or store |
| `APPCONFIG_NAME` | Required; globally unique | Store name |
| `APPCONFIG_SKU` | `standard` | Store SKU |
| `APPCONFIG_LABEL` | `prod` | Label assigned to imported keys |
| `AZURE_APPCONFIG_REFRESH_INTERVAL_SECONDS` | `30` | Sentinel polling interval, in seconds |
| `UPDATE_CONTAINER_APP_ENV` | `true` | Write the endpoint, label, and interval to the Container App |

> [!WARNING]
> Do not commit `deployment/deploy.parameters.sh`.

## Deploy

From the repository root:

```bash
cd deployment
cp deploy.parameters.example.sh deploy.parameters.sh  # first deployment only
${EDITOR:-vi} deploy.parameters.sh
./deploy.sh
```

Select **7) App Configuration**. The command completes with:

```text
App Configuration deployment complete
Store: <store-name>
Endpoint: <store-endpoint>
Label: <label>
Config keys published: <total> (Warm: <count>, Cold: <count>)
```

Step 7:

- Reads the live Container App and [`ProxyConfig.cs`](../../src/SimpleL7Proxy/Config/ProxyConfig.cs).
- Creates or reuses the resource group and App Configuration store.
- Grants **App Configuration Data Owner** to the signed-in user and **App Configuration Data Reader** to the Container App identity when those assignments are missing.
- Imports all publishable settings under `APPCONFIG_LABEL`. Values come from the Container App, local environment, code default, or `-`, in that order.
- Writes `Warm:Sentinel` and `Warm:RefreshSeconds`.
- When `UPDATE_CONTAINER_APP_ENV=true`, sets `AZURE_APPCONFIG_ENDPOINT`, `AZURE_APPCONFIG_LABEL`, and `AZURE_APPCONFIG_REFRESH_INTERVAL_SECONDS` on the Container App.

### JSON fallback reference

After the deployment summary, the script prints **Environment Variable Defaults (JSON)**. Keep this output as a baseline fallback configuration. Each property is a Container App environment-variable name, and its value is the default read from `ProxyConfig.cs`.

If a proxy replica starts while App Configuration is unreachable, it uses the environment variables defined on the Container App, followed by built-in defaults. To make the fallback explicit, create a new Container App revision and add the required non-null JSON entries as manual environment variables. In the Azure portal, open the Container App, select **Revisions and replicas** > **Create new revision**, edit the container, and add the name/value pairs under **Environment variables**. See [Manage environment variables on Azure Container Apps](https://learn.microsoft.com/azure/container-apps/environment-variables#add-environment-variables-on-existing-container-apps).

The JSON is not a copy of the effective App Configuration values. It contains code defaults only. Update the retained values if the fallback must match environment-specific settings; use a KVSet export when the actual App Configuration keys, labels, and values must be preserved.

## Rerun Step 7

Rerunning step 7 updates the managed keys under `APPCONFIG_LABEL` from the Container App, local environment, and code defaults. Values edited directly in App Configuration can be replaced when the same key and label are imported; unrelated keys are not deleted.

If a previous value is needed, use App Configuration's [point-in-time Restore](https://learn.microsoft.com/azure/azure-app-configuration/concept-point-time-snapshot#restore-key-values). For an offline or long-term copy, use [Import/export](https://learn.microsoft.com/azure/azure-app-configuration/howto-import-export-data). After recovery, follow [App Configuration operations](../../docs/AZURE_APP_CONFIGURATION.md#change-and-verify-settings) to apply the values to running replicas.

## Verify

Inspect the store, imported keys, and Container App connection:

```bash
cd deployment
source deploy.parameters.sh

az appconfig show --name "$APPCONFIG_NAME" \
    --resource-group "$APPCONFIG_RESOURCE_GROUP" \
    --query "{Name:name,Endpoint:endpoint,Sku:sku.name}" -o table

az appconfig kv list --name "$APPCONFIG_NAME" --auth-mode login \
    --label "$APPCONFIG_LABEL" --query "[].{Key:key,Value:value}" -o table

az containerapp show --name "$CONTAINER_APP_NAME" \
    --resource-group "$CONTAINER_APP_RESOURCE_GROUP" \
    --query "properties.template.containers[0].env[?starts_with(name, 'AZURE_APPCONFIG_')]" \
    -o table
```

Expected results:

- The store exists in `APPCONFIG_RESOURCE_GROUP`.
- The selected label contains the imported keys and `Warm:Sentinel`.
- The Container App has the three `AZURE_APPCONFIG_*` values when `UPDATE_CONTAINER_APP_ENV=true`.

## Troubleshooting

| Symptom | Likely cause | Check |
|---|---|---|
| `Could not read Container App` | Incorrect app, resource group, subscription, or step 5 was not completed | Verify with `az containerapp show` |
| `NameUnavailable` | Store name is already used in Azure | Change `APPCONFIG_NAME` |
| No system-assigned managed identity | Identity was disabled during step 5 | Enable it and rerun steps 5 and 7 |
| Import returns `403` | Missing data-plane access or RBAC propagation delay | Verify **App Configuration Data Owner** and retry |
| `No [ConfigOption(...)] decorations found` | Source file is missing or cannot be parsed | Verify [`ProxyConfig.cs`](../../src/SimpleL7Proxy/Config/ProxyConfig.cs) |
| `Could not update Container App env vars` | Missing update permission or incorrect app/container | Verify Azure permissions and deployment parameters |

## Related

- [Interactive deployment](../README.md)
- [App Configuration operations](../../docs/AZURE_APP_CONFIGURATION.md)
- [Proxy setting reference](../../docs/CONFIGURATION_SETTINGS.md)
