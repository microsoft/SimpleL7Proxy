# Getting 503 Service Unavailable

> **TL;DR**
> A `503` from the proxy means every backend was tried and every attempt failed. This is different from a `429` (rejected before sending) — a `503` means the proxy exhausted all hosts.

---

## Diagnose the cause

The proxy includes a JSON error body with per-attempt details. Read it:

```json
{
  "status": 503,
  "message": "All backends failed",
  "attempts": [
    { "host": "https://api1.backend.com", "code": 500, "error": "Internal Server Error" },
    { "host": "https://api2.backend.com", "code": 502, "error": "Bad Gateway" }
  ]
}
```

Response headers also show:
- `x-Request-Process-Duration` — time the proxy spent trying backends
- `BackendHost` — last backend attempted

---

## Common causes

### All backends returning 5xx

The backends themselves are failing. The proxy retries each host in sequence; when all fail with non-`AcceptableStatusCodes` responses, it returns 503.

**Fix:**
- Check each backend directly: `curl <backend-url>/<probe-path>`
- If the backend is temporarily overloaded, add it to `AcceptableStatusCodes` to pass its status through instead of retrying.

| Setting | Env Var | App Config key |
|---------|---------|----------------|
| Acceptable codes | `AcceptableStatusCodes=[200,202,503]` | `Warm:Response:AcceptableStatusCodes` |

> [!NOTE]
> Adding a code to `AcceptableStatusCodes` means it will be returned directly to the client **and** will not count as a circuit-breaker failure.

### Circuit breakers blocked all hosts before the request arrived

If all circuits opened between request enqueue and dequeue, the proxy skips every host. The result is a 503 with no actual backend attempts.

Check the circuit breaker status: `curl http://<proxy-host>/readiness`

> [!TIP]
> See [circuit-breaker.md](circuit-breaker.md) for recovery steps.

### Backends returning codes in 3xx or 404

Responses listed in `AcceptableStatusCodes` return directly to the client. The default list includes `404`; redirects retry unless explicitly added to that setting.

**Fix:** Verify the backend URLs and path routing are correct. Check `stripprefix` settings on each host — a stripped prefix may produce the wrong downstream path.

### All hosts excluded from path routing

If a request path does not match any configured host `path` prefix, no hosts are eligible and the proxy returns 503 immediately.

**Fix:** Verify host `path` configuration matches the request paths you are sending.

```bash
# Example: only requests starting with /api/v1 go to Host1
Host1="host=https://api.backend.com;path=/api/v1;probe=/health"
```

---

## Related

- [RESPONSE_CODES.md](../RESPONSE_CODES.md) — full list of proxy-originated codes
- [BACKEND_HOSTS.md](../BACKEND_HOSTS.md) — host configuration reference
- [CIRCUIT_BREAKER.md](../CIRCUIT_BREAKER.md) — circuit breaker reference
- [circuit-breaker.md](circuit-breaker.md) — circuit breaker troubleshooting
- [backend-hosts.md](backend-hosts.md) — backend host troubleshooting
