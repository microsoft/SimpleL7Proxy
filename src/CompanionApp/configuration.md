# Update Azure App Configuration

## Configuration prerequisites

- **.NET 10 SDK** and a checkout of this repository to run CompanionApp. Follow the [shared setup](readme.md#set-up-companionapp).
- **An existing Azure App Configuration store**, its HTTPS endpoint, and the exact label when updating an existing configuration.
- **An authenticated Azure identity** for CompanionApp. For local development, use the [Azure sign-in steps](readme.md#sign-in-for-azure-workflows).
- **App Configuration Data Owner** on the store for the identity running CompanionApp. App Configuration Data Reader permits reading but not publishing; Azure management-plane access alone is not sufficient.
- **Network access to the store** from the machine running CompanionApp. Private endpoints require the corresponding network access and DNS resolution.

## Connect to a store

**Proxy Configuration edits the proxy's remote `Warm:` and `Cold:` settings, not CompanionApp's local JSON files. Changes remain drafts until you publish them with Update.** Use this workflow for an existing store, whether the proxy was deployed through CompanionApp or another process.

Edit these JSON property paths in [appsettings.Development.json](appsettings.Development.json), then restart CompanionApp:

| Setting | What to edit |
| --- | --- |
| `CompanionApp.AppConfigurationEndpoint` | Use your existing store's HTTPS endpoint ending in `.azconfig.io`. This is not the proxy URL. Leave empty when not using the editor. |
| `CompanionApp.AppConfigurationLabel` | Use the exact, case-sensitive label read by your proxy. Leave empty only when using unlabeled settings. |
| `CompanionApp.BypassConfig` | Keep `false` for normal use. This is a development cache option, not an authentication or read-only mode. |

Confirm the proxy uses that same store and label through `AZURE_APPCONFIG_ENDPOINT` and `AZURE_APPCONFIG_LABEL`. The proxy's managed identity needs its own read permission; CompanionApp's permission does not grant access to the proxy.

> [!WARNING]
> The editor uses the CompanionApp server's Azure identity, not each browser user's portal session. Keep the app local or protect it with authentication and restricted network access before giving it write permissions and exposing it to other users.

## Edit an existing configuration

1. Open **Config > Proxy Configuration** (`/admin/configuration`). Confirm **App Configuration URL**, then select **Refresh** if the configurations have not loaded. Use **App Configuration settings** to reveal the URL again when the header is collapsed.
2. Select the **Update** action, choose the exact **Starting configuration (label)**, and select **Continue**. This opens an editor; it does not write to Azure.
3. Open the relevant section, such as **Hosts**, **Request**, or **Logging**. Edit values and close the section to retain them as local drafts. Saving a host dialog also stages the value locally.
4. Select the **settings changed** button to review the staged changes. Check the target label before publishing.
5. Select the editor's **Update** button. CompanionApp writes the changed keys, gives `Warm:Sentinel` a new value under the same label, then downloads and compares the saved values.
6. Wait for **Updated and verified** and check the old/new sentinel values in the status message. This confirms the values in the store, not that every running proxy has applied them.

**Reset to defaults** stages built-in defaults for matching non-host settings; it is not a rollback to the last saved configuration. Review the changed-settings list before publishing.

## Create or duplicate a configuration

- Select **New**, enter a unique label, and edit the generated proxy defaults.
- Select **Duplicate**, choose a source label, and enter a new label to copy an existing configuration.

Both actions create local drafts; only the final **Update** creates the label's settings in Azure. Creating a label does not switch any proxy to it.

## Confirm the proxy picked up the change

- [ ] The editor reports **Updated and verified**, and the selected label contains the expected value and new `Warm:Sentinel`.
- [ ] For a `Warm:` change, the proxy checks the sentinel at `AZURE_APPCONFIG_REFRESH_INTERVAL_SECONDS` intervals (default: 30 seconds). Check its `[APP-CONFIG] Sentinel changed` log and verify the changed behavior with a request.
- [ ] For a `Cold:` change, restart the affected proxy processes or active Container App revisions. Updating the sentinel does not apply cold settings to an already running process.

See [Change and verify proxy settings](../../docs/how-to/configure-app-configuration.md#change-and-verify-settings) for runtime checks and recovery guidance.

## Troubleshoot configuration updates

| Symptom | Check |
| --- | --- |
| Proxy Configuration reports authentication failure or Azure `403` | Check the identity in the same environment as the app, the store endpoint, data-plane role assignments, and private-network access. Data Reader cannot publish changes. |
| No proxy settings appear for a label | Match the label's case and check that the store contains published `Warm:` or `Cold:` keys. New stores can use **New**; existing labels without `Warm:Sentinel` need that key under the same label before Update. |
| Update succeeds but proxy behavior is unchanged | Check the proxy's store and label, then the warm sentinel log or cold-setting restart requirement. See [App Configuration troubleshooting](../../docs/how-to/configure-app-configuration.md#troubleshoot-setting-changes). |

> [!WARNING]
> `BypassConfig=true` can load a stale snapshot from `data/appconfig-cache`; a missing cache falls back to Azure. **Update still writes to Azure.** Writes are made one key at a time, so a failed update can leave partial changes. Reload and inspect the stored values before retrying; do not assume a rollback occurred.