# POC: Securing the Proxy with Container Apps Easy Auth

**Every request to the proxy is authenticated by the Container Apps platform before it reaches your app — no auth code required.**

## TL;DR (< 5 minutes)

1. Create an Entra app registration with a client secret, redirect URI, and ID token issuance enabled.
2. Wire it to your Container App with `az containerapp auth microsoft update` and set unauthenticated action to `Return401`.
3. Hit the app without a session — get `401`. Hit `/.auth/login/aad` — authenticate — get through.

**Expected outcome:** unauthenticated `curl` → `401`. Browser flow via `/.auth/login/aad` → `200`.

## What you will observe

- `curl https://<your-proxy>/` with no session cookie → `401 Unauthorized` (EasyAuth sidecar rejects before the proxy sees the request).
- Browser navigation to `https://<your-proxy>/` without a session → redirect to `https://login.microsoftonline.com/...`.
- After successful Entra login → redirect back to `https://<your-proxy>/.auth/login/aad/callback` → session cookie set → proxy receives request with identity headers.
- `X-MS-CLIENT-PRINCIPAL-NAME` header contains the authenticated user's UPN.
- `X-MS-CLIENT-PRINCIPAL` header contains a base64-encoded claims JSON.
- Requests from users not in the tenant → `401` (Entra rejects the login, session is never issued).

## Flow

```
Browser / API client
  │
  ▼
Container Apps EasyAuth sidecar
  ├─ No session / invalid token ──► 401 or redirect to login.microsoftonline.com
  └─ Valid session / token
       │  injects X-MS-CLIENT-PRINCIPAL-NAME, X-MS-CLIENT-PRINCIPAL headers
       ▼
  Proxy app code (never sees unauthenticated requests)
```

> [!NOTE]
> EasyAuth runs as a platform sidecar. The proxy receives only authenticated requests and reads identity from headers — no SDK or middleware needed.

## Setup

**What matters:** `--enable-id-token-issuance true`, the correct redirect URI, and unauthenticated action set to reject (not redirect) for API scenarios.

| Item | Value used in this POC |
| :--- | :--- |
| Redirect URI pattern | `https://<app-fqdn>/.auth/login/aad/callback` |
| Unauthenticated action | `Return401` (API) or `RedirectToLoginPage` (browser) |
| Identity headers injected | `X-MS-CLIENT-PRINCIPAL-NAME`, `X-MS-CLIENT-PRINCIPAL` |
| Login endpoint | `https://<app-fqdn>/.auth/login/aad` |
| Token refresh endpoint | `https://<app-fqdn>/.auth/refresh` |

**Prerequisites:**

- Azure subscription with Contributor access
- `az` CLI logged in (`az login`)
- Container App deployed (the proxy)
- Tenant ID on hand: `az account show --query tenantId -o tsv`

### 1. Create the Entra App Registration

**This registration tells Entra ID which app is allowed to authenticate users — the redirect URI must match exactly.**

```bash
APP_NAME="aca-auth-poc"

# Create registration
APP_ID=$(az ad app create \
  --display-name "$APP_NAME" \
  --sign-in-audience AzureADMyOrg \
  --query appId -o tsv)
echo "APP_ID=$APP_ID"

# Enable ID token issuance (required for EasyAuth)
az ad app update --id "$APP_ID" --enable-id-token-issuance true

# Set redirect URI
APP_FQDN="https://<app-name>.<env>.<region>.azurecontainerapps.io"
az ad app update --id "$APP_ID" \
  --web-redirect-uris "$APP_FQDN/.auth/login/aad/callback"

# Create client secret (set end-date explicitly — required in some tenants)
CLIENT_SECRET=$(az ad app credential reset \
  --id "$APP_ID" \
  --display-name "easyauth-secret" \
  --end-date "$(date -d '+30 days' '+%Y-%m-%d')" \
  --query password -o tsv)
echo "CLIENT_SECRET=$CLIENT_SECRET"

# Create service principal
az ad sp create --id "$APP_ID" 1>/dev/null
```

> [!WARNING]
> `--enable-id-token-issuance true` is mandatory. EasyAuth will fail silently at login without it.

### 2. Enable EasyAuth on the Container App

**This wires the app registration to the Container App platform — the sidecar handles all token validation from this point.**

> [!WARNING]
> If this command partially applies (enables auth but fails to register the provider), **the app returns `503` for all traffic** until auth is either fixed or disabled. Verify immediately after running — do not proceed to Step 3 until the provider check passes.

```bash
az containerapp auth microsoft update \
  --name "<CONTAINER_APP_NAME>" \
  --resource-group "<RG>" \
  --client-id "$APP_ID" \
  --client-secret "$CLIENT_SECRET" \
  --tenant-id "<TENANT_ID>" \
  --yes
```

> [!NOTE]
> This command also enables authentication on the app.

Verify the Microsoft identity provider was registered — **if the `identityProviders` block is empty, re-run the command above**:

```bash
az containerapp auth show \
  --name "<CONTAINER_APP_NAME>" \
  --resource-group "<RG>" \
  --query "{enabled:platform.enabled, provider:identityProviders.azureActiveDirectory.enabled}" \
  -o table
```

Expected output: both `enabled` columns show `True`. If the portal shows **"No identity provider"**, re-run Step 2 — the `az containerapp auth microsoft update` command may have partially applied.

### 3. Set the Unauthenticated Action

**For API/proxy scenarios, use `Return401` — `RedirectToLoginPage` is for browser-only apps.**

```bash
az containerapp auth update \
  --name "<CONTAINER_APP_NAME>" \
  --resource-group "<RG>" \
  --enabled true \
  --unauthenticated-client-action Return401
```

> [!TIP]
> Use `RedirectToLoginPage` if you want the browser to be sent to the Entra login page automatically instead of receiving a `401`.

## Run

```bash
APP_FQDN="https://<app-name>.<env>.<region>.azurecontainerapps.io"

# 1. Should return 401
curl -i "$APP_FQDN"

# 2. Browser login flow — open in browser
echo "$APP_FQDN/.auth/login/aad"

# 3. After login, check injected identity headers
curl -i "$APP_FQDN" --cookie "<session-cookie>"
```

> [!TIP]
> After logging in via the browser, use browser DevTools → Network → copy the `AppServiceAuthSession` cookie value for use with `curl`.

## Verify

Run each check in order. All four must pass.

- [ ] `curl -i $APP_FQDN` (no session) → `HTTP/1.1 401`, no proxy app output in response
- [ ] Open `$APP_FQDN/.auth/login/aad` in a browser → redirected to `login.microsoftonline.com`
- [ ] Complete Entra login → redirected back to `$APP_FQDN`, session cookie set, app responds normally
- [ ] Inspect response headers or app logs — `X-MS-CLIENT-PRINCIPAL-NAME` is present and contains your UPN

> [!TIP]
> `curl "$APP_FQDN/.auth/me"` with a valid session returns a JSON array of claims — use this to confirm the session is active without inspecting headers manually.

## Troubleshooting

| Symptom | Likely cause | Check |
| :--- | :--- | :--- |
| `curl` returns `503` instead of `401` | Auth is enabled but no identity provider is configured — platform blocks all traffic | **To restore the app immediately:** `az containerapp auth update -n <name> -g <rg> --enabled false`. Then re-run Step 2 and verify the provider is registered before re-enabling. Check with `az containerapp auth show -n <name> -g <rg> --query "identityProviders"` |
| Login redirects to Entra but then fails with `AADSTS50011` | Redirect URI mismatch | Compare the URI in the error with what is registered: `az ad app show --id $APP_ID --query "web.redirectUris"` |
| `401` even after successful Entra login | ID token issuance not enabled | Run `az ad app update --id $APP_ID --enable-id-token-issuance true` |
| Browser gets `401` instead of login redirect | Unauthenticated action set to `Return401` | Change to `RedirectToLoginPage`: `az containerapp auth update --unauthenticated-client-action RedirectToLoginPage` |
| `X-MS-CLIENT-PRINCIPAL-NAME` header missing in app | EasyAuth not fully enabled | Run `az containerapp auth show -n <name> -g <rg>` and confirm `"enabled": true` |
| Client secret error at login | Secret expired or wrong value | Reset: `az ad app credential reset --id $APP_ID --display-name "easyauth-secret"` then re-run Step 2 |


