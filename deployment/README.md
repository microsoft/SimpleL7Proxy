# Deploy SimpleL7Proxy

The quickest way to deploy is to use Azure Cloud Shell to deploy the ZIP created by Deployment Setup. The zip file contains parameterized bicep files and deploys the following architecture:

![alt text](arch.png)

## Before you begin

You need:

- The complete deployment ZIP from Deployment Setup.
- The target Azure subscription ID.
- Subscription Contributor access plus permission to assign roles, or subscription Owner access.
- Permission to create an Azure Container Registry and import images.

## Deploy from Azure Cloud Shell

1. Open Azure Cloud Shell in Bash mode.
2. Select **Manage files > Upload**.
3. Upload the deployment ZIP.
4. Extract it and open the extracted directory:

```bash
zip='YOUR_DEPLOYMENT_ZIP.zip'

mkdir simplel7proxy-deployment
unzip "$zip" -d simplel7proxy-deployment
cd simplel7proxy-deployment
```

5. Set the subscription and deploy:

```bash
sub='YOUR_SUBSCRIPTION_ID'
./deploy.sh "$sub" create
```

This creates a uniq deployment by adding uique name to the end of the resources.

![alt text](deployment.png)

When deployment succeeds, the script prints the available proxy and Companion App URLs. Make a note of these so that you can validate the proxy..


## Configure the proxy

Open the Companion App URL and select **Proxy Configuration**.

Select Update and pick the deployment label: prod.
![alt text](Config.png)

The deployment seeds the App Configuration store with default values during the deployment.  You can make updates in the Companion App; most settings will become active in 30 seconds but some will require a proxy restart.

## Verify the deployment

- Open the Log Stream in the proxy container app to validate that everything comes up cleanly:
![alt text](ready.png)


The proxy doesn't know about your backend hosts yet, so you should now configure the host in the companion app to start using the proxy.

## Check deployment status

**Do not run `create` again while the deployment is still active.** Azure continues the deployment after Cloud Shell or your terminal disconnects.

Open Cloud Shell and set the original subscription and deployment name:

```bash
sub='YOUR_SUBSCRIPTION_ID'
deployment='YOUR_DEPLOYMENT_NAME'

az deployment sub show \
    --subscription "$sub" \
    --name "$deployment" \
    --query '{State:properties.provisioningState,Timestamp:properties.timestamp}' \
    --output table
```

| State | Next step |
| --- | --- |
| `Accepted` or `Running` | Wait and check again. |
| `Succeeded` | Retrieve the URLs and verify the deployment. |
| `Failed` or `Canceled` | Inspect the error and resolve it before retrying. |

Inspect a failure:

```bash
az deployment sub show \
    --subscription "$sub" \
    --name "$deployment" \
    --query properties.error \
    --output json
```

Retrieve the application URLs:

```bash
az deployment sub show \
    --subscription "$sub" \
    --name "$deployment" \
    --query '{Proxy:properties.outputs.proxyUrl.value,CompanionApp:properties.outputs.companionAppUrl.value}' \
    --output table
```

If Azure reports `DeploymentNotFound`, confirm the tenant and subscription, then list recent subscription deployments:

```bash
az deployment sub list \
    --subscription "$sub" \
    --query '[].{Name:name,State:properties.provisioningState,Timestamp:properties.timestamp}' \
    --output table
```

A missing deployment record does not mean that no resources were created. In the Azure portal, open **Subscriptions > your subscription > Deployments** and inspect the deployment operations.

## Retry a failed deployment

Retry only after the deployment reaches a terminal state and you resolve the reported error.

Return to the original extracted ZIP directory:

```bash
./deploy.sh "$sub" what-if
./deploy.sh "$sub" create
```

Use the same subscription, resource names, and deployment ZIP. Do not regenerate the setup or delete partially created resources only to retry the deployment.

## Preview changes

Validate the deployment:

```bash
./deploy.sh "$sub" validate
```

Preview the Azure changes:

```bash
./deploy.sh "$sub" what-if
```

These commands contact Azure but do not import images or create the deployment.

If no action is supplied, `deploy.sh` runs `validate`.

## Use unique resource names

Add `--MakeUniq` to generate a new four-digit suffix for deployment-created resource names:

```bash
./deploy.sh "$sub" validate --MakeUniq
```

The script prints the generated parameters-file path. The original `parameters.json` remains unchanged.

## Use an existing Container Apps environment

By default, the deployment creates the Container Apps environment in the proxy resource group.

Set these values to use an existing environment:

```bash
USE_EXISTING_ENVIRONMENT=true
ENVIRONMENT_NAME='YOUR_ENVIRONMENT_NAME'
ENVIRONMENT_RESOURCE_GROUP='YOUR_ENVIRONMENT_RESOURCE_GROUP'
```

The environment must be in the same subscription. It can be in the proxy resource group or another resource group. The deployment does not modify it.

## Deploy from a local terminal

Azure Cloud Shell is the recommended deployment environment.

For local deployment, install:

- Azure CLI with Bicep support
- Bash
- `jq`

Sign in to the target Azure tenant, extract the deployment ZIP, and run:

```bash
sub='YOUR_SUBSCRIPTION_ID'
./deploy.sh "$sub" create
```

The same Azure permissions and recovery steps apply.

## What the deployment creates

The deployment:

- Creates the selected resource groups and Azure Container Registry.
- Imports the selected proxy, HealthProbe, Companion App, and Metrics Server images.
- Creates the selected infrastructure.
- Enables system-assigned identities on the Container Apps.
- Grants the required ACR and App Configuration roles.
- Updates the Container Apps to use the imported ACR images.
- Creates the Metrics Server as a single-replica internal Container App when selected.
- Prints the deployment name and available application URLs.

The deployment grants:

- App Configuration Data Owner to the deployment principal.
- App Configuration Data Reader to the proxy managed identity.
- App Configuration Data Owner to the Companion App managed identity when selected.

## What the ZIP contains

- `bootstrap.bicep` creates the selected resource groups and Azure Container Registry.
- `main.bicep` creates or updates the selected application infrastructure.
- `modules/*.bicep` contains the Bicep modules used by the entry points.
- `parameters.json` contains the values selected in Deployment Setup.
- `deploy.sh` validates, previews, and creates the deployment.

The script imports released images from `publicnvmacr`. It does not build source code locally.

Regenerate the ZIP when changing resource names, image names, topology, or Deployment Setup selections.

## Troubleshoot

**Image import fails**

Confirm the deployment identity can import images into the selected Azure Container Registry and can reach the public source registry.

**A Container App cannot pull its image**

Confirm the imported image tag exists and the Container App managed identity has `AcrPull` on the registry.

**Role assignment fails**

Confirm the deployment identity can create role assignments at the required scopes.

**The Companion App receives HTTP 403 from App Configuration**

Confirm its managed identity has App Configuration Data Owner. Allow time for the role assignment to take effect, then retry.

**An async resource is missing**

Confirm the configured Service Bus and Cosmos DB resources, resource groups, and data resources already exist.

Deployment generation does not check Azure permissions, quota, resource-name availability, or backend connectivity.