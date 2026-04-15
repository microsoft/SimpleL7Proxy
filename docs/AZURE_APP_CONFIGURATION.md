# Azure App Configuration Integration

Azure App Configuration lets you change **Warm** settings across all proxy instances in ~30 seconds without a restart.

> **TL;DR**
> - **Warm settings** are hot-reloaded; **Cold settings** require a restart — both live under the `Warm:*` key prefix, distinguished by their label.
> - **Changing any setting takes effect only after you update `Warm:Sentinel`** — that is the refresh trigger.
> - **Managed Identity is the recommended auth method**; connection strings work for local development.

---

## Reference — Configuration Types

| Type | Label in App Config | Reload | Example settings |
|------|---------------------|--------|-----------------|
| **Warm** | *(none)* or `APPCONFIG_LABEL` | ~30 s, no restart | `MaxAttempts`, `DefaultPriority`, `DefaultTTLSecs` |
| **Cold** | `Cold` | Restart required | `Port`, `Workers`, `Timeout`, `PollInterval` |
| **Hidden** | — (not published) | Runtime-derived | `AsyncBlobStorageConnectionString` |

All keys share the `Warm:` prefix (single `Select("Warm:*")` query). The **Label** column in Azure Portal's Configuration Explorer immediately shows which settings require a restart.

---

## Reference — Environment Variables

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `AZURE_APPCONFIG_ENDPOINT` | One of these two | — | Managed Identity endpoint (recommended) |
| `AZURE_APPCONFIG_CONNECTION_STRING` | One of these two | — | Connection string (dev/fallback) |
| `AZURE_APPCONFIG_LABEL` | No | *(none)* | Label filter for Warm settings |
| `AZURE_APPCONFIG_REFRESH_SECONDS` | No | `30` | Sentinel poll interval in seconds |

---

## How Refresh Works

```
Every AZURE_APPCONFIG_REFRESH_SECONDS
        │
        ▼
  Check Warm:Sentinel ──changed?──Yes──► Reload ALL Warm settings → apply live
                       ──No──────────► Nothing happens (no extra API calls)
```

**Update `Warm:Sentinel` to any new value to push a config change to all running instances.**

---

## Setting Up

**Rule: Create the resource, assign the role, import your settings, then set the environment variable on the Container App.**

### 1. Create the resource

```bash
az group create --name rg-proxy --location eastus
az appconfig create \
  --name appconfig-proxy \
  --resource-group rg-proxy \
  --location eastus \
  --sku Standard
```

### 2. Assign Managed Identity access

```bash
IDENTITY_ID=$(az containerapp show \
  --name your-proxy-app \
  --resource-group rg-proxy \
  --query identity.principalId -o tsv)

APPCONFIG_ID=$(az appconfig show \
  --name appconfig-proxy \
  --resource-group rg-proxy \
  --query id -o tsv)

az role assignment create \
  --role "App Configuration Data Reader" \
  --assignee $IDENTITY_ID \
  --scope $APPCONFIG_ID
```

> [!NOTE]
> **Default role:** `App Configuration Data Reader` is sufficient — the proxy only reads settings, never writes them.

> [!TIP]
> **Troubleshooting:** If the proxy logs authentication failures, confirm the Container App's system-assigned managed identity is enabled and the role assignment has propagated (can take a few minutes).

### 3. Import initial settings

```json
{
  "Warm:MaxAttempts": 3,
  "Warm:DefaultPriority": 5,
  "Warm:LogAllRequestHeaders": false,
  "Warm:AcceptableStatusCodes": "[200, 201, 202]",
  "Warm:Sentinel": "1"
}
```

```bash
az appconfig kv import \
  --name appconfig-proxy \
  --source file \
  --path warm-settings.json \
  --format json \
  --label Production
```

### 4. Configure the Container App

```bash
az containerapp update \
  --name your-proxy-app \
  --resource-group rg-proxy \
  --set-env-vars \
    AZURE_APPCONFIG_ENDPOINT=https://appconfig-proxy.azconfig.io \
    AZURE_APPCONFIG_LABEL=Production \
    AZURE_APPCONFIG_REFRESH_SECONDS=30
```

> [!WARNING]
> **Error:** If `AZURE_APPCONFIG_ENDPOINT` is set but the managed identity has no role assignment, the proxy will fail to start. Set `AZURE_APPCONFIG_CONNECTION_STRING` as a fallback during initial setup.

---

## Per-Request Override

**Rule: To change a Warm setting at runtime, update the key value then bump `Warm:Sentinel` — both steps are required.**

```bash
# 1. Update the setting
az appconfig kv set \
  --name appconfig-proxy \
  --key "Warm:MaxAttempts" \
  --value "5" \
  --label Production

# 2. Trigger refresh
az appconfig kv set \
  --name appconfig-proxy \
  --key "Warm:Sentinel" \
  --value "$(date +%s)" \
  --label Production
```

> [!NOTE]
> All instances pick up the change within `AZURE_APPCONFIG_REFRESH_SECONDS` (default 30 s) — no rolling restart needed.

---

## Available Warm Settings

| Category | Settings |
|----------|----------|
| **Logging** | `LogAllRequestHeaders`, `LogAllRequestHeadersExcept`, `LogAllResponseHeaders`, `LogAllResponseHeadersExcept`, `LogHeaders` |
| **Request processing** | `MaxAttempts`, `DefaultPriority`, `DefaultTTLSecs`, `TimeoutHeader`, `TTLHeader` |
| **Validation** | `RequiredHeaders`, `DisallowedHeaders`, `ValidateHeaders`, `ValidateAuthAppID`, `ValidateAuthAppIDUrl`, `ValidateAuthAppIDHeader`, `ValidateAuthAppFieldName` |
| **User management** | `UserConfigUrl`, `SuspendedUserConfigUrl`, `UserProfileHeader`, `UserIDFieldName`, `UniqueUserHeaders`, `UserPriorityThreshold` |
| **Priority** | `PriorityKeyHeader`, `PriorityKeys`, `PriorityValues` |
| **Response** | `AcceptableStatusCodes`, `StripResponseHeaders`, `StripRequestHeaders` |
| **Async (timing)** | `AsyncTimeout`, `AsyncTTLSecs`, `AsyncTriggerTimeout`, `AsyncClientRequestHeader`, `AsyncClientConfigFieldName` |

---

## Worked Example

> **Goal:** Raise `MaxAttempts` from 3 to 5 on a live deployment without restarting.

| Step | Command | Expected result |
|------|---------|----------------|
| Check current value | `az appconfig kv show --name appconfig-proxy --key "Warm:MaxAttempts" --label Production` | `"value": "3"` |
| Update setting | `az appconfig kv set ... --key "Warm:MaxAttempts" --value "5"` | Setting saved |
| Bump sentinel | `az appconfig kv set ... --key "Warm:Sentinel" --value "$(date +%s)"` | Refresh triggered |
| Wait ≤30 s | — | Proxy logs: `✓ Warm settings applied successfully` |
| Verify | Send a request that exercises retries | Proxy now retries up to 5 times |

**No deployment or restart is needed — the sentinel bump propagates to every running instance within the poll interval.**

---

## Monitoring

The proxy emits these log entries around refresh:

```
[CONFIG] ✓ Azure App Configuration initialized with Warm settings refresh
[CONFIG] Azure App Configuration refresh service started with 30s interval
[CONFIG] Configuration refresh check completed - changes detected
[CONFIG] Warm settings changed - applying to BackendOptions
[CONFIG] ✓ Warm settings applied successfully
```

> [!TIP]
> **Troubleshooting:** If `changes detected` never appears after a sentinel update, verify the label filter (`AZURE_APPCONFIG_LABEL`) matches the label used when importing the keys.

---

## Related Documentation

- [CONFIGURATION_SETTINGS.md](CONFIGURATION_SETTINGS.md) — Full list of all settings and their reload types
- [ENVIRONMENT_VARIABLES.md](ENVIRONMENT_VARIABLES.md) — All environment variables
- [DEVELOPMENT.md](DEVELOPMENT.md) — Local development setup
