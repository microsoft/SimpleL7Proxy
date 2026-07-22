# POC: Securing the Proxy with Container Apps EasyAuth

**Protect the proxy from unauthorized access.**

## TL;DR (< 5 minutes)

1. Run the `secureProxy.sh` script to configure EasyAuth in ACA and enable authentication.
2. Call the proxy with a bearer token from a trusted client — everything else is denied.

**Expected outcome:** Only authenticated, authorized callers can reach the proxy.

> EasyAuth offloads authentication from the proxy and only accepts authorized requests.

## What you will observe

- Requests without a valid Entra token are rejected before the proxy sees them.
- Requests with a token from a trusted client reach the proxy and get its normal response.
- Requests with a token from any other source — wrong audience, untrusted client, expired — are rejected.
- The proxy receives no unauthenticated or unauthorized traffic at any point.

## Flow

```
API client / service
   │
   │  Authorization: Bearer <token>
   ▼
ACA EasyAuth sidecar
   ├─ No token              ──► 401
   ├─ Wrong audience        ──► 403
   ├─ Untrusted client app  ──► 403
   └─ Valid token from trusted client
        ▼
   SimpleL7Proxy
   (receives only authenticated requests)
```

> [!NOTE]
> EasyAuth is enforced by the Container Apps platform and intercepts all traffic.
>
> This POC uses Entra ID only. EasyAuth also supports other providers (GitHub, Google, custom OpenID Connect), token refresh, per-route exclusions, and forwarding signed claims — out of scope here.

## Setup

**Prerequisites:**

- Azure subscription with Contributor access
- `az` CLI authenticated (`az login`)
- Container App deployed (the proxy)
- Tenant ID: `az account show --query tenantId -o tsv`

### Run `secureProxy.sh` in ACA mode

> [!NOTE] OAuth 2.0 in Azure requires a client that has been granted a permission on an app registration representing the resource, and a validator configured to accept tokens for that app registration. Each client that calls the proxy will need to be granted that permission before its tokens will be accepted.

In this POC the client is the Azure CLI, our validator is built into the ACA, and the permission on the proxy's app registration is a delegated scope named `api.access`.

To simplify this setup, the [`secureProxy.sh`](../../deployment/POC/secureProxy.sh) script in `deployment/POC/` will do the work of configuring the ACA to do the validation.

Arguments:

1. `-a` — display name for the app registration (created if missing, reused if present).
2. `-n` / `-g` — the Container App and its resource group.

```bash
./deployment/POC/secureProxy.sh \
  -a aca-proxy \
  -n <your-container-app-name> \
  -g <your-resource-group>
```

On success, you will see these values.

Output:
```bash
export APP_ID="<guid>"
export TENANT_ID="<tenant-guid>"
export HEALTH_URL="https://<fqdn>/health"
export CONTAINER_APP_NAME="<name>"
export RG="<resource-group>"
```

Paste them into your shell, then continue to **Verify Access** below. Re-running with the same arguments is safe.

> [!NOTE]
> Creating the app registration requires `Application.ReadWrite.All` (or equivalent) in the tenant. If you don't have that, an admin can either run the script or pre-create the app — an existing app registration with a matching display name is reused.


## Verify Access

Run four requests and check the codes:

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

# 4. Wrong audience (valid Azure token for a different resource) — expect 403
BAD_TOKEN=$(az account get-access-token --resource "https://management.azure.com/" --query accessToken -o tsv | tr -d '\r\n')
curl -i "$HEALTH_URL" -H "Authorization: Bearer $BAD_TOKEN"
```

If you saw `401`, `200`, `403` in that order, the proxy is secured.

> [!NOTE]
> Skipped the script and set things up by hand? Pre-authorize Azure CLI (well-known client ID `04b07795-8ddb-461a-bbee-02f9e1bf7b46`) on the API app's `api.access` scope, or step 2 fails with `AADSTS650057: Invalid resource`.

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

| Symptom | Cause | Fix |
| :--- | :--- | :--- |
| `403` with valid token; logs show `"does not match any of the allowed applications"` | The client's `appId` (token `azp` claim) is not in `defaultAuthorizationPolicy.allowedApplications` | Add it: `./secureProxy.sh -a <name> -z <client-appId> -n $CONTAINER_APP_NAME -g $RG` |
| `403` with valid token; logs show `aud` does not match | Token `aud` is `api://<GUID>` (v1) but `allowedAudiences` is the bare GUID (v2) — or vice versa | Re-run `secureProxy.sh` (it forces v2 tokens with the bare-GUID audience). Decode the token at [jwt.ms](https://jwt.ms) to confirm `aud` |
| `403` with valid token immediately after a config change | EasyAuth's per-principal deny cache (~60s) | Wait ~60 seconds or acquire a fresh token, then retry |
| `503` on all traffic after enabling auth | Auth enabled but no identity provider registered | `az containerapp auth show -n $CONTAINER_APP_NAME -g $RG --query identityProviders` — if empty, re-run `secureProxy.sh`. To restore service immediately: `az containerapp auth update -n $CONTAINER_APP_NAME -g $RG --enabled false` |
| `401` despite a valid Entra token | Auth platform not enabled, or no provider configured | `az containerapp auth show -n $CONTAINER_APP_NAME -g $RG` — confirm `platform.enabled=true` and `identityProviders.azureActiveDirectory` is populated |
| Client secret rejected at authentication | Secret expired or rotated | Re-run `secureProxy.sh` — it mints a new 30-day secret and writes it to EasyAuth |
| `AADSTS65001 consent_required` when requesting token | Calling client is not pre-authorized on the `api.access` scope | `./secureProxy.sh -a <name> -z <client-appId>` — or in the portal: **API app → Expose an API → Authorized client applications → Add**, paste the client's `appId`, check `api.access` |


