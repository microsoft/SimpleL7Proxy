# Deploy to Azure Container Apps

Deploy the published SimpleL7Proxy image to Azure Container Apps. Configure it at runtime with environment variables or Azure App Configuration.

## TL;DR

- Deploy using the [prebuilt package](../../deployment/README.md).
- Configure hosts and model routing in CompanionApp.
- Secure the proxy and CompanionApp before exposing them to users.

> [!IMPORTANT]
> [Alternative deployment paths](../Legacy/README.md) are documented separately.

> [!WARNING]
> The prebuilt deployment starts with the proxy and CompanionApp unlocked. Unauthorized users could send requests or change configuration. Do not leave them unsecured: configure CompanionApp authentication using its on-screen instructions and [protect proxy requests](../how-to/configure-security.md). CompanionApp displays its authentication reminder in each browser session while authentication is disabled.

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

## Use the prebuilt package

**Choose this method for most deployments.** The package creates the resources defined in `parameters.json` in one resource group.

From the extracted package directory, replace `SUBSCRIPTION_ID` and run:

```bash
bash deploy.sh SUBSCRIPTION_ID validate
bash deploy.sh SUBSCRIPTION_ID what-if
bash deploy.sh SUBSCRIPTION_ID create
```

The first command resolves the `<UNIQ>` placeholders in `parameters.json`. Keep the extracted directory and use it for all three commands.

> [!TIP]
> See the [standard deployment guide](../../deployment/README.md) for prerequisites, custom IDs, App Configuration setup, verification, and recovery.

## Verify the deployment

**Check the deployment, configuration, and request path.**

- [ ] The subscription deployment reports `Succeeded`.
- [ ] The deployment returns a nonempty `proxyUrl`.
- [ ] The Container App revision runs the expected proxy and optional HealthProbe images.
- [ ] The selected App Configuration label contains the expected proxy settings.
- [ ] Readiness returns HTTP 200 after a backend is configured.
- [ ] A request reaches the configured backend.

> [!NOTE]
> Use CompanionApp [Proxy Configuration](../../src/CompanionApp/configuration.md#configuration-prerequisites) to review and publish settings for the selected label.

## If deployment doesn't work

| Symptom | Check |
| --- | --- |
| A standard deployment fails or the terminal disconnects | Follow [Check deployment status](../../deployment/README.md#check-deployment-status) before running `create` again. |
| The proxy starts but isn't ready | Publish a configuration with at least one backend, then confirm the proxy uses the same App Configuration endpoint and label. |

---

[Back to Run SimpleL7Proxy](README.md#1-run)
