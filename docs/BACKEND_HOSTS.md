# Backend Host Configuration

Configure up to 9 backend hosts (`Host1`…`Host9`) using a semicolon-separated connection string, or the simpler legacy per-variable format.

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
| `stripprefix` / `strippathprefix` | `true` | Strip the matched `path` prefix before forwarding. Set `false` to preserve the full original path. |
| `retryafter` / `useretryafter` | `true` | Honour the `Retry-After` header returned by the backend. |

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

# Direct mode — serverless, no probing
Host5="host=https://my-func.azurewebsites.net;mode=direct;path=/api/v1"

# IP override — skip DNS
Host6="host=https://api.backend.com;ipaddress=10.0.1.5;probe=/health"
```

> [!NOTE]
> **Legacy format** (`Host1=https://...`, `Probe_path1=/health`, `IP1=10.0.1.5`) is still supported but cannot express `path`, `mode`, `usemi`, or other per-host options. Do not mix legacy and connection-string keys for the same host number.

---

## Direct Mode

**Rule: Use `mode=direct` for any backend that scales to zero — the proxy will never probe it, so it will never wake it unnecessarily.**

```bash
Host5="host=https://my-func.azurewebsites.net;mode=direct;path=/api/v1"
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

## Related Documentation

- [LOAD_BALANCING.md](LOAD_BALANCING.md) — How hosts are ordered and retried per request
- [CIRCUIT_BREAKER.md](CIRCUIT_BREAKER.md) — Per-request failure tracking and circuit state
- [CONFIGURATION_SETTINGS.md](CONFIGURATION_SETTINGS.md) — `PollInterval`, `PollTimeout`, `SuccessRate` config keys

