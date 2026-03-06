# Azure App Configuration Deployment

Provisions an Azure App Configuration store and populates it with the proxy's
**publishable** settings — both **Warm** (hot-reloaded) and **Cold**
(requires restart).  The running proxy watches a single sentinel key and
hot-reloads all warm settings when it changes — no restart required.
Cold settings are published so they can be centrally managed, but changing
them requires a Container App restart.

## Config Modes

Every `BackendOptions` property can be decorated with
`[ConfigOption("Category:Name")]`.  The `Mode` parameter controls how
the property is treated:

| Mode | Published to App Config? | Hot-reloaded? | Notes |
|---|---|---|---|
| **Warm** (default) | ✅ | ✅ | Value changes take effect within ~30 s |
| **Cold** | ✅ | ❌ | Requires a Container App restart |
| **Hidden** | ❌ | ❌ | Composite / derived at runtime — skipped by deploy.sh |

## Prerequisites

| Requirement | Details |
|---|---|
| **Azure CLI** | `az` ≥ 2.50 with the `containerapp` extension |
| **jq** | Used to parse the Container App JSON |
| **Azure login** | `az login` (the script will prompt if needed) |
| **A running Container App** | The script reads its env vars as the source of truth for warm values |
| **Bash 4+** | Uses associative arrays (`declare -A`) |

## Quick Start

```bash
cd deployment/AppConfiguration

# 1. Create your parameters file
cp deploy.parameters.example.sh deploy.parameters.sh

# 2. Edit deploy.parameters.sh with your values
#    (see Parameters section below)

# 3. Run
./deploy.sh
```

## Parameters

All parameters are set in `deploy.parameters.sh`.

| Parameter | Description |
|---|---|
| `CONTAINER_APP_NAME` | Name of the Container App whose env vars are the source of warm values |
| `CONTAINER_APP_RESOURCE_GROUP` | Resource group where the Container App lives |
| `RESOURCE_GROUP` | Resource group for the App Configuration store (created if missing) |
| `LOCATION` | Azure region for the App Configuration store |
| `APPCONFIG_NAME` | Name of the App Configuration store (created if missing) |
| `APPCONFIG_SKU` | `free` or `standard` |
| `APPCONFIG_LABEL` | Label applied to all `Warm:*` keys (empty string = null / no label) |
| `AZURE_APPCONFIG_REFRESH_SECONDS` | Refresh interval written to `Warm:RefreshSeconds` |
| `UPDATE_CONTAINER_APP_ENV` | `true` to push `AZURE_APPCONFIG_ENDPOINT`, `AZURE_APPCONFIG_LABEL`, and `AZURE_APPCONFIG_REFRESH_SECONDS` env vars onto the Container App |

> **Do not commit `deploy.parameters.sh`** — it contains environment-specific values.
> Only `deploy.parameters.example.sh` is checked in.

## What the Script Does

### 1. Read the live Container App

Queries the Container App deployment and loads every env var from
`containers[0].env` into memory.  Also discovers the container name
(needed for the optional env-var update at the end).

### 2. Ensure the App Configuration store exists

Creates the resource group and App Configuration store if they don't
already exist.

### 3. Assign RBAC (first run only)

Checks whether the signed-in Azure identity has the
**App Configuration Data Owner** role on the store.  If not, assigns it
and waits 30 seconds for propagation.

### 4. Discover config properties from source code

Parses `BackendOptions.cs` with `awk`, scanning for the `[ConfigOption]` attribute:

- **`[ConfigOption("Category:Name")]`** — marks a property as warm-reloadable
  (the default mode) and defines the key path under the `Warm:` prefix.
  The env var name defaults to the property name.
- **`[ConfigOption("Category:Name", ConfigName = "EnvVar")]`** — overrides
  the env var name when it differs from the property name (e.g.,
  `CONTAINER_APP_NAME` for the `ContainerApp` property).
- **`[ConfigOption("Category:Name", Mode = ConfigMode.Cold)]`** — the
  property is published to App Config but **not** hot-reloaded.  Changing
  the value requires a Container App restart.
- **`[ConfigOption("Category:Name", Mode = ConfigMode.Hidden)]`** — the
  property is **skipped by deploy.sh**.  Its runtime value is composite or
  derived (e.g., `IDStr` is built from a prefix + replicaID at startup).
- **`[ParsedConfig("SourceConfig")]`** — marks a non-publishable property
  whose default comes from a parsed composite config string (e.g.,
  `AsyncBlobStorageConfig`, `AsyncSBConfig`).

Each discovered property (with Mode ≠ Hidden) produces a quad:
`PropertyName | KeyPath | ConfigName | Mode`.

### 5. Resolve values and publish

For each publishable property (Warm or Cold):

1. **Container App env** — look up `ConfigName` in the env vars loaded in
   step 1.
2. **Local shell env** — if not found on the Container App, fall back to a
   local shell variable with the same name.
3. **Skip** — if neither has a value, the key is skipped.

Found values are written to the App Config store as `Warm:<KeyPath>`.
The output shows the source of each value (`container-app` or `local-env`).

### 6. Bump the sentinel

Writes `Warm:Sentinel` with the current UTC timestamp and
`Warm:RefreshSeconds` with the configured interval.  The proxy SDK watches
only `Warm:Sentinel` — when it changes, all `Warm:*` keys are reloaded as
a batch.

### 7. Update Container App env vars (optional)

If `UPDATE_CONTAINER_APP_ENV=true`, pushes three env vars onto the
Container App so the proxy knows where to connect:

- `AZURE_APPCONFIG_ENDPOINT`
- `AZURE_APPCONFIG_LABEL`
- `AZURE_APPCONFIG_REFRESH_SECONDS`

## Re-running

The script is idempotent.  Run it again any time you want to sync the
Container App's current env var values into App Configuration.  The
sentinel bump ensures the proxy picks up the new values on its next
refresh cycle.

## How the Proxy Consumes These Settings

The proxy's `AzureAppConfigurationRefreshService` (a `BackgroundService`):

1. Connects to the App Configuration endpoint using managed identity.
2. Selects all keys matching `Warm:*`.
3. Registers `Warm:Sentinel` as the refresh trigger (`refreshAll: true`).
4. Every `RefreshSeconds`, checks if the sentinel changed.
5. On change, reloads all `Warm:*` keys and applies **only Warm-mode**
   properties to `BackendOptions` via reflection using the `[ConfigOption]`
   attribute metadata.  Cold properties are present in the store but
   are **not** applied at runtime — they require a restart to take effect.
