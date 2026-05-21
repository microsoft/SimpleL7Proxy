# POC: Security and OAuth 2.0 Configuration

**Purpose:** Validate the repo-aligned OAuth 2.0 trust chain across client -> Container App -> APIM, including the three Entra app registrations and Graph-based app role assignments.

## TL;DR (< 5 minutes)

1. **Most important rule: each hop must use its own token audience (`aud`) and role check; do not pass the same token end-to-end.**
2. Create three app registrations: APIM protected API, ACA protected API, and client caller app.
3. Assign app roles with Microsoft Graph PowerShell when portal UI cannot target managed identities.

## What you will observe

- Client obtains a token for ACA (`aud = api://<ACA_APP_ID>`) and can call ACA when ACA auth is enabled.
- ACA uses managed identity to obtain a separate token for APIM (`aud = api://<APIM_APP_ID>`) and APIM accepts `roles = API.Caller`.
- App role assignment succeeds for identities not selectable in portal UI by using Graph PowerShell cmdlets.

## Reference

| Setting | Value in this POC | Unit | Set in | Takes effect |
| :--- | :--- | :--- | :--- | :--- |
| APIM API app registration | `SimpleL7Proxy-APIM-API` | name | Entra App registrations | immediate |
| ACA API app registration | `SimpleL7Proxy-ACA-API` | name | Entra App registrations | immediate |
| Client app registration | `SimpleL7Proxy-Client` | name | Entra App registrations | immediate |
| App role value | `API.Caller` | claim value | APIM API + ACA API app registrations | token issuance |
| ACA scope value | `api.access` | scope value | ACA API app registration | token issuance |
| APIM audience | `api://<APIM_APP_ID>` | URI | APIM `validate-jwt` policy | policy save |
| ACA audience | `api://<ACA_APP_ID>` | URI | ACA auth config | config save |
| Client -> ACA token resource | `api://<ACA_APP_ID>` | URI | token request | per request |
| ACA -> APIM token resource | `api://<APIM_APP_ID>/.default` | URI | managed identity token request | per request |
| Graph module permission | `AppRoleAssignment.ReadWrite.All` | Graph scope | `Connect-MgGraph` | login session |

> [!NOTE]
> Units used in this doc: all IDs are GUIDs; audiences are URI strings; role/scope values are string claims.

## Setup

### 1) Register APIM protected API app

**Rule: APIM must validate tokens issued for the APIM API audience, not the ACA audience.**

```text
Name: SimpleL7Proxy-APIM-API
Application ID URI: api://<APIM_APP_ID>
App role: API.Caller (Allowed member types: Applications)
```

> [!WARNING]
> If `Allowed member types` excludes `Applications`, app-to-app role assignment fails.

### 2) Register ACA protected API app

**Rule: ACA must expose its own audience and scope for inbound client tokens.**

```text
Name: SimpleL7Proxy-ACA-API
Application ID URI: api://<ACA_APP_ID>
Scope: api.access
```

> [!TIP]
> Keep this audience distinct from APIM to avoid token confusion between hops.

### 3) Register client app

**Rule: the client app needs permission to ACA scope and must be allowed by ACA auth policy.**

```text
Name: SimpleL7Proxy-Client
API permission: ACA API -> Delegated -> api.access
Credential: client secret (or cert)
```

> [!NOTE]
> For service-to-service calls, use client credentials and validate `roles` where applicable.

### 4) Assign app roles with PowerShell (Graph)

**Rule: use Graph PowerShell for app role assignments when managed identities do not appear in portal options.**

```powershell
Connect-MgGraph -Scopes "Application.Read.All AppRoleAssignment.ReadWrite.All"
Select-MgProfile -Name "v1.0"
Get-MgContext
```

> [!TIP]
> If `Connect-MgGraph` fails on permissions, sign in with an Entra admin account and consent the requested scopes.

#### 4a) Assign ACA managed identity -> APIM API role (`API.Caller`)

```powershell
$acaSpId = "<ACA_MANAGED_IDENTITY_OBJECT_ID>"
$apimResourceSpId = "<APIM_API_SERVICE_PRINCIPAL_OBJECT_ID>"
$apimRoleId = "<APIM_API_CALLER_ROLE_ID>"
New-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $acaSpId -PrincipalId $acaSpId -ResourceId $apimResourceSpId -AppRoleId $apimRoleId
```

> [!WARNING]
> Use object IDs, not app IDs, for `ServicePrincipalId`, `PrincipalId`, and `ResourceId`.

#### 4b) Assign client service principal -> ACA API role (`API.Caller`)

```powershell
$clientSpId = "<CLIENT_SERVICE_PRINCIPAL_OBJECT_ID>"
$acaResourceSpId = "<ACA_API_SERVICE_PRINCIPAL_OBJECT_ID>"
$acaRoleId = "<ACA_API_CALLER_ROLE_ID>"
New-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $clientSpId -PrincipalId $clientSpId -ResourceId $acaResourceSpId -AppRoleId $acaRoleId
```

> [!NOTE]
> If you use delegated-only access to ACA (`api.access`), keep this step optional; for app-role based enforcement, keep it required.

### 5) Configure ACA auth and APIM JWT validation

**Rule: ACA validates client token audience; APIM validates ACA managed identity token audience and role.**

```text
ACA allowed audience: api://<ACA_APP_ID>
APIM validate-jwt audience: api://<APIM_APP_ID>
APIM required claim: roles contains API.Caller
```

> [!WARNING]
> Passing ACA audience to APIM `validate-jwt` is a common misconfiguration and causes authorization failures.

## Full flow

```mermaid
flowchart LR
    A[Client App Registration\n(SimpleL7Proxy-Client)] -->|Token aud=api://ACA_APP_ID| B[ACA Ingress + Easy Auth]
    B -->|Validates ACA audience| C[SimpleL7Proxy in ACA]
    C -->|Managed Identity token\naud=api://APIM_APP_ID/.default| D[APIM]
    D -->|validate-jwt: audience + role API.Caller| E[Backend routing/policy]

    F[ACA API App Registration\n(SimpleL7Proxy-ACA-API)] -. defines audience/scope .-> B
    G[APIM API App Registration\n(SimpleL7Proxy-APIM-API)] -. defines API.Caller role .-> D
```

## Worked example

| Step | Example value | Result |
| :--- | :--- | :--- |
| Create APIM API app | `appId = 11111111-1111-1111-1111-111111111111` | APIM audience becomes `api://11111111-1111-1111-1111-111111111111` |
| Create ACA API app | `appId = 22222222-2222-2222-2222-222222222222` | ACA audience becomes `api://22222222-2222-2222-2222-222222222222` |
| Create client app | `appId = 33333333-3333-3333-3333-333333333333` | Client can request token for ACA audience |
| Assign ACA MI role on APIM API | `New-MgServicePrincipalAppRoleAssignment ...` | APIM accepts ACA token with `roles: API.Caller` |
| Request token in ACA for APIM | `resource = api://111.../.default` | ACA -> APIM call authorized |

## Verify

- [ ] APIM API app registration exists with app role `API.Caller`.
- [ ] ACA API app registration exists with scope `api.access` and identifier URI.
- [ ] Client app registration has permission to call ACA API.
- [ ] ACA managed identity is assigned to APIM API app role.
- [ ] ACA auth is enabled and configured with ACA audience.
- [ ] APIM `validate-jwt` checks APIM audience and `roles=API.Caller`.
- [ ] Client can call ACA with token for ACA audience.
- [ ] ACA can call APIM with managed identity token for APIM audience.

## Related docs

- [scripts/README.md](../scripts/README.md)
- [scripts/ca2apimSetup.sh](../scripts/ca2apimSetup.sh)
- [scripts/console2caSetup.sh](../scripts/console2caSetup.sh)
- [scripts/enableContainerAppAuth.sh](../scripts/enableContainerAppAuth.sh)
- [APIM-Policy/readme.md](../APIM-Policy/readme.md)
