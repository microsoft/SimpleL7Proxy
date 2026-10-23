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

1. Open **Config > Proxy Configuration** (`/admin/configuration`). The page loads the configured `CompanionApp.AppConfigurationEndpoint` and opens **Configuration Details** for the exact `CompanionApp.AppConfigurationLabel`. Loading does not write to Azure; an empty label selects unlabeled settings.
   While loading, the page shows a loading banner above grey, non-interactive section cards. The same Details layout becomes colored and interactive when loading finishes; placeholder cards do not contain downloaded values.
2. Confirm the label in the header. To edit another label, select **Back to Configurations**, then **Update**, choose the **Starting configuration (label)**, and select **Continue**. Use **App Configuration settings** to inspect the endpoint. The refresh button reloads the current label and is disabled while local changes are pending; **Last loaded** is the download time, not the store's last modification time.
3. Open the relevant section, such as **Hosts**, **Request**, or **Logging**. Edit values and close the section to retain them as local drafts. Saving a host dialog also stages the value locally.
   App Configuration uses [section widgets](Components/Widgets/AppConfiguration/) for both grey loading placeholders and loaded summary cards. [AppConfigurationSectionsWidget](Components/Widgets/AppConfiguration/AppConfigurationSectionsWidget.razor) owns only grid layout and section dispatch; the ten non-Tokenomics sections have individual widget files containing their icons and draft-aware summaries. Unknown sections use the generic widget, and the missing-Hosts card uses the Hosts widget with empty settings. All use [AppConfigurationSectionWidget](Components/Widgets/AppConfiguration/AppConfigurationSectionWidget.razor) for the shared card shell, help, loading state, and changed badge.

   The shared section widget also renders the expanded popup shell; the existing section editors retain their controls. [AdminConfigurationPage](Components/Pages/AdminConfigurationPage.razor) owns section selection, label loading, drafts, validation, navigation guards, and explicit cloud saves. Closing a section does not publish or discard its drafts.

   Page, card, and popup styles are centralized in [app-configuration.css](wwwroot/app-configuration.css), loaded through `Assets` in [App.razor](Components/App.razor). Selectors are scoped beneath `.app-configuration-page`, including native label and unsaved-change dialogs; document scroll locking applies only while this page has an open custom popup. Widgets inherit the page's interactive render mode and retain the existing keyboard/focus handling.

   **Tokenomics** is displayed directly beneath the other configuration cards on the same page; there is no Tokenomics navigation card or separate editor view. Its first row contains **Limits & Budgets**, **Request Priority**, and **Capacity & Queues**; the second row contains **Model Routing** and **Model Costs**. No popup opens initially. Selecting a summary opens that widget's expanded editor in a popup; only one Tokenomics popup is rendered at a time. Close, Escape, or clicking the backdrop closes the popup and retains local drafts. Summaries reflect the current valid draft values. Reopening widgets or editing other sections preserves staged changes, field errors, and model search/pagination state without saving. A warning remains visible when a closed widget contains invalid edits.

   In the expanded views, limits and budgets share one period matrix. Model Routing edits the default model, hierarchy, unavailable-model action, and replacement action; Model Costs separately edits model names and input, cached input, and output prices. Narrow screens stack the summaries. Policy-action groups start expanded and can be collapsed. The single bottom **Save changes** button uses the same explicit **Update** flow for every section; **Discard** restores all loaded values for the current configuration, including changes outside Tokenomics, and clears buffered Tokenomics input and validation errors. Loading disables both card groups and shows Tokenomics placeholders rather than invented values.

   Summary cards use labeled lines with compact token/budget units and delay seconds; the selection border replaces the Edit/Currently editing line. Summaries show loaded draft actions, not hardcoded defaults. If quota-exceeded actions differ by period, all three are shown. Empty model routing/pricing is explicitly identified; hierarchy names remain in the expanded editor, and long default-model names are truncated in the summary.

   The display is composed from [Tokenomics configuration widgets](Components/Widgets/TokenomicsConfiguration/). [TokenomicsEditor](Components/Shared/Settings/TokenomicsEditor.razor) retains draft parsing, validation, model pricing, search, pagination, and change callbacks; the configuration page retains label navigation and cloud saves. Widgets inherit the page's interactive render mode rather than starting separate interactive roots.

   All editor layout, field, card, and save-bar styles are centralized in [tokenomics-configuration.css](wwwroot/tokenomics-configuration.css), loaded through `Assets` in [App.razor](Components/App.razor), like the `/tokenomics` stylesheet. Selectors are scoped beneath `.tokenomics-configuration` so editor styling does not alter reports or other configuration sections. Shared documentation-help styling remains in its existing shared component.
4. Select the **settings changed** button to review the staged changes. Check the target label before publishing.
5. Select the editor's **Update** button. CompanionApp writes the changed keys, gives `Warm:Sentinel` a new value under the same label, then downloads and compares the saved values.
6. Wait for **Updated and verified** and check the old/new sentinel values in the status message. This confirms the values in the store, not that every running proxy has applied them.

**Reset to defaults** stages built-in defaults for matching non-host settings; it is not a rollback to the last saved configuration. Review the changed-settings list before publishing.

## Create or duplicate a configuration

- From **Configuration Details**, select **Back to Configurations**. Resolve any unsaved-change prompt before switching.
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
| The configured default label was not found | Check `CompanionApp.AppConfigurationLabel`, including case. The page keeps configuration selection available; it does not open a different label or create the missing label automatically. Select an existing configuration or explicitly create one with **New**. |
| No proxy settings appear for a label | Match the label's case and check that the store contains published `Warm:` or `Cold:` keys. New stores can use **New**; existing labels without `Warm:Sentinel` need that key under the same label before Update. |
| Update succeeds but proxy behavior is unchanged | Check the proxy's store and label, then the warm sentinel log or cold-setting restart requirement. See [App Configuration troubleshooting](../../docs/how-to/configure-app-configuration.md#troubleshoot-setting-changes). |

> [!WARNING]
> `BypassConfig=true` can load a stale snapshot from `data/appconfig-cache`; a missing cache falls back to Azure. **Update still writes to Azure.** Writes are made one key at a time, so a failed update can leave partial changes. Reload and inspect the stored values before retrying; do not assume a rollback occurred.