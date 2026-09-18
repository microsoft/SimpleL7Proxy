### Step 1 — Set Variables

Set `APP_NAME`, `CONTAINER_APP_NAME`, `RG` to match your environment.

```bash
export ENTRA_APP_NAME="aca-proxy"            # Display name for the Entra app registration
export CONTAINER_APP_NAME="<your-app-name>"  # Container App name
export RG="<your-resource-group>"            # Container App resource group
```

> [!NOTE]
> In Windows/WSL environments, sanitize `-o tsv` outputs with `tr -d '\r\n'` before reusing values in later CLI calls.

### Step 2 — Create the Entra App Registration and enable EasyAuth

Save the generated `APP_ID` and `CLIENT_SECRET` variables for troubleshooting.

```bash

# Lookup tenant and app fqdn
export TENANT_ID="$(az account show --query tenantId -o tsv | tr -d '\r\n')"
export APP_FQDN="https://$(az containerapp show --name "$CONTAINER_APP_NAME" --resource-group "$RG" --query properties.configuration.ingress.fqdn -o tsv | tr -d '\r\n')"
export HEALTH_URL="$APP_FQDN/health"

export APP_ID=$(az ad app create \
  --display-name "$ENTRA_APP_NAME" \
  --sign-in-audience AzureADMyOrg \
  --query appId -o tsv | tr -d '\r\n')
echo "APP_ID=$APP_ID"

# Required so az token requests to api://$APP_ID can resolve the resource principal.
az ad app update --id "$APP_ID" --identifier-uris "api://$APP_ID"

# Create service principal
az ad sp create --id "$APP_ID" 1>/dev/null

# Create delegated scope
if [ -z "$APP_ID" ]; then
  echo "APP_ID is empty. Re-run Step 2 app creation or app lookup first."
  exit 1
fi

SCOPE_ID="$(uuidgen | tr '[:upper:]' '[:lower:]')"
API_OBJ="$(az ad app show --id "$APP_ID" --query api -o json)"
UPDATED_API_OBJ="$(echo "$API_OBJ" | jq --arg id "$SCOPE_ID" '.oauth2PermissionScopes = [{
  adminConsentDescription: "Access the API",
  adminConsentDisplayName: "Admin Access",
  id: $id,
  isEnabled: true,
  type: "Admin",
  userConsentDescription: "Access the API",
  userConsentDisplayName: "User Access",
  value: "api.access"
}]')"
az ad app update --id "$APP_ID" --set api="$UPDATED_API_OBJ"

# Enable ID token issuance
az ad app update --id "$APP_ID" --enable-id-token-issuance true

# Create client secret
export CLIENT_SECRET=$(az ad app credential reset \
  --id "$APP_ID" \
  --display-name "proxy-auth-secret" \
  --end-date "$(date -d '+30 days' '+%Y-%m-%d')" \
  --query password -o tsv | tr -d '\r\n')

# Do not print or commit secret values. Keep them in memory only.


# Enable EazyAuth
az containerapp auth microsoft update \
  --name "$CONTAINER_APP_NAME" \
  --resource-group "$RG" \
  --client-id "$APP_ID" \
  --client-secret "$CLIENT_SECRET" \
  --tenant-id "$TENANT_ID" \
  --yes

# Verify identifier URI is set correctly.
az ad app show --id "$APP_ID" --query "{appId:appId,identifierUris:identifierUris,scopes:api.oauth2PermissionScopes[].value}" -o table

# Optional hygiene: clear secret from shell after auth configuration is complete.
# unset CLIENT_SECRET
```

> [!WARNING]
> You may need to grant admin consent in the Azure portal before token acquisition works.
> If `az account get-access-token --resource "api://$APP_ID"` returns `AADSTS65001` (`consent_required`), ask a tenant admin to grant consent for your client app/API scope in Entra ID:
> **App registrations** -> your client app -> **API permissions** -> **Grant admin consent**.
>
> For better secret hygiene, avoid sharing terminal output that includes auth commands and never paste secret values into tickets, PR comments, or chat logs.

### Step 3 — Verify Container App

Run these checks to ensure auth is enabled and the Microsoft identity provider is registered.

```bash
ENABLED="$(az containerapp auth show \
  --name "$CONTAINER_APP_NAME" \
  --resource-group "$RG" \
  --query "platform.enabled" -o tsv | tr -d '\r\n')"

AAD_CLIENT_ID="$(az containerapp auth show \
  --name "$CONTAINER_APP_NAME" \
  --resource-group "$RG" \
  --query "identityProviders.azureActiveDirectory.registration.clientId" -o tsv | tr -d '\r\n')"

AUDIENCE="$(az containerapp auth show \
  --name "$CONTAINER_APP_NAME" \
  --resource-group "$RG" \
  --query "identityProviders.azureActiveDirectory.validation.allowedAudiences[0]" -o tsv | tr -d '\r\n')"

echo "enabled=$ENABLED"
echo "aad_client_id=$AAD_CLIENT_ID"
echo "allowed_audience=$AUDIENCE"
```

Expected:

- enabled=true
- aad_client_id=<guid>
- allowed_audience=api://<same-guid-or-intended-api-uri>

If `aad_client_id` or `allowed_audience` is empty, or `enabled` is not `true`, re-run the auth microsoft update command above and do not continue to Step 4.

### Step 4 — Set the Unauthenticated Action

Rejects unauthenticated requests outright — callers get a `401` with no redirect.

```bash
az containerapp auth update \
  --name "$CONTAINER_APP_NAME" \
  --resource-group "$RG" \
  --enabled true \
  --unauthenticated-client-action Return401
```

### Step 5 — Align Allowed Audiences with v2.0 Tokens

The default Microsoft provider configuration registers `api://<APP_ID>` as the allowed audience. Entra v2.0 access tokens (issued by `https://login.microsoftonline.com/<tenant>/v2.0`) carry the **bare GUID** in their `aud` claim — not the `api://` form. Without this step, valid tokens are rejected with `403`.

Replace the allowed audience with the bare GUID:

```bash
az containerapp auth microsoft update \
  --name "$CONTAINER_APP_NAME" \
  --resource-group "$RG" \
  --allowed-audiences "$APP_ID"
```

Verify:

```bash
az containerapp auth show \
  --name "$CONTAINER_APP_NAME" \
  --resource-group "$RG" \
  --query "identityProviders.azureActiveDirectory.validation.allowedAudiences"
```

Expected output:

```json
[
  "<APP_ID>"
]
```

> [!NOTE]
> `--allowed-audiences` **replaces** the existing list and only accepts one value per invocation. If you also need to accept v1-style `api://<APP_ID>` audiences (for legacy callers), patch the list directly via `az containerapp auth update --set identityProviders.azureActiveDirectory.validation.allowedAudiences=...` with a JSON array, taking care with shell quoting.

### Step 6 — Restrict to Trusted Client Applications

Even with a valid token, EasyAuth's `defaultAuthorizationPolicy.allowedApplications` decides which client apps may call the proxy. An empty list combined with EasyAuth's MISE evaluation results in `403` with `"this principal does not match any of the allowed applications"`.

For this POC we explicitly trust the Microsoft Azure CLI client (`04b07795-8ddb-461a-bbee-02f9e1bf7b46`) so you can verify with `az account get-access-token`. In production, replace this with the client app IDs of the real callers (your console SP, APIM, another service, etc.).

```bash
az containerapp auth update \
  --name "$CONTAINER_APP_NAME" \
  --resource-group "$RG" \
  --set identityProviders.azureActiveDirectory.validation.defaultAuthorizationPolicy.allowedApplications='["04b07795-8ddb-461a-bbee-02f9e1bf7b46"]'
```

Verify:

```bash
az containerapp auth show \
  --name "$CONTAINER_APP_NAME" \
  --resource-group "$RG" \
  --query "identityProviders.azureActiveDirectory.validation.defaultAuthorizationPolicy.allowedApplications"
```

> [!NOTE]
> EasyAuth caches authorization decisions per principal for roughly 60 seconds. After changing `allowedApplications` or `allowedAudiences`, wait a minute (or acquire a fresh token) before re-testing — otherwise you'll see a cached deny.
