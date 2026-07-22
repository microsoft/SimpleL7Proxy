# Verify SimpleL7Proxy

Confirm that the proxy is healthy and that a request reaches a configured backend.

## TL;DR

- Call `/liveness` to verify the process.
- Call `/readiness` to verify that a backend is eligible.
- Send a request and confirm that the response contains `BackendHost`.

| Signal | Expected value | Meaning |
|--------|----------------|---------|
| `/liveness` | `200 OK` | The proxy process is alive |
| `/readiness` | `200 OK` | At least one backend can receive traffic |
| `BackendHost` | Backend URL | The request passed through the proxy |

## Check Health

**Readiness is the required signal before sending a proxied request.**

```bash
curl -i http://localhost:8000/liveness
curl -i http://localhost:8000/readiness
curl -i http://localhost:8000/startup
```

> [!WARNING]
> A successful liveness response with failed readiness means the process is running but no backend is currently eligible.

## Send a Request

**Confirm both the final status and the proxy response headers.**

```bash
curl -i http://localhost:8000/your/path
# Expect a BackendHost response header.
```

> [!TIP]
> Also inspect `x-Request-Worker`, `x-Request-Queue-Duration`, and `x-Request-Process-Duration` when diagnosing latency.

## Verification Checklist

- [ ] `/liveness` returns `200 OK`.
- [ ] `/readiness` returns `200 OK`.
- [ ] The request returns the expected backend response.
- [ ] `BackendHost` identifies the selected backend.
- [ ] `eventslog.json` contains the request event.

See [Health Endpoints](../reference/health-endpoints.md) and [Headers and Status Codes](../reference/headers-and-status-codes.md) for exact behavior.
