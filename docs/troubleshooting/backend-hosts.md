# Backend Hosts Not Healthy

> **TL;DR**
> 1. Verify the `host=` URL and `probe=` path are reachable from the proxy.
> 2. Check `SuccessRate` — hosts below the threshold are removed from the active pool.
> 3. For serverless/on-demand backends, use `mode=direct` to skip probing entirely.

---

## How host health works

The proxy polls every configured backend every `PollInterval` ms. Each poll result is recorded. A host stays in the active pool as long as its recent success rate stays above `SuccessRate` (default 80%).

```
Poll result: success → rate increases
Poll result: failure → rate decreases
rate < SuccessRate % → host removed from active pool (still polled)
rate >= SuccessRate % → host added back automatically
```

---

## Step 1 — Verify the host URL and probe path

Test the probe directly from the environment where the proxy runs:

```bash
# Standard probe path
curl -v https://<backend-host>/<probe-path>

# Example
curl -v https://api.backend.com/echo/resource?param1=sample
```

The probe must return a 2xx response. Any non-2xx is recorded as a failure.

---

## Step 2 — Check host configuration syntax

The connection string format is recommended. An **unrecognised key** causes `UriFormatException` at startup and prevents the proxy from starting.

```bash
# Correct
Host1="host=https://api.backend.com;probe=/health;path=/api"

# Wrong — 'url=' is not a valid key
Host1="url=https://api.backend.com;probe=/health"
```

| Key | Notes |
|-----|-------|
| `host` | Required. Protocol defaults to `https://` if omitted. |
| `probe` | Default: `echo/resource?param1=sample`. Must return 2xx. |
| `path` | Optional path prefix for routing. Default `/`. |
| `mode=direct` | Disables all probing. Host is always healthy. |

---

## Step 3 — Tune poll settings

| Setting | Env Var | App Config key | Default |
|---------|---------|----------------|---------|
| Poll interval (ms) | `PollInterval=<ms>` | `Cold:Server:PollInterval` | 10000 |
| Probe timeout (ms) | `PollTimeout=<ms>` | `Cold:Server:PollTimeout` | 5000 |
| Min success rate (%) | `SuccessRate=<n>` | `Cold:CircuitBreaker:SuccessRate` | 80 |

> [!TIP]
> If `PollTimeout` is shorter than the backend's warm-up time, every probe fails. For slow-starting backends, increase `PollTimeout` or use `mode=direct` and let the circuit breaker handle failures.

---

## Using `mode=direct` for serverless backends

Backends that scale to zero (Azure Functions, Container Apps with min replicas = 0) should use `mode=direct`. This prevents the health poller from waking them unnecessarily and guarantees they are always in the active pool.

```bash
Host3="host=https://my-func.azurewebsites.net;mode=direct;path=/api/v1"
```

In direct mode, the circuit breaker still tracks per-request failures — the host is excluded once it breaches `CBErrorThreshold` failures within `CBTimeslice`.

> [!WARNING]
> In direct mode, there is no readiness check at startup. The first real request is the first probe. Ensure your backend is able to handle a cold-start request.

---

## Authenticated backends (managed identity / OAuth)

If the backend requires a Bearer token, set `usemi=true` and provide `audience`:

```bash
Host2="host=https://secure-api.internal;usemi=true;audience=api://my-app-id;probe=/health"
```

The proxy acquires a token from the managed identity endpoint. If the token acquisition fails, probe requests will receive `401` and the host will fail health checks.

**Fix:** Verify the proxy managed identity has the required role on the backend API and that `audience` matches the backend's app registration.

---

## Related

- [BACKEND_HOSTS.md](../BACKEND_HOSTS.md) — full host configuration reference
- [HEALTH_CHECKING.md](../HEALTH_CHECKING.md) — health endpoint reference
- [circuit-breaker.md](circuit-breaker.md) — circuit breaker troubleshooting
- [health-probes.md](health-probes.md) — Kubernetes probe configuration
