# POC: Securing the Proxy with Container Apps EasyAuth

**Protect the proxy from unauthorized access.**

## TL;DR (< 5 minutes)

1. Register an Entra app, create a client secret, and enable ID token issuance.
2. Enable EasyAuth on the Container App and set the unauthenticated action to `Return401`.
3. Acquire a bearer token scoped to `api://<APP_ID>` and include it in the `Authorization: Bearer` header.

**Expected outcome:** `curl` without a token → `401`. `curl` with a valid token → request reaches the proxy and returns `200`.

> EasyAuth rejects any request without a valid Entra token before it reaches the proxy.

## What you will observe

- `curl https://<proxy>/` with no `Authorization` header → `401 Unauthorized`; the proxy never processes the request.
- `curl` with a token for the wrong audience → `401`.
- `curl` with a valid bearer token scoped to `api://<APP_ID>` → request reaches the proxy, proxy returns its normal response.
- The proxy receives no unauthenticated traffic at any point.

## Flow

```
API client / service
   │
   │  Authorization: Bearer <token>
   ▼
ACA EasyAuth sidecar
   ├─ No token        ──► 401
   ├─ Wrong audience  ──► 401
   └─ Valid token
        ▼
   SimpleL7Proxy
   (receives only authenticated requests)
```

> [!NOTE]
> EasyAuth runs as a platform sidecar managed by Azure Container Apps and only forwards validated requests.

## Setup

**Prerequisites:**

- Azure subscription with Contributor access
- `az` CLI authenticated (`az login`)
- Container App deployed (the proxy)
- Tenant ID: `az account show --query tenantId -o tsv`

### Step 1 — Set Variables

Set `APP_NAME`, `CONTAINER_APP_NAME`, `RG` to match your environment.

```bash
export ENTRA_APP_NAME="aca-proxy"                  # display name for the Entra app registration
export CONTAINER_APP_NAME="<your-app-name>"  # your Container App name
export RG="<your-resource-group>"            # your resource group
```

> [!NOTE]
> In Windows/WSL environments, sanitize `-o tsv` outputs with `tr -d '\r\n'` before reusing values in later CLI calls.

### Step 2 — Create the Entra App Registration and enable EasyAuth

Save the generated `APP_ID` and `CLIENT_SECRET` variables for troubleshooting.

```bash

# Lookup 
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

## Verify Access

```bash
# 1. No token — expect 401
curl -i "$HEALTH_URL"

# 2. Acquire a token scoped to the proxy's app registration
TOKEN=$(az account get-access-token \
  --resource "api://$APP_ID" \
  --query accessToken -o tsv | tr -d '\r\n')

# 3. Call with token — expect 200
curl -i "$HEALTH_URL" \
  -H "Authorization: Bearer $TOKEN"

# Wrong audience (valid Azure token, wrong resource) — expect 401
# Uses the Azure management API as the resource to produce a real Entra-signed token
# whose 'aud' claim does not match api://$APP_ID — EasyAuth will reject it due to the mismatch.
BAD_TOKEN=$(az account get-access-token --resource "https://management.azure.com/" --query accessToken -o tsv | tr -d '\r\n')
curl -i "$HEALTH_URL" -H "Authorization: Bearer $BAD_TOKEN"

```

## Remove

Use this to temporarily disable auth — for example, to isolate whether a problem is in EasyAuth or in the proxy itself.

> [!WARNING]
> Disabling auth exposes the app endpoint to unauthenticated traffic. Use this only for short-lived troubleshooting in non-production environments, and re-enable auth immediately after validation.

```bash
az containerapp auth update \
  --name "$CONTAINER_APP_NAME" \
  --resource-group "$RG" \
  --enabled false
```

All traffic is accepted again immediately. The app registration and secret are left in place. To restore protection:

```bash
az containerapp auth update \
  --name "$CONTAINER_APP_NAME" \
  --resource-group "$RG" \
  --enabled true
```

## Troubleshooting

| Symptom | Cause | Check |
| :--- | :--- | :--- |
| `401` on all requests including valid tokens | Wrong token audience | Decode at [jwt.ms](https://jwt.ms); `aud` claim must be `api://<APP_ID>` |
| `503` on all traffic after Step 3 | Auth enabled but no identity provider registered | `az containerapp auth show -n <name> -g <rg> --query identityProviders` — if empty, re-run Step 3. To restore immediately: `az containerapp auth update -n <name> -g <rg> --enabled false` |
| Authentication fails with `AADSTS50019` or similar | ID token issuance not enabled | `az ad app update --id $APP_ID --enable-id-token-issuance true` |
| `401` despite a valid Entra token | Auth not fully enabled or provider missing | `az containerapp auth show -n <name> -g <rg>` — confirm `"enabled": true` and provider is configured |
| Client secret rejected at authentication | Secret expired or rotated | `az ad app credential reset --id $APP_ID --display-name "proxy-auth-secret"` then re-run Step 2 |
| `AADSTS65001 consent_required` when requesting token | Delegated consent not granted for client -> API scope | Run Step 2a to grant consent, then retry token request |


