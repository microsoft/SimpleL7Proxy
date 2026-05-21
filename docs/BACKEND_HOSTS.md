# Backend Host Configuration

Configure any number of backend hosts (`Host1`…`Host9`) using a semicolon-separated connection string, or the simpler legacy per-variable format.

> **TL;DR**
> - **Connection string format is recommended** — all per-host options in one variable.
> - **`mode=direct` skips health probes entirely** — the host is always considered healthy; use it for serverless/on-demand backends.
> - **The health poller runs every `PollInterval` ms** and drops hosts below `SuccessRate`% from the active pool until they recover.

---

## Reference — Connection String Keys

> **Units:** all timeouts in milliseconds unless noted. Delimiters: `;` or `,` (both accepted).

| Key | Default | Description |
|-----|---------|-------------|
| `host` | *(required)* | Backend base URL. Protocol defaults to `https://` if omitted. Trailing slashes are stripped. |
| `probe` | `echo/resource?param1=sample` | Health probe path. Ignored when `mode=direct`. |
| `path` | `/` | Path prefix used for routing. Requests matching this prefix are sent to this host. |
| `mode` | *(standard)* | Set to `direct` to disable probing and assume the host is always healthy. |
| `ipaddress` | *(empty)* | Override DNS — force all requests to this IP. |
| `processor` | *(empty)* | Custom stream processor name. Required and auto-defaulted in `direct` mode. |
| `usemi` / `useoauth` | `false` | Attach a Managed Identity / OAuth2 Bearer token to every request and probe. |
| `audience` | *(empty)* | OAuth token audience. Required when `usemi=true`. |
| `api-key` | *(empty)* | API key value to send on every forwarded request and probe. Sets auth mode to API key. |
| `api-key-header` | `api-key` | Header name used when `api-key` is set. |
| `stripprefix` / `strippathprefix` | `true` | Strip the matched `path` prefix before forwarding. Set `false` to preserve the full original path. |
| `retryafter` / `useretryafter` | `true` | Honour the `Retry-After` header returned by the backend. |
| `usegcpauth` | `false` | Enable GCP Workload Identity Federation auth for this host. See [GCP Vertex AI](#gcp-vertex-ai-backends) below. |
| `gcpproject` | *(required with `usegcpauth`)* | GCP project name used in the backend path (e.g. `a208790-ellms-preprod`). |
| `gcpprojectnumber` | *(required with `usegcpauth`)* | Numeric GCP project number used in the WIF audience URL (e.g. `753819451045`). |
| `gcpregion` | *(required with `usegcpauth`)* | GCP region (e.g. `us-east1`). Used in the backend path; also auto-derives `host` if omitted. |
| `gcppool` | *(required with `usegcpauth`)* | Workload Identity Federation pool ID (e.g. `azure-gcp-identity-federation`). |
| `gcpprovider` | *(required with `usegcpauth`)* | WIF provider ID (e.g. `azure-gcp-identity-provider`). |
| `gcpsa` | *(required with `usegcpauth`)* | GCP service account email to impersonate (e.g. `my-svc@project.iam.gserviceaccount.com`). |
| `gcpazureclientid` | *(required with `usegcpauth`)* | Azure resource URI used to obtain the subject token (e.g. `api://374a2caa-...`). |

> [!WARNING]
> An **unrecognised key** in the connection string throws `UriFormatException` at startup and prevents the proxy from starting.

---

## Configuring Hosts

**Rule: Use the connection string format for all new hosts — it keeps every option for a host in one variable.**

```bash
# Minimal — standard probed host
Host1="host=https://api.backend.com;probe=/health"

# Path-routed host (strip prefix, default)
Host2="host=https://chat-service.internal;path=/chat;probe=/health"

# Preserve full path (backend owns its own routing)
Host3="host=https://passthrough.internal;path=/api/v1;stripprefix=false"

# Authenticated host (Managed Identity)
Host4="host=https://secure-api.internal;usemi=true;audience=api://my-app-id;probe=/health"

# Authenticated host (API key with custom header)
Host5="host=https://secure-api.internal;api-key-header=foo;api-key=bar;probe=/health"

# Direct mode — serverless, no probing
Host6="host=https://my-func.azurewebsites.net;mode=direct;path=/api/v1"

# IP override — skip DNS
Host7="host=https://api.backend.com;ipaddress=10.0.1.5;probe=/health"
```

## Per-Host Auth Behavior

Auth is configured per host via the `HostN` connection string, not by a global `UseOAuth` switch.

| Host connection string values | Effective auth mode |
|-------------------------------|---------------------|
| `useoauth=true` (or `usemi=true`) | OAuth2 / Managed Identity |
| `api-key=<non-empty>` | API key mode (`<api-key-header>: <api-key>`) |
| `useoauth=false` and empty `api-key` | No auth header added |

Example custom header mapping:

```bash
Host1="host=https://example.internal;api-key-header=foo;api-key=bar"
```

This sends `foo: bar` to that backend.

> [!NOTE]
> Set only one auth mode per host. If both OAuth and API key entries are present, the host string order can change which mode is applied.

> [!NOTE]
> **Legacy format** (`Host1=https://...`, `Probe_path1=/health`, `IP1=10.0.1.5`) is still supported but cannot express `path`, `mode`, `usemi`, or other per-host options. Do not mix legacy and connection-string keys for the same host number.

---

## Direct Mode

**Rule: Use `mode=direct` for any backend that scales to zero — the proxy will never probe it, so it will never wake it unnecessarily.**

```bash
Host6="host=https://my-func.azurewebsites.net;mode=direct;path=/api/v1"
```

In direct mode:
- No health probe is ever sent.
- The host is always treated as healthy (`SuccessRate = 1.0`).
- Average latency defaults to `0`, so direct-mode hosts sort first in `latency` load-balance mode.
- `processor` is auto-set to the default stream processor if not specified.

> [!TIP]
> **Troubleshooting:** If a direct-mode host starts returning errors, the circuit breaker still tracks failures per request — the host will be excluded once it breaches `CBErrorThreshold`.

---

## Path-Based Routing

**Rule: Specific-path hosts always win over catch-all hosts; within matched hosts the load balancer decides.**

```bash
Host1="host=https://chat-service.internal;path=/chat"
Host2="host=https://embed-service.internal;path=/embeddings"
Host3="host=https://default-service.internal"   # catch-all (path=/)
```

| Incoming request | Matched host | Forwarded path (`stripprefix=true`) |
|------------------|--------------|--------------------------------------|
| `GET /chat/completions` | Host1 | `GET /completions` |
| `POST /embeddings/create` | Host2 | `POST /create` |
| `GET /models` | Host3 | `GET /models` |

Path matching rules:
1. Hosts with an explicit `path` prefix are checked first.
2. `/`, `/*`, or empty `path` is a catch-all and is tried only when no specific path matches.
3. Wildcards (`/api/*`) match the same as the bare prefix (`/api`).

> [!NOTE]
> **`stripprefix=false`** preserves the full original request path on the forwarded request. Use this when the backend application handles its own sub-routing under the same prefix.

---

## Health Polling

**Rule: The poller runs every `PollInterval` ms; a host is active only while its rolling success rate is ≥ `SuccessRate`%.**

```
Every PollInterval ms:
  For each probed host:
    GET <ProbeUrl>  (timeout = PollTimeout ms)
    ├── 2xx  → AddCallSuccess(true)  → latency recorded
    └── else → AddCallSuccess(false) → latency not recorded

FilterActiveHosts:
  active = hosts where SuccessRate() >= threshold
  if latency order changed → invalidate shared iterator cache
```

| Config | Default | Description |
|--------|---------|-------------|
| `PollInterval` | `15000` ms | How often each host is probed |
| `PollTimeout` | `3000` ms | Max wait for a probe response |
| `SuccessRate` | `80` % | Minimum rolling success rate to stay active |

> [!NOTE]
> Direct-mode hosts skip `GetHostStatus` entirely — they always return `true` and are included in `FilterActiveHosts` unconditionally.

> [!TIP]
> **Troubleshooting:** If all hosts fall below the threshold the proxy returns `503`. Lower `SuccessRate` or increase `PollTimeout` if backends are slow but functional.

---

## Worked Example

> **Setup:** 3 hosts, `LoadBalanceMode=latency`, `SuccessRate=80`, `PollInterval=15000`.

| Host | Probe result | Rolling rate | Active? | Avg latency |
|------|-------------|--------------|---------|-------------|
| `chat-service` | 9/10 success | 90% | Yes | 120 ms |
| `embed-service` | 6/10 success | 60% | **No** | — |
| `func-direct` | `mode=direct` | always 100% | Yes | 0 ms |

**In latency mode, `func-direct` (0 ms) is tried first, then `chat-service` (120 ms). `embed-service` is excluded until its rolling rate recovers above 80%.**

---

## GCP Vertex AI Backends

Use `usegcpauth=true` to route requests to Google Cloud Vertex AI with automatic OAuth via **Workload Identity Federation (WIF)**.

The proxy handles the full 3-step authentication flow on your behalf:
1. Acquires an Azure JWT for the configured `gcpazureclientid` resource via `DefaultAzureCredential`
2. Exchanges it at `https://sts.googleapis.com/v1/token` for a short-lived federated GCP token
3. Impersonates the `gcpsa` service account at `https://iamcredentials.googleapis.com` to get the final access token
4. Injects `Authorization: Bearer {token}` on every forwarded request; background task refreshes 5 minutes before expiry

### Path Translation

The `path` key sets the **client-facing** prefix. The proxy auto-constructs the full Vertex AI resource path:

```
client prefix stripped  →  /v1/projects/{gcpproject}/locations/{gcpregion}  +  remaining path
```

| Client request | Forwarded backend path |
|---|---|
| `POST /a208790-gemini-2.5-pro/publishers/google/models/gemini-2.5-flash:streamGenerateContent` | `POST /v1/projects/a208790-ellms-preprod/locations/us-east1/publishers/google/models/gemini-2.5-flash:streamGenerateContent` |

### Config Example

```bash
Host1="mode=direct;path=/a208790-gemini-2.5-pro;\
usegcpauth=true;\
gcpproject=a208790-ellms-preprod;\
gcpprojectnumber=753819451045;\
gcpregion=us-east1;\
gcppool=azure-gcp-identity-federation;\
gcpprovider=azure-gcp-identity-provider;\
gcpsa=eais-vertexai-svc@a208790-eais6-prod.iam.gserviceaccount.com;\
gcpazureclientid=api://374a2caa-b184-4b47-93e9-3b7c4e7a6b76"
```

> [!NOTE]
> **`host=` is optional** when `usegcpauth=true` — the proxy derives it as `https://{gcpregion}-aiplatform.googleapis.com`.

> [!NOTE]
> **`mode=direct` is required** for Vertex AI since there is no standard health probe endpoint. The circuit breaker still tracks per-request failures.

> [!NOTE]
> **Existing AOAI backends are unaffected.** GCP auth and path rewriting only activate when `usegcpauth=true` is present in the host config string.

### Token Refresh Logs

```
[TOKEN] Refreshed GCP token for pool: //iam.googleapis.com/projects/753819451045/locations/global/workloadIdentityPools/azure-gcp-identity-federation/providers/azure-gcp-identity-provider, SA: eais-vertexai-svc@..., expires: 2026-05-11T14:00:00+00:00
```

---

## Related Documentation

- [LOAD_BALANCING.md](LOAD_BALANCING.md) — How hosts are ordered and retried per request
- [CIRCUIT_BREAKER.md](CIRCUIT_BREAKER.md) — Per-request failure tracking and circuit state
- [CONFIGURATION_SETTINGS.md](CONFIGURATION_SETTINGS.md) — `PollInterval`, `PollTimeout`, `SuccessRate` config keys

