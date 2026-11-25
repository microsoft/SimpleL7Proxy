# Verify SimpleL7Proxy

Confirm that the proxy is healthy and that a request reaches a configured backend.

## TL;DR

- Call `/liveness` to verify the process.
- Call `/readiness` to verify that the proxy is ready.
- Send a request in [Investigator](companionapp-investigator.md) and confirm the backend response.

| Signal | Expected value | Meaning |
|--------|----------------|---------|
| `/liveness` | `200 OK` | The proxy process is alive |
| `/readiness` | `200 OK` | Startup, backend, and queue health checks passed |
| `BackendHost` | Backend URL | The request passed through the proxy |

## Check Health

**Readiness is the required signal before sending a proxied request.**

```bash
PROXY_URL="https://<proxy-url>"
curl -i "$PROXY_URL/liveness"
curl -i "$PROXY_URL/readiness"
curl -i "$PROXY_URL/startup"
```

Replace `<proxy-url>` with the proxy URL from the deployment output. For local development, use `http://localhost:8000` instead.

> [!WARNING]
> A successful liveness response with failed readiness means the process is running but is not ready. Check startup, backend health, and event or blob queue backlogs.

## Send a Request

**Send a request in [Investigator](companionapp-investigator.md) and confirm the final status.**

For a command-line check, use a request from the [endpoint](connect-endpoint.md) or [APIM](connect-apim.md) guide. For the simulator, adapt a request from the [simulator guide](../../test/LLMSimulator/Readme.md) to the proxy URL. Inspect the response headers for `BackendHost`.

> [!TIP]
> Also inspect `x-Request-Worker`, `x-Request-Queue-Duration`, and `x-Request-Process-Duration` when diagnosing latency.

## Verification Checklist

- [ ] `/liveness` returns `200 OK`.
- [ ] `/readiness` returns `200 OK`.
- [ ] The request returns the expected backend response.
- [ ] `BackendHost` identifies the selected backend.
- [ ] If file event logging is enabled, `eventslog.json` contains the request event.

See [Health Endpoints](../reference/health-endpoints.md) and [Headers and Status Codes](../reference/headers-and-status-codes.md) for exact behavior.
