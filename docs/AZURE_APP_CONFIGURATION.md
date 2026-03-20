# Azure App Configuration Integration

This document describes how to use Azure App Configuration for hot-reloading **[Warm]** settings without restarting the proxy.

## Overview

The proxy supports three types of configuration settings:

| Type | Behavior | Label in App Config | Example |
|------|----------|--------------------|---------|
| **Warm** | Hot-reloaded from Azure App Config | *(none)* or `APPCONFIG_LABEL` | `MaxAttempts`, `LogConsole`, `DefaultPriority` |
| **Cold** | Read at startup, requires restart | `Cold` | `PollInterval`, `Timeout`, `Workers` |
| **Hidden** | Not published | — | `AsyncBlobStorageConnectionString` (parsed at runtime) |

Both Warm and Cold settings are stored under the `Warm:` key prefix (for a single `Select("Warm:*")` query), but are distinguished by their **label**:
- Warm settings use the deployment label (default: no label)
- Cold settings always use label `Cold`

This makes it easy to identify which settings require a restart when browsing the Azure portal's Configuration Explorer — just look at the **Label** column.

## Environment Variables

Configure the connection to Azure App Configuration:

```bash
# Option 1: Managed Identity (recommended for production)
AZURE_APPCONFIG_ENDPOINT=https://your-appconfig.azconfig.io

# Option 2: Connection String (for development)
AZURE_APPCONFIG_CONNECTION_STRING=Endpoint=https://...;Id=...;Secret=...

# Optional: Label filter (default: no label)
AZURE_APPCONFIG_LABEL=Production

# Optional: Refresh interval in seconds (default: 30)
AZURE_APPCONFIG_REFRESH_SECONDS=30
```

## Azure App Configuration Key Structure

All settings are stored under the `Warm:` key prefix. **Labels** distinguish the reload mode:

```
# Warm settings (label = none or APPCONFIG_LABEL) — hot-reloaded
Warm:MaxAttempts = 3
Warm:LogConsole = true
Warm:DefaultPriority = 5
Warm:Sentinel = 1  # Change this to trigger refresh

# Cold settings (label = Cold) — read at startup only
Warm:Server:Port = 8000
Warm:Server:Workers = 100
Warm:Server:Timeout = 100000
```

### Sentinel Key Pattern

The `Warm:Sentinel` key is used to trigger configuration refresh:

1. The refresh service polls Azure App Configuration every N seconds
2. It only checks if `Warm:Sentinel` has changed
3. If changed, **all** Warm settings are reloaded
4. This minimizes API calls while allowing instant updates

**To trigger a refresh:** Update `Warm:Sentinel` to any new value (e.g., increment a counter or use a timestamp).

## Setting Up Azure App Configuration

### 1. Create the Resource

```bash
# Create resource group
az group create --name rg-proxy --location eastus

# Create App Configuration store
az appconfig create \
  --name appconfig-proxy \
  --resource-group rg-proxy \
  --location eastus \
  --sku Standard
```

### 2. Assign Managed Identity Access

```bash
# Get the Container App's managed identity
IDENTITY_ID=$(az containerapp show \
  --name your-proxy-app \
  --resource-group rg-proxy \
  --query identity.principalId -o tsv)

# Get App Configuration resource ID
APPCONFIG_ID=$(az appconfig show \
  --name appconfig-proxy \
  --resource-group rg-proxy \
  --query id -o tsv)

# Assign App Configuration Data Reader role
az role assignment create \
  --role "App Configuration Data Reader" \
  --assignee $IDENTITY_ID \
  --scope $APPCONFIG_ID
```

### 3. Import Initial Settings

Create a JSON file with your warm settings:

```json
{
  "Warm:MaxAttempts": 3,
  "Warm:DefaultPriority": 5,
  "Warm:LogConsole": true,
  "Warm:LogProbes": false,
  "Warm:AcceptableStatusCodes": "[200, 201, 202]",
  "Warm:Sentinel": "1"
}
```

Import to App Configuration:

```bash
az appconfig kv import \
  --name appconfig-proxy \
  --source file \
  --path warm-settings.json \
  --format json \
  --label Production
```

### 4. Update Container App Environment

```bash
az containerapp update \
  --name your-proxy-app \
  --resource-group rg-proxy \
  --set-env-vars \
    AZURE_APPCONFIG_ENDPOINT=https://appconfig-proxy.azconfig.io \
    AZURE_APPCONFIG_LABEL=Production \
    AZURE_APPCONFIG_REFRESH_SECONDS=30
```

## Available Warm Settings

These settings can be hot-reloaded:

### Logging
- `LogConsole` - Enable console logging
- `LogConsoleEvent` - Enable console event logging  
- `LogPoller` - Log poller activity
- `LogProbes` - Log health probe activity
- `LogHeaders` - Headers to include in logs
- `LogAllRequestHeaders` - Log all request headers
- `LogAllRequestHeadersExcept` - Exclude specific request headers
- `LogAllResponseHeaders` - Log all response headers
- `LogAllResponseHeadersExcept` - Exclude specific response headers

### Request Processing
- `MaxAttempts` - Maximum retry attempts
- `DefaultPriority` - Default request priority
- `DefaultTTLSecs` - Default time-to-live
- `TimeoutHeader` - Header containing timeout value
- `TTLHeader` - Header containing TTL value

### Validation
- `RequiredHeaders` - Headers that must be present
- `DisallowedHeaders` - Headers that are not allowed
- `ValidateHeaders` - Header validation rules
- `ValidateAuthAppID` - Enable app ID validation
- `ValidateAuthAppIDUrl` - URL for app ID validation
- `ValidateAuthAppFieldName` - Field name for app ID
- `ValidateAuthAppIDHeader` - Header containing app ID

### User Management
- `UserConfigUrl` - URL for user configuration
- `SuspendedUserConfigUrl` - URL for suspended users
- `UserProfileHeader` - Header containing user profile
- `UserIDFieldName` - Field name for user ID
- `UniqueUserHeaders` - Headers that identify unique users
- `UserPriorityThreshold` - Priority threshold for users

### Priority
- `PriorityKeyHeader` - Header containing priority key
- `PriorityKeys` - List of priority keys
- `PriorityValues` - Priority values for each key

### Response Handling
- `AcceptableStatusCodes` - Status codes to accept
- `StripResponseHeaders` - Headers to remove from response
- `StripRequestHeaders` - Headers to remove from request

### Async Settings (timing only)
- `AsyncTimeout` - Async operation timeout
- `AsyncTTLSecs` - Async TTL in seconds
- `AsyncTriggerTimeout` - Trigger timeout
- `AsyncClientRequestHeader` - Client async header
- `AsyncClientConfigFieldName` - Config field name

## Monitoring Refresh

The proxy logs configuration refresh activity:

```
[CONFIG] ✓ Azure App Configuration initialized with Warm settings refresh
[CONFIG] Azure App Configuration refresh service started with 30s interval
[CONFIG] Configuration refresh check completed - changes detected
[CONFIG] Warm settings changed - applying to BackendOptions
[CONFIG] ✓ Warm settings applied successfully
```

## Troubleshooting

### Settings Not Refreshing

1. Check the sentinel key was updated:
   ```bash
   az appconfig kv show --name appconfig-proxy --key "Warm:Sentinel"
   ```

2. Verify the label filter matches:
   ```bash
   az appconfig kv list --name appconfig-proxy --label Production
   ```

3. Check proxy logs for refresh errors

### Authentication Failures

1. Verify managed identity is enabled on the Container App
2. Check role assignment is correct (App Configuration Data Reader)
3. Ensure the endpoint URL is correct

### Performance Considerations

- Default refresh interval is 30 seconds
- Only the sentinel key is checked on each poll
- Full refresh only occurs when sentinel changes
- Consider longer intervals for production (60-120 seconds)

## Example: Changing MaxAttempts at Runtime

```bash
# Update the setting
az appconfig kv set \
  --name appconfig-proxy \
  --key "Warm:MaxAttempts" \
  --value "5" \
  --label Production

# Trigger refresh by updating sentinel
az appconfig kv set \
  --name appconfig-proxy \
  --key "Warm:Sentinel" \
  --value "$(date +%s)" \
  --label Production
```

Within 30 seconds (or your configured interval), all proxy instances will pick up the new value without restart.
