# Deploy to Azure Container Apps

Deploy the published SimpleL7Proxy image to Azure Container Apps. Configure it at runtime with environment variables or Azure App Configuration.

## TL;DR

- **For most deployments:** Use the [`deployment` Bicep bundle](../../deployment/README.md).
- **For step-by-step control:** Use the [`deployment/interactive` workflow](../../deployment/interactive/README.md).
- **For a custom package:** Use CompanionApp **Deployment Setup** to download a tailored Bicep ZIP.

> [!IMPORTANT]
> Use the standard Bicep bundle unless you need to control individual stages or generate a custom topology.

## Choose a deployment method

| Method | Choose it when | Start here |
| --- | --- | --- |
| **Bicep bundle (recommended)** | You want a complete, repeatable deployment in one resource group. | [`deployment/README.md`](../../deployment/README.md) |
| Interactive scripts | You need private networking, async resources, or control over individual stages. | [`deployment/interactive/README.md`](../../deployment/interactive/README.md) |
| CompanionApp Bicep export | You want to choose resource names, topology, sizing, and features in a guided form. | [Set up CompanionApp](../../src/CompanionApp/readme.md#set-up-companionapp) |

## Configure without rebuilding

**The proxy image runs unchanged.** Each replica combines settings in this order:

| Source | Applied | Overrides |
| --- | --- | --- |
| Built-in defaults | At startup | Nothing |
| Container App environment variables | At startup | Built-in defaults |
| App Configuration `Cold:` settings | At startup | Environment variables |
| App Configuration `Warm:` settings | At startup and after a sentinel refresh | Earlier sources |

If App Configuration is unavailable, the proxy starts with environment variables and built-in defaults. See the [configuration reference](../reference/configuration.md) for all settings and defaults.

### Edit App Configuration

**Use CompanionApp for routine changes.** In **Proxy Configuration**, you can create or duplicate a labeled configuration, edit settings as drafts, review the changes, and publish verified values.

- **[CompanionApp (recommended)](../../src/CompanionApp/configuration.md#configuration-prerequisites):** Stages changes for review, updates `Warm:Sentinel`, and verifies the published values. Its identity needs App Configuration Data Owner.
- **[Azure portal](../how-to/configure-app-configuration.md#change-and-verify-settings):** Edits keys directly in Configuration explorer. Keep the exact label, update `Warm:Sentinel` after a `Warm:` change, and restart the revision after a `Cold:` change.

```text
Environment variables -> replica startup
App Configuration Cold -> replica startup
App Configuration Warm -> sentinel-triggered refresh
```

> [!TIP]
> Use CompanionApp for the normal workflow. Use the Azure portal when you need direct access to an individual key.

## Use the standard bundle

**Choose this method for most deployments.** The bundle creates the resources defined in `parameters.json` in one resource group.

From the extracted bundle directory, replace `SUBSCRIPTION_ID` and run:

```bash
bash deploy.sh SUBSCRIPTION_ID validate
bash deploy.sh SUBSCRIPTION_ID what-if
bash deploy.sh SUBSCRIPTION_ID create
```

The first command resolves the `<UNIQ>` placeholders in `parameters.json`. Keep the extracted directory and use it for all three commands.

> [!TIP]
> See the [standard deployment guide](../../deployment/README.md) for prerequisites, custom IDs, App Configuration setup, verification, and recovery.

## Use the interactive workflow

**Choose this method when you want to run deployment stages individually.** It supports public and private networking. Async stages appear when you enable async mode.

From the repository root:

```bash
cd deployment/interactive
cp deploy.parameters.example.sh deploy.parameters.sh
./deploy.sh
```

Edit `deploy.parameters.sh`, open the menu, then run the enabled steps in order. Rerun a step when you need to update only that part of the deployment.

> [!WARNING]
> Step 7 updates App Configuration. Export the current configuration before you run it. See the [interactive deployment guide](../../deployment/interactive/README.md) for the full sequence and parameter reference.

## Create a custom Bicep package

**Choose this method when you want a guided setup for custom values.** CompanionApp prepares the package locally. Downloading it doesn't deploy resources.

1. [Set up and start CompanionApp](../../src/CompanionApp/readme.md#set-up-companionapp).
2. Open **Deployment Setup** (`/admin/deployment`) and complete the setup tabs.
3. Review the topology, open **Deployment**, select **Bicep**, and download the ZIP.

```text
Deployment Setup > Review
Deployment Setup > Deployment > Bicep
Download ZIP > simplel7proxy-bicep.zip
```

Extract the ZIP and open its `README.md`. The package includes static Bicep templates, your deployment parameters, and a `deploy.sh` script.

> [!NOTE]
> Generate a new package after changing names, topology, images, or features. Use one extracted package for every operation in the same deployment.

## Example: Use a fixed deployment ID

The fixed ID `xq9vs` produces the same resource names in each operation:

| Step | Action | Observable result |
| --- | --- | --- |
| 1 | Run `bash deploy.sh SUBSCRIPTION_ID validate --unique-id xq9vs` | Validation uses deployment name `ca-myapp-proxy-xq9vs-bicep`. No resources are created. |
| 2 | Run `bash deploy.sh SUBSCRIPTION_ID what-if` from the same directory | Azure displays the planned changes for the same resource names. |
| 3 | Run `bash deploy.sh SUBSCRIPTION_ID create` after reviewing the preview | Azure creates or updates the selected resources in `rg-myapp-prod-xq9vs`. |
| 4 | Publish a labeled configuration in CompanionApp | The proxy can load backend and runtime settings from App Configuration. |

## Verify the deployment

**Check the deployment, configuration, and request path.**

- [ ] The subscription deployment reports `Succeeded`.
- [ ] The deployment returns a nonempty `proxyUrl`.
- [ ] The Container App revision runs the expected proxy and optional HealthProbe images.
- [ ] The selected App Configuration label contains the expected proxy settings.
- [ ] Readiness returns HTTP 200 after a backend is configured.
- [ ] A request reaches the configured backend.

> [!NOTE]
> The Bicep bundle creates the App Configuration store but doesn't publish proxy settings. Use CompanionApp [Proxy Configuration](../../src/CompanionApp/configuration.md#configuration-prerequisites) to publish the selected label.

## If deployment doesn't work

| Symptom | Check |
| --- | --- |
| You aren't sure which path to use | Use the recommended Bicep bundle. Choose another path only for per-step control or custom package generation. |
| A standard deployment fails or the terminal disconnects | Follow [Check a deployment after disconnecting](../../deployment/README.md#check-a-deployment-after-disconnecting) before running `create` again. |
| An interactive menu step is disabled | Check `PRIVATE_NETWORK_DEPLOYMENT` and `ASYNC_DEPLOYMENT` in `deploy.parameters.sh`. |
| The CompanionApp ZIP doesn't contain your intended values | Return to Deployment Setup, review every section, and generate a new Bicep package. |
| The proxy starts but isn't ready | Publish a configuration with at least one backend, then confirm the proxy uses the same App Configuration endpoint and label. |

---

[Back to Choose Your Setup](README.md#2-choose-where-to-run)
