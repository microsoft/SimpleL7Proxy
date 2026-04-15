# Request Validation

Reject or sanitize incoming requests before they enter the queue — return **417** if a required header is missing or fails a value rule, **403** if an App ID is not in the allowlist.

**TL;DR**
- Set `RequiredHeaders` to demand specific headers on every request; missing → 417.
- Set `ValidateHeaders` to enforce per-user value allowlists; mismatch → 417.
- Set `ValidateAuthAppID=true` + `ValidateAuthAppIDUrl` to block unknown Entra app IDs; unknown → 403.

## Reference Table

All settings are **Warm** — changes apply instantly without a container restart.

| Setting | Type | Default | Description |
|---|---|---|---|
| `RequiredHeaders` | `List<string>` | `[]` | Headers that must be non-empty; first missing header → 417. |
| `DisallowedHeaders` | `List<string>` | `[]` | Headers stripped from the request before forwarding to the backend. |
| `ValidateHeaders` | `Dictionary<string,string>` | `{}` | Rules: `SourceHeader=AllowlistHeader`. Value of `SourceHeader` must appear in the comma-separated list in `AllowlistHeader`. Supports `*` suffix for prefix matching. |
| `ValidateAuthAppID` | `bool` | `false` | Enable App ID allowlisting (runs before all other checks). |
| `ValidateAuthAppIDUrl` | `string` | `""` | URL or `file:auth.json` for the App ID allowlist. Requires `UseProfiles=true`. |
| `ValidateAuthAppIDHeader` | `string` | `X-MS-CLIENT-PRINCIPAL-ID` | Request header containing the caller's Entra App ID. |
| `ValidateAuthAppFieldName` | `string` | `authAppID` | JSON field name in the allowlist file that holds the App ID value. |

> [!NOTE]
> **Auto-population side effect:** Setting `ValidateHeaders=SourceHeader=AllowlistHeader` automatically adds both headers to `RequiredHeaders` **and** adds `AllowlistHeader` to `DisallowedHeaders`. The allowlist header is injected by the user profile service and must not reach the backend.

## Validation Execution Order

Validation runs on every non-probe request before it is enqueued. The order is fixed:

```
Incoming request
       │
       ▼
[1] ValidateAuthAppID check ──── fail ──► 403 Forbidden  (DisallowedAppID)
       │ pass
       ▼
[2] Strip DisallowedHeaders  (silent — no error returned)
       │
       ▼
[3] User profile lookup ─────── unknown ──► 403 Forbidden  (UnknownProfile)
       │ found → inject profile headers into request
       ▼
[4] RequiredHeaders check ───── first missing ──► 417 Expectation Failed  (IncompleteHeaders)
       │ all present
       ▼
[5] ValidateHeaders rules ───── first mismatch ──► 417 Expectation Failed  (InvalidHeader)
       │ all pass
       ▼
    Enqueue → workers → backend
```

---

## Scenario 1: Require Specific Headers on Every Request

**The simplest gate: reject any request that doesn't carry a mandatory header.**

Use `RequiredHeaders` to list header names that must be non-empty. The proxy returns 417 on the first missing one.

```env
RequiredHeaders=Authorization,X-Correlation-ID
```

A request without `Authorization` receives:
```
HTTP/1.1 417 Expectation Failed
X-S7P-Error: Required header is missing: Authorization
```

> [!TIP]
> Use `RequiredHeaders` to reject unauthenticated requests before they consume queue capacity or reach any backend.

---

## Scenario 2: Strip Internal Headers Before Forwarding

**Prevent internal routing or control headers from leaking to the backend.**

Use `DisallowedHeaders` to list headers the proxy removes silently before forwarding. The caller sees no error.

```env
DisallowedHeaders=X-Internal-RouteKey,X-Admin-Override
```

Common use: remove allowlist headers that the user profile service injects (see Scenario 3) so the backend never receives them.

> [!WARNING]
> When you set `ValidateHeaders`, the allowlist header is **automatically** added to `DisallowedHeaders`. Only add headers here manually for headers that have no corresponding `ValidateHeaders` rule.

---

## Scenario 3: Validate Header Values Against a Per-User Allowlist

**The most powerful pattern: each user has their own allowlist, injected at runtime by the user profile service.**

`ValidateHeaders` maps a *source header* (sent by the client) to an *allowlist header* (populated from the user's stored profile). The proxy checks that the source header value appears in the comma-separated allowlist. Prefix matching is supported with a trailing `*`.

### How it works

1. Client sends `X-Requested-Model: gpt-4o`.
2. User profile service injects `S7PAllowedModels: gpt-4o-mini,gpt-4o` into the request headers (from the user's profile JSON).
3. Proxy checks: is `gpt-4o` in `gpt-4o-mini,gpt-4o`? Yes → pass.
4. Before forwarding, `S7PAllowedModels` is stripped (it is auto-added to `DisallowedHeaders`).

### Configuration

```env
ValidateHeaders=X-Requested-Model=S7PAllowedModels

# This single line automatically adds:
#   RequiredHeaders += X-Requested-Model, S7PAllowedModels  (auto)
#   DisallowedHeaders += S7PAllowedModels                   (auto)
```

**User profile JSON** (served from `UserConfigUrl`):
```json
[
  {
    "userId": "alice@contoso.com",
    "S7PAllowedModels": "gpt-4o-mini,gpt-4o"
  },
  {
    "userId": "bob@contoso.com",
    "S7PAllowedModels": "gpt-4o-mini"
  },
  {
    "userId": "admin@contoso.com",
    "S7PAllowedModels": "gpt-4*"
  }
]
```

The `admin` entry uses a wildcard: `gpt-4o`, `gpt-4o-mini`, and `gpt-4-turbo` all match; `gpt-3.5-turbo` does not.

### Worked example

| User | Request header | Profile `S7PAllowedModels` | Result |
|---|---|---|---|
| alice@contoso.com | `X-Requested-Model: gpt-4o` | `gpt-4o-mini,gpt-4o` | ✅ 200 — forwarded |
| bob@contoso.com | `X-Requested-Model: gpt-4o` | `gpt-4o-mini` | ❌ 417 — `gpt-4o` not in allowlist |
| carol@contoso.com (no profile) | `X-Requested-Model: gpt-4o` | — | ❌ 403 — unknown profile |
| dave@contoso.com | *(header absent)* | `gpt-4o-mini,gpt-4o` | ❌ 417 — required header missing |
| admin@contoso.com | `X-Requested-Model: gpt-4-turbo` | `gpt-4*` | ✅ 200 — prefix match |

> [!NOTE]
> Error messages strip an `S7` prefix from the source header name. A rule `S7PModel=S7PAllowedModels` reports `Validation check failed for PModel: <value>`.

---

## Scenario 4: Block Unknown Entra App IDs

**Allowlist calling applications by their Entra App/Client ID — requests from any unlisted application receive 403.**

This check executes *first*, before DisallowedHeaders stripping, user profile lookup, and header validation.

### Configuration

```env
UseProfiles=true
ValidateAuthAppID=true
ValidateAuthAppIDUrl=file:auth.json
# ValidateAuthAppIDHeader=X-MS-CLIENT-PRINCIPAL-ID   ← default (injected by Azure EasyAuth)
# ValidateAuthAppFieldName=authAppID                 ← default
```

**auth.json**:
```json
[
  { "authAppID": "a1b2c3d4-0000-0000-0000-000000000001" },
  { "authAppID": "a1b2c3d4-0000-0000-0000-000000000002" },
  { "authAppID": "a1b2c3d4-0000-0000-0000-000000000003" }
]
```

> [!NOTE]
> `ValidateAuthAppIDUrl` is only active when `UseProfiles=true`. The allowlist is loaded on the same background timer as user profiles (`UserConfigRefreshIntervalSecs`, default 300 s).

### How it works

Azure Container Apps EasyAuth automatically injects `X-MS-CLIENT-PRINCIPAL-ID` with the caller's Entra App/Client GUID. The proxy performs a case-insensitive dictionary lookup. If the GUID is absent from the list — or the header itself is missing — the request is rejected:

```
Request: X-MS-CLIENT-PRINCIPAL-ID: a1b2c3d4-0000-0000-0000-000000000001
  ├── in auth.json → pass → continue

Request: X-MS-CLIENT-PRINCIPAL-ID: ffffffff-0000-0000-0000-ffffffffffff
  └── not in auth.json → 403 Forbidden
                          X-S7P-Error: Invalid AuthAppID: ffffffff-...
```

### Revoking access

Remove the entry from `auth.json`. The proxy picks up the change on the next refresh cycle without a restart. For zero-downtime revocation, use the soft-delete pattern: add `__DeletedAt` and `__ExpiresAt` timestamps to keep the entry present during the grace period before all in-flight requests drain.

### Using an HTTP endpoint

```env
ValidateAuthAppIDUrl=https://config-service.internal/api/allowlisted-appids
```

The endpoint must return a JSON array in the same format as `auth.json`. The proxy fetches and atomically swaps the in-memory dictionary on each refresh.

> [!WARNING]
> If `ValidateAuthAppIDUrl` is temporarily unreachable, the proxy continues using the **last successfully loaded** allowlist. After `UserSoftDeleteTTLMinutes` of continuous failure, the service transitions to a degraded readiness state and Container Apps will eventually stop routing traffic to it.

---

## Combining Multiple Rules

All mechanisms compose independently. Enable any subset:

```env
# Gate 1: Only known Entra apps may call the proxy
ValidateAuthAppID=true
ValidateAuthAppIDUrl=file:auth.json

# Gate 2: Every request must carry a correlation ID
RequiredHeaders=X-Correlation-ID

# Gate 3: Per-user model allowlist (auto-adds to RequiredHeaders and DisallowedHeaders)
ValidateHeaders=X-Requested-Model=S7PAllowedModels

# Gate 4: Strip an APIM internal routing header
DisallowedHeaders=X-APIM-Internal-Key
```

Combined flow for a fully valid request:

```
X-MS-CLIENT-PRINCIPAL-ID → auth.json lookup              → pass
X-APIM-Internal-Key      → stripped (DisallowedHeaders)
S7PAllowedModels         → injected from user profile
X-Correlation-ID         → RequiredHeaders check         → present → pass
X-Requested-Model        → checked against S7PAllowedModels → match → pass
S7PAllowedModels         → stripped (auto DisallowedHeaders)
                                                         → enqueue → backend
```

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| All requests get 403 immediately after enabling `ValidateAuthAppID` | Allowlist not yet loaded at startup | Check `[PROFILE] Auth: DEGRADED` in logs; wait for first successful load |
| 417 on a header you know the client sends | Header was stripped by `DisallowedHeaders` before the check | Review auto-added entries from your `ValidateHeaders` rule |
| 403 "User profile not found" even though profile exists | `UserProfileHeader` value doesn't match `UserIDFieldName` in the JSON | Confirm the header name and JSON field name are consistent |
| 417 "required header missing" for the allowlist header (`S7PAllowedModels`) | User profile not found or profile JSON lacks the key | Verify `UserConfigUrl` is reachable and the user ID matches `UserIDFieldName` |
| Allowlist header (`S7PAllowedModels`) appears in backend request | `ValidateHeaders` rule not set; strip is auto-applied only when the rule is active | Set the `ValidateHeaders` rule or add the header to `DisallowedHeaders` manually |
| App ID validation blocks a valid caller after Entra cert rotation | The App/Client GUID changed | Update `auth.json` with the new GUID |
