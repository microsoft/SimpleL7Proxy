# POC: Securing APIM with Entra JWT Validation

**APIM rejects every request that does not carry a valid Entra JWT with the correct audience and an assigned role.**

## TL;DR (< 5 minutes)

1. Create an Entra app registration, add an `API.Caller` app role, assign it to your Container App's managed identity.
2. Add a `<validate-jwt>` inbound policy to APIM that checks `aud`, `iss`, and `roles`.
3. Call APIM with a bearer token — no token or wrong claims returns `401`; correct token returns `200`.

**Expected outcome:** `curl` without a token → `401`. `curl` with `az account get-access-token` → `200`.

## What you will observe

- Request with no `Authorization` header → APIM returns `401 Unauthorized` immediately (policy short-circuits, backend never called).
- Request with a token whose `aud` does not match `api://<APP_ID>` → `401`.
- Request with correct `aud`/`iss` but `roles` claim absent or wrong value → `401`.
- Request with correct `aud`, `iss`, and `roles: API.Caller` → `200 OK`, backend receives the request.
- After revoking the role assignment, existing tokens remain valid until they expire (token TTL is typically 1 hour).

## Flow

```
curl / Container App
  │  Authorization: Bearer <token>
  ▼
APIM inbound policy: <validate-jwt>
  ├─ aud ≠ api://<APP_ID>  ──► 401 (policy rejects, backend not called)
  ├─ iss ≠ expected tenant  ──► 401
  ├─ roles claim missing    ──► 401
  └─ all checks pass        ──► forward to backend ──► 200
```

> [!NOTE]
> `<validate-jwt>` runs entirely within APIM. The backend only sees requests that have already passed token validation.

## Setup

**What matters:** an Entra app registration with an app role, role assigned to the managed identity, and `appRoleAssignmentRequired=true`.

| Item | Value used in this POC |
| :--- | :--- |
| App role value | `API.Caller` |
| Token audience | `api://<APP_ID>` |
| APIM policy element | `<validate-jwt>` in inbound |
| Issuer URL pattern | `https://sts.windows.net/<TENANT_ID>/` |
| OpenID config URL pattern | `https://login.microsoftonline.com/<TENANT_ID>/v2.0/.well-known/openid-configuration` |

**Prerequisites:**

- Azure subscription with Contributor access
- `az` CLI logged in (`az login`)
- APIM instance deployed (Developer SKU is sufficient)
- Container App deployed with system-assigned managed identity and outbound HTTP allowed

### 1. Enable System-Assigned Managed Identity on the Container App

**The MI's `principalId` is the identity you will assign a role to — capture it before continuing.**

```bash
RG="my-rg"
CA_NAME="my-containerapp"

az containerapp identity assign \
  -g "$RG" -n "$CA_NAME" \
  --system-assigned
```

Capture the managed identity's object ID:

```bash
CA_PRINCIPAL_ID=$(az containerapp show \
  -g "$RG" -n "$CA_NAME" \
  --query "identity.principalId" -o tsv)
echo "CA_PRINCIPAL_ID=$CA_PRINCIPAL_ID"
```

### 2. Create the Entra App Registration for the Protected API

**This registration is the "resource" — its `appId` becomes the token audience (`api://<APP_ID>`) and the app role controls who can get a token for it.**

```bash
APP_NAME="apim-protected-api-poc"

# 1. Create the app registration
APP_ID=$(az ad app create --display-name "$APP_NAME" --query "appId" -o tsv)
echo "APP_ID=$APP_ID"

# 2. Create the service principal
az ad sp create --id "$APP_ID" 1>/dev/null

# 3. Set the identifier URI (audience)
az ad app update --id "$APP_ID" --identifier-uris "api://$APP_ID"

# 4. Add an app role
ROLE_ID=$(python3 -c "import uuid; print(uuid.uuid4())")
az ad app update --id "$APP_ID" --app-roles "[
  {
    \"allowedMemberTypes\": [\"User\", \"Application\"],
    \"description\": \"Caller\",
    \"displayName\": \"Caller\",
    \"id\": \"$ROLE_ID\",
    \"isEnabled\": true,
    \"origin\": \"Application\",
    \"value\": \"API.Caller\"
  }
]"

# 5. Require role assignment
az ad sp update --id "$APP_ID" --set appRoleAssignmentRequired=true
```

> [!NOTE]
> For delegated user access (OAuth2 scopes), add a scope via **App registrations → [API App] → Expose an API → Add a scope**. App roles alone are sufficient for this POC.

### 3. Assign the App Role to the Managed Identity

**`appRoleAssignmentRequired=true` ensures only explicitly assigned identities receive the role in their tokens — without it, any user in the tenant can get a token.**

Get the service principal object ID of the protected API:

```bash
API_SP_OBJECT_ID=$(az ad sp show --id "$APP_ID" --query "id" -o tsv)
echo "API_SP_OBJECT_ID=$API_SP_OBJECT_ID"
```

Assign the role (PowerShell, using the AzureAD module):

```powershell
$AssigneeObjectId = "<CA_PRINCIPAL_ID>"   # Managed Identity principalId
$ResourceObjectId = "<API_SP_OBJECT_ID>"  # Service principal objectId of the API app
$AppRoleId        = "<ROLE_ID>"           # App role GUID from Step 2

Connect-AzureAD

New-AzureADServiceAppRoleAssignment `
  -ObjectId    $AssigneeObjectId `
  -PrincipalId $AssigneeObjectId `
  -ResourceId  $ResourceObjectId `
  -Id          $AppRoleId
```

> [!TIP]
> You can also assign the role via the Azure portal: **Enterprise Applications → [API App] → Users and groups → Add assignment**.

### 4. Apply the APIM JWT Validation Policy

**The `<validate-jwt>` element is the only enforcement gate — remove it and the endpoint is open to anyone.**

Apply to your APIM API under **All operations → Inbound processing**:

```xml
<inbound>
  <base />
  <validate-jwt
    header-name="Authorization"
    failed-validation-httpcode="401"
    failed-validation-error-message="Unauthorized: invalid or missing token">
    <openid-config url="https://login.microsoftonline.com/<TENANT_ID>/v2.0/.well-known/openid-configuration" />
    <audiences>
      <audience>api://<APP_ID></audience>
      <audience><APP_ID></audience>
    </audiences>
    <issuers>
      <issuer>https://sts.windows.net/<TENANT_ID>/</issuer>
    </issuers>
    <required-claims>
      <claim name="roles" match="any">
        <value>API.Caller</value>
      </claim>
    </required-claims>
  </validate-jwt>
</inbound>
```

Replace `<TENANT_ID>` and `<APP_ID>` with the values from the previous steps.

## Run

```bash
# Set these once
APIM_URL="https://<your-apim-name>.azure-api.net/<your-api-route>"

# 1. Should return 401
curl -i "$APIM_URL"

# 2. Acquire token and call — should return 200
TOKEN=$(az account get-access-token \
  --resource "api://$APP_ID" \
  --query accessToken -o tsv)

curl -i "$APIM_URL" \
  -H "Authorization: Bearer $TOKEN"
```

> [!TIP]
> Paste the token into [jwt.io](https://jwt.io) and confirm `aud = api://<APP_ID>` and `roles` contains `"API.Caller"`.

## Optional: Calling from the Proxy (Managed Identity)

**The proxy attaches an MI token automatically when `usemi=true` and `audience` are set in the backend host connection string — no code required.**

```bash
# In your proxy environment / App Configuration
Host1="host=https://<your-apim-name>.azure-api.net;usemi=true;audience=api://<APP_ID>;probe=/health"
```

What happens at runtime:
- The proxy acquires an Entra token for `api://<APP_ID>` using its managed identity.
- Every request forwarded to that backend includes `Authorization: Bearer <token>`.
- The token is refreshed automatically before expiry.

> [!NOTE]
> `usemi` and `useoauth` are aliases — either key works. The proxy's MI must have the `API.Caller` role assigned (Step 3 above).

> [!WARNING]
> `audience` is required when `usemi=true`. Omitting it means the proxy acquires no token and the backend receives an unauthenticated request.

## Verify

Run each check in order. All five must pass.

- [ ] `curl $APIM_URL` (no header) → response is `HTTP/1.1 401`, body contains `"Unauthorized: invalid or missing token"`
- [ ] `curl $APIM_URL -H "Authorization: Bearer badtoken"` → `401`
- [ ] Acquire a valid token but for a **different** resource (`az account get-access-token --resource https://management.azure.com`) → `401`
- [ ] Acquire the correct token (`az account get-access-token --resource "api://$APP_ID"`) → `200 OK`
- [ ] Decode the `200` token at [jwt.io](https://jwt.io) and confirm: `aud = api://<APP_ID>`, `iss = https://sts.windows.net/<TENANT_ID>/`, `roles` array contains `"API.Caller"`

> [!TIP]
> `az account get-access-token --resource "api://$APP_ID" --query accessToken -o tsv | pbcopy` puts the token straight on the clipboard for jwt.io.

## Troubleshooting

| Symptom | Likely cause | Check |
| :--- | :--- | :--- |
| `401` even with a token that looks correct | `aud` mismatch — token issued for wrong resource | Decode token at jwt.io; `aud` must be `api://<APP_ID>` not `APP_ID` alone |
| `401` with correct `aud` | `roles` claim missing | Confirm role assignment exists: `az rest --method GET --url "https://graph.microsoft.com/v1.0/servicePrincipals/<API_SP_OBJECT_ID>/appRoleAssignedTo"` |
| `401` from Container App but `200` from `az` CLI | MI not assigned the role | Check `CA_PRINCIPAL_ID` matches the MI object ID: `az containerapp show -g $RG -n $CA_NAME --query "identity.principalId"` |
| `401` with `"IDX10511: Signature validation failed"` | Issuer URL mismatch | APIM policy `<issuer>` must match the `iss` claim exactly — copy from jwt.io decode |
| Role assignment succeeds but token still lacks `roles` | `appRoleAssignmentRequired` not set | Run `az ad sp update --id "$APP_ID" --set appRoleAssignmentRequired=true` |



