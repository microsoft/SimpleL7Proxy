# Backend Host Configuration

Configure backend hosts and optional named path routes using semicolon-separated connection strings.

> **TL;DR**
> - **Existing `Host1`…`HostN` configurations require no changes** — omitted mode retains probed/APIM behavior, accepts every priority in group `1`, and calls the configured host itself.
> - **Use named `Host_*` and `Path_*` settings for reusable routes** — the longest matching prefix owns the request.
> - **Host mode is explicit** — `apim` is a probed gateway, `direct` is called without probes, and `indirect` is delegated through `via`.

---

## Reference — Connection String Keys

> **Units:** all timeouts in milliseconds unless noted. Delimiters: `;` or `,` (both accepted).

| Key | Default | Description |
|-----|---------|-------------|
| `host` | *(required)* | Backend base URL. Protocol defaults to `https://` if omitted. Trailing slashes are stripped. |
| `probe` | `echo/resource?param1=sample` | Health probe path. Used only by `mode=apim`; ignored by `direct` and `indirect`. |
| `path` | `/` | Path prefix used for routing. Requests matching this prefix are sent to this host. |
| `acceptablePriorities` | `*` | Colon-separated numeric request priorities accepted by this host, for example `1:2`. `*` or omission accepts all priorities. |
| `priorityGroup` | `1` | Positive integer failover group. Lower groups are exhausted before higher groups. `LoadBalanceMode` orders peers within one group. |
| `via` | *(empty)* | Named `mode=apim` gateway host such as `Host_apim`. REQUIRED with `mode=indirect` and invalid with other modes. |
| `mode` | `apim` | `apim` calls and probes this host; `direct` calls it without probes; `indirect` never calls or probes it and delegates through `via`. Unknown values are rejected. |
| `ipaddress` | *(empty)* | Override DNS — force all requests to this IP. |
| `processor` | *(empty)* | Custom stream processor name. Required and auto-defaulted in `direct` mode. |
| `usemi` / `useoauth` | `false` | Attach a Managed Identity / OAuth2 Bearer token to every request and probe. |
| `audience` | *(empty)* | OAuth token audience. Required when `usemi=true`. |
| `authprovider` | `AzureProvider` | Exact simple class name of an `IBackendTokenProvider` registered by `AuthProviders`. Matching is case-insensitive; no class-name suffix is implied. |
| `api-key` | *(empty)* | API key value to send on every forwarded request and probe. Sets auth mode to API key. |
| `api-key-header` | `api-key` | Header name used when `api-key` is set. |
| `stripprefix` / `strippathprefix` | `true` | Strip the matched `path` prefix before forwarding. Set `false` to preserve the full original path. |
| `retryafter` / `useretryafter` | `true` | Honour the `Retry-After` header returned by the backend. |

> [!WARNING]
> An **unrecognised or invalid key rejects the complete candidate host/route snapshot**. A warm refresh retains the last known-good snapshot; an invalid initial configuration starts with no active hosts.

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
Host4="host=https://secure-api.internal;usemi=true;audience=api://my-app-id;authprovider=AzureProvider;probe=/health"

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

For OAuth2 mode, `authprovider` selects one of the implementations registered globally through `AuthProviders`. The value MUST equal the implementation's simple runtime class name, such as `AzureProvider` or `ContosoTokenSource`; custom class names do not need to end in `Provider`.

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

## Selecting A Host Mode

**Rule: Set the mode according to who receives the proxy's HTTP request.**

```bash
Host_apim="host=https://gateway.azure-api.net;mode=apim;probe=/status"
Host_direct="host=https://model.example.net;mode=direct"
Host_logical="host=https://ptu.example.net;mode=indirect;via=Host_apim"
```

| Mode | Called by proxy? | Probed by proxy? | Required companion setting |
|---|---:|---:|---|
| `apim` | Yes | Yes | A usable `probe` path |
| `direct` | Yes | No | None |
| `indirect` | No | No | `via=Host_<gateway>` targeting an `apim` host |

Omitting `mode` preserves the existing `apim` behavior. A `via` target must use `mode=apim`; indirect-to-indirect chains, `via` on direct/APIM hosts, and indirect hosts outside a `Path_*` route are rejected.

> [!TIP]
> **Troubleshooting:** If a candidate snapshot is rejected after adding `via`, verify that the logical host uses `mode=indirect` and the referenced gateway uses `mode=apim`.

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

### Named Routes

**Rule: A `Path_*` setting owns its longest matching prefix and references named hosts without duplicating their connection settings.**

```bash
Host_chat_east="host=https://chat-east.internal;mode=direct;acceptablePriorities=1:2;priorityGroup=1"
Host_chat_fallback="host=https://chat-fallback.internal;mode=direct;acceptablePriorities=1:2:3;priorityGroup=2"
Path_chat="prefix=/api/chat;hosts=Host_chat_east:Host_chat_fallback;stripprefix=true"
```

Named route fields:

| Field | Default | Description |
|---|---|---|
| `prefix` | *(required)* | Segment-boundary request prefix. `/api` matches `/api` and `/api/x`, not `/apix`. |
| `hosts` | *(required)* | Colon-separated `Host_*`, `Host-*`, or `HostN` references. |
| `stripprefix` / `strippathprefix` | `true` | Remove the matched prefix before forwarding. |

The longest prefix wins. If that route has no host accepting the request priority, the proxy returns no candidates and does not fall through to a broader route. Hosts referenced by named routes are not also exposed through legacy catch-all selection.

> [!TIP]
> **Troubleshooting:** If a route keeps the previous configuration after refresh, check for a missing host reference, duplicate prefix, mixed direct and `via` hosts, or an invalid `via` target. The rejected update is logged and the active snapshot remains unchanged.

### Priority Groups

**Rule: Filter by `acceptablePriorities`, exhaust the lowest eligible `priorityGroup`, then advance to the next group.**

```bash
Host_ptu="host=https://ptu.internal;mode=direct;acceptablePriorities=1;priorityGroup=1"
Host_paygo="host=https://paygo.internal;mode=direct;acceptablePriorities=1:2:3;priorityGroup=2"
Path_models="prefix=/models;hosts=Host_ptu:Host_paygo;stripprefix=false"
```

For priority `1`, PTU is tried before PayGo. Priorities `2` and `3` start directly at PayGo because no group-1 host accepts them. `latency` and `timetofirstbyte` sort only within a group, so a faster group-2 host cannot precede group 1.

> [!TIP]
> **Troubleshooting:** A `503` with no attempted backend means the matched route has no host whose `acceptablePriorities` contains the resolved numeric request priority.

### Routing Through APIM

**Rule: Set every logical APIM-selected backend to `mode=indirect;via=Host_<gateway>`; keep `via` off `Path_*`.**

```bash
Host_apim="host=https://gateway.azure-api.net;mode=apim;probe=/status"
Host_ptu="host=https://ptu.openai.azure.com/openai;mode=indirect;via=Host_apim;acceptablePriorities=1;priorityGroup=1"
Path_openai="prefix=/api;hosts=Host_ptu;stripprefix=true"
```

All hosts in one route must either be callable hosts or be `indirect` hosts referencing the same `apim` gateway. Missing, self-referencing, chained, mixed-mode, and multiple-gateway configurations are rejected atomically. The gateway owns proxy health and circuit state; indirect logical backends are not probed or called directly.

> [!NOTE]
> In the current migration phase, `via` selects the APIM transport but APIM still reads its endpoint catalog and retry rules from the deployed policy fragment. The signed proxy-to-APIM route envelope and policy parser remain deferred work.

> [!TIP]
> **Troubleshooting:** If APIM selects an unexpected endpoint, compare the logical host settings with the deployed APIM fragment. Until envelope support is completed, the fragment remains authoritative inside APIM.

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

## Related Documentation

- [LOAD_BALANCING.md](load-balancing.md) — How hosts are ordered and retried per request
- [CIRCUIT_BREAKER.md](circuit-breaker.md) — Per-request failure tracking and circuit state
- [CONFIGURATION_SETTINGS.md](configuration.md) — `PollInterval`, `PollTimeout`, `SuccessRate` config keys
- [Customize a Backend Token Provider](../how-to/customize-token-provider.md) — Implement, register, and select an outbound token provider
