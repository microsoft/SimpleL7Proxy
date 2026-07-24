# App Configuration Not Loading

> **TL;DR**
> 1. Set `AZURE_APPCONFIG_ENDPOINT` (managed identity) or `AZURE_APPCONFIG_CONNECTION_STRING`.
> 2. Assign the **`App Configuration Data Reader`** role to the proxy identity.
> 3. Warm settings refresh in ~30 s after bumping `Warm:Sentinel`. Cold settings require a restart.

---

## How settings are loaded

The proxy loads from Azure App Configuration at startup and refreshes Warm settings continuously. The refresh trigger is the `Warm:Sentinel` key — whenever its value changes, all Warm-labeled keys are reloaded within approximately 30 seconds.

```
Startup:  read all keys (Warm:* + Cold:*) filtered by AZURE_APPCONFIG_LABEL
Runtime:  poll Warm:Sentinel every ~30s → if changed, reload all Warm:* keys
```

**Key format:** `{Warm|Cold}:{Section}:{SubSection}:{Name}`
- The `Warm:` or `Cold:` prefix determines reload behaviour (hot vs. restart required).
- The **label** is the environment name (e.g., `dev`, `prod`) — set via `AZURE_APPCONFIG_LABEL`.
- The proxy loads only keys whose label matches `AZURE_APPCONFIG_LABEL`.

Cold settings are **never** refreshed at runtime. To apply a Cold setting change: update the key in App Config and restart the proxy.

---

## Step 1 — Set the connection variable

Use **one** of the following:

| Method | Env Var | Notes |
|--------|---------|-------|
| Managed identity (recommended) | `AZURE_APPCONFIG_ENDPOINT=https://<name>.azconfig.io` | Requires RBAC role (see below) |
| Connection string | `AZURE_APPCONFIG_CONNECTION_STRING=<value>` | Simpler; stores credential in plain text |

If neither is set, the proxy reads settings from environment variables only.

---

## Step 2 — Assign the RBAC role (managed identity)

```bash
IDENTITY_ID=$(az containerapp show \
  --name <proxy-app-name> \
  --resource-group <rg> \
  --query identity.principalId -o tsv)

APPCONFIG_ID=$(az appconfig show \
  --name <appconfig-name> \
  --resource-group <rg> \
  --query id -o tsv)

az role assignment create \
  --role "App Configuration Data Reader" \
  --assignee $IDENTITY_ID \
  --scope $APPCONFIG_ID
```

> [!NOTE]
> The proxy only needs **Data Reader** access — it never writes to App Configuration.

---

## Step 3 — Verify key format

All keys use a `Warm:` or `Cold:` prefix followed by the section path. The **label** is the environment name — it must match `AZURE_APPCONFIG_LABEL`.

| Prefix | Reload behaviour |
|--------|------------------|
| `Warm:` | Reloaded within ~30 s of bumping `Warm:Sentinel` |
| `Cold:` | Loaded at startup only; requires restart to change |

**Correct key examples:**

| Key | Label | Value | Notes |
|-----|-------|-------|-------|
| `Cold:Logging:EventLoggers` | `dev` | `eventhub` | Cold — restart required |
| `Warm:CircuitBreaker:ErrorThreshold` | `dev` | `60` | Warm — hot reloaded |
| `Warm:Sentinel` | `dev` | `2` | Bump this value to trigger refresh |

---

## Symptom: settings loaded at startup but not refreshing

1. Bump `Warm:Sentinel` — change its value to anything different (with label matching `AZURE_APPCONFIG_LABEL`). Refresh happens within ~30 s.
2. Verify the key being changed has the `Warm:` prefix (not `Cold:`).
3. Check that `AZURE_APPCONFIG_ENDPOINT` or connection string is still valid.

## Symptom: proxy ignores App Configuration entirely

- If neither `AZURE_APPCONFIG_ENDPOINT` nor `AZURE_APPCONFIG_CONNECTION_STRING` is set, App Configuration is not used. The proxy reads only environment variables.
- If the managed identity does not have the `App Configuration Data Reader` role, the connection will fail at startup. Check logs for `[AppConfig]` entries.

## Symptom: Cold setting change not taking effect

Cold settings (keys prefixed `Cold:`) are not reloaded at runtime. Bumping `Warm:Sentinel` has no effect on them.

**Fix:** Update the App Config key and restart the proxy.

---

## Related

- [AZURE_APP_CONFIGURATION.md](../how-to/configure-app-configuration.md) — full App Config setup guide
- [CONFIGURATION_SETTINGS.md](../reference/configuration.md) — all settings with Warm/Cold classification
- [ENVIRONMENT_VARIABLES.md](../reference/environment-variables.md) — complete environment variable reference
