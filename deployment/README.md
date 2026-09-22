# Deploy SimpleL7Proxy

Deploy the selected proxy stack through static subscription-scoped Bicep templates and one generated parameters file.

## Prerequisites

- Azure CLI with Bicep support, Bash, `jq`, and an Azure sign-in for the target subscription.
- Subscription Contributor plus role-assignment authority, or subscription Owner.
- Permission to create the selected Azure Container Registry and import images into it.
- For async mode, the selected Service Bus and Cosmos DB resources must already exist. Publish RequestAPI code separately after infrastructure deployment.

## Deploy

Extract the complete ZIP downloaded from Deployment Setup. Use its included deploy.sh from the extracted directory; no repository checkout is required. Replace SUBSCRIPTION_ID with the target subscription ID:

```bash
bash deploy.sh SUBSCRIPTION_ID validate
bash deploy.sh SUBSCRIPTION_ID what-if
bash deploy.sh SUBSCRIPTION_ID create
```

Validation and what-if contact Azure without importing images. Create provisions the resource groups and registry, imports the proxy, optional HealthProbe, selected Companion App, and selected Metrics Server releases, and deploys the remaining infrastructure. The Metrics Server is a single-replica internal Container App in the same managed environment as the proxy. The application stage first creates each selected Container App with its public image to establish its system-assigned identity, grants that principal its runtime roles, and then updates the app to use the copy in ACR. The script defaults to validate when its second argument is omitted.

By default, the deployment creates the Container Apps environment in the proxy resource group. Set `USE_EXISTING_ENVIRONMENT=true`, `ENVIRONMENT_NAME`, and `ENVIRONMENT_RESOURCE_GROUP` to attach every selected Container App to an existing environment instead. The existing environment can share the proxy resource group or reside in another resource group in the same subscription; the deployment does not modify it.

To add a new four-digit suffix to every deployment-created resource name, use `--MakeUniq`. The script applies the suffix to a generated parameters file, prints its path, and leaves it available for reference. The original `parameters.json` remains unchanged.

```bash
bash deploy.sh SUBSCRIPTION_ID validate --MakeUniq
```

The deployment creates the App Configuration store and grants the proxy managed identity App Configuration Data Reader. When Companion App deployment is selected, it grants that app's managed identity App Configuration Data Owner and sets `CompanionApp__AppConfigurationEndpoint` to the created store endpoint. It does not write configuration settings. After `create` succeeds, the script prints the proxy URL and the Companion App URL. Open **Proxy Configuration** in the Companion App and create or duplicate a configuration for the selected label.

## Check after leaving the terminal

**Check the deployment's status before running create again.** Once Azure accepts the deployment, it runs server-side even if Cloud Shell or your terminal disconnects. Reopen Cloud Shell or another Azure CLI terminal signed into the same tenant with access to the original subscription.

Shell variables must be set again in a new session. Replace SUBSCRIPTION_ID with the subscription used for create and DEPLOYMENT_NAME with the name printed by `deploy.sh`. These read-only commands work from any directory without the ZIP, Bicep, or repository checkout:

```bash
subscription='SUBSCRIPTION_ID'
deployment='DEPLOYMENT_NAME'
az deployment sub show --subscription "$subscription" --name "$deployment" \
    --query '{State:properties.provisioningState,Timestamp:properties.timestamp}' --output table
```

| State | What to do |
| --- | --- |
| Accepted, Running, or another nonterminal state | Check again later. Do not submit another create while this deployment is active. |
| Succeeded | Retrieve the application URLs below, then verify readiness and a backend request. |
| Failed or Canceled | Inspect the error below and resolve the cause before retrying. Resources already created remain; a failed deployment is not an automatic rollback. |

To inspect a failure, including nested deployment errors:

```bash
az deployment sub show --subscription "$subscription" --name "$deployment" \
    --query properties.error --output json
```

To retrieve both application URLs after Succeeded:

```bash
az deployment sub show --subscription "$subscription" --name "$deployment" \
    --query '{Proxy:properties.outputs.proxyUrl.value,CompanionApp:properties.outputs.companionAppUrl.value}' --output table
```

If the name is unavailable or Azure reports DeploymentNotFound, confirm the tenant and subscription, then identify the original deployment by name and timestamp:

```bash
az deployment sub list --subscription "$subscription" \
    --query '[].{Name:name,State:properties.provisioningState,Timestamp:properties.timestamp}' --output table
```

A missing deployment record does not establish that no resources were created. In the Azure portal, open **Subscriptions > your subscription > Deployments**, select the original deployment, and inspect its operation details. Follow failed nested deployments into their resource groups when more detail is needed.

**Retry only after a terminal failure and after resolving its cause.** Return to the original extracted ZIP directory, run `bash deploy.sh "$subscription" what-if`, review the changes, then run `bash deploy.sh "$subscription" create`. Keep the same subscription, resource names, and bundle. Do not regenerate a setup or delete partially created resources just to check status or reconnect. The retry reapplies the template; it does not resume the old shell process.

## Bundle Contents

- bootstrap.bicep creates the selected resource groups and Azure Container Registry before image import.
- main.bicep creates or updates the selected infrastructure, enables the Container Apps' system-assigned identities, grants their selected ACR and App Configuration roles, and deploys from the imported ACR image tags.
- modules/*.bicep are the static resource templates consumed by the two entry points.
- parameters.json contains the settings selected in Deployment Setup and is consumed by both Bicep entry points.
- deploy.sh imports the proxy, optional HealthProbe, selected Companion App, and selected Metrics Server images from publicnvmacr into the selected ACR. It does not build local source code.
- Regenerate the bundle to change resource names, image repository names, topology, or other setup values consistently.

## Verify

- Confirm the deployment succeeds and inspect its proxy and Companion App URL outputs. Private-network proxy deployments require access to that network.
- Confirm each selected Container App revision runs the expected proxy, optional HealthProbe, Companion App, and Metrics Server images.
- In the Companion App, confirm the selected App Configuration label contains the expected proxy settings.
- After adding a backend in Proxy Configuration, confirm readiness returns HTTP 200 and a request reaches that backend.

## Troubleshoot

- Image import failure: confirm the deploying identity can import into the selected ACR and can reach the pinned public source image.
- Image pull failure: confirm the imported tag exists and the proxy managed identity has AcrPull on the selected ACR.
- Role-assignment failure: check the deploying identity's role-assignment authority and applicable conditions.
- App Configuration 403 in the Companion App: grant its signed-in identity App Configuration Data Owner on the store and allow time for the assignment to take effect.
- Missing async resource: check the existing Service Bus and Cosmos resource groups, names, and data resources.

Generation does not verify Azure permissions, quota, resource-name availability, or backend connectivity.
