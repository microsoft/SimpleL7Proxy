# Diagnose 503 Service Unavailable

Distinguish proxy-generated, health-probe, and backend-derived `503` responses before changing host or retry configuration.

## TL;DR

1. Capture the response body, content type, and all headers; `503` does not prove that every backend was attempted.
2. Use attempt counters and generated request summaries to determine whether any host was called or only skipped.
3. Treat health-endpoint `503` responses and shutdown rejection as separate response paths.

## Capture the Response

**Preserve the exact response because body shape and headers identify the producing path.**

```bash
curl -sS -D response.headers -o response.body \
  -H "S7PDEBUG: true" http://<proxy-host>/<request-path>
cat response.headers response.body
```

Correlate the result with proxy logs by `S7P-ID`, `x-MID`, or request ID. Record path-matching logs, circuit-breaker skips, attempt events, shutdown logs, and backend or APIM diagnostics.

## Identify the Response Origin

**Classify the response before diagnosing backend availability.**

| Evidence | Origin | Meaning |
|----------|--------|---------|
| Plain-text body `Server is shutting down.` with `S7P-ID` | Proxy admission | The listener rejected the request during shutdown; no backend attempt was required |
| Plain-text body `Not Healthy.  Active Hosts: 0` or `Not Healthy.  Failed Hosts: True` from `/readiness` or `/startup` | Health endpoint | The health aggregate is not ready; this is not an exhausted request |
| Body starts `Error processing request.  No active hosts were able to handle the request.` followed by `Request Summary:` and a JSON object keyed as `Attempt-1`, `Attempt-2`, and so on | Proxy worker | Host iteration ended without a successful response |
| Generated body has an empty request-summary object and `Attempts: 0` | Proxy worker | No eligible host was attempted; path selection, active-host state, or host skips can produce this |
| Generated body contains attempt entries whose `Status` values are `503` | Backend-derived final status | One or more backends returned `503`; the proxy generated the summary after iteration |
| Original backend body and normal successful-response header family | Backend response | A status accepted by the active response path was returned without becoming the generated exhausted-host format |

The generated exhausted-host body is not the schema `{ "status": 503, "message": ..., "attempts": [...] }`. It is text followed by a serialized object of attempt summaries:

```text
Error processing request.  No active hosts were able to handle the request.
Request Summary:
{
  "Attempt-1": { "Status": "503", "Backend-Host": "https://..." }
}
```

Attempt fields vary by failure path. Diagnose from fields that are actually present rather than expecting a fixed JSON schema.

## Interpret Response Headers

**Use the header family to identify the response path; the names are not uniform across all responses.**

| Response path | Headers to inspect |
|---------------|--------------------|
| Generated exhausted-host error | `x-Request-Queue-Duration`, `x-Total-Latency`, `x-ProxyHost`, `x-MID`, `Attempts`, `Lifetime-Attempts`, `Model` |
| Normal proxied response | `Request-Queue-Duration`, `Request-Process-Duration`, `Total-Latency`, `BackendHost`, `Attempts`, `Lifetime-Attempts` |
| Admission rejection during shutdown | `S7P-ID`, `Retry-After` |
| Main or sidecar health endpoint | Status and plain-text body; request-attempt headers are not expected |

An exhausted-host error does not consistently emit `x-Request-Process-Duration` or `BackendHost`. Use `Attempts` and the body’s attempt summaries to identify calls that occurred.

## Diagnose Zero or Skipped Attempts

**A `503` with zero attempts means iteration produced no callable host, not that every configured backend failed a request.**

Check for:

- No active hosts when the worker creates its iterator.
- No host whose configured `path` matches the request path.
- Hosts skipped because their circuit checks report failed after the request was queued.
- A host set that changed between admission, enqueue, and worker dispatch.
- Shutdown or async eviction paths that cancel work instead of completing a backend call.

```bash
curl -i http://<proxy-host>/healthdetail
curl -i http://<proxy-host>/readiness
```

Use `/healthdetail` and `[ProxyToBackEnd:<id>]` logs to compare configured paths, active hosts, circuit status, and the logged `Found <n> backend hosts` value. Readiness is supporting evidence only; it does not identify why a particular request had zero eligible hosts.

## Diagnose Attempted Backends

**Read each generated `Attempt-N` entry to identify the actual status or transport failure.**

- If every recorded status is `503`, the final client status can remain `503` while the proxy replaces the backend body with its generated attempt summary.
- If recorded statuses differ, the proxy can return `502 Bad Gateway` instead of `503`.
- If no attempt status exists, the default exhausted-host status is `503`.
- Backend statuses handled by the normal response path can determine the client response instead of being converted to `503`; the status code alone therefore does not prove proxy origin.
- `AcceptableStatusCodes` controls circuit-breaker failure accounting. Do not assume that adding `503` guarantees direct pass-through of the original backend response body.

Test the destination URL and authentication shown in each attempt, then compare the backend response with the proxy summary. For path-routed hosts, verify `path` and `stripprefix` behavior before changing retry limits.

## Diagnose Health-Endpoint 503

**Health responses describe aggregate process state, not backend-attempt exhaustion.**

`/readiness` and `/startup` can return `503` for incomplete startup, zero active hosts, excessive event backlog, an unhealthy blob queue, circuit status, or event-client health. A sidecar can also return `503` when status updates from the main process become stale. Use [Health Probe Troubleshooting](health-probes.md) for the complete body-to-cause mapping.

## Related

- [Response Codes and Headers](../reference/headers-and-status-codes.md)
- [Backend Host Configuration](../reference/backend-hosts.md)
- [Circuit Breaker Reference](../reference/circuit-breaker.md)
- [Circuit Breaker Troubleshooting](circuit-breaker.md)
- [Backend Host Troubleshooting](backend-hosts.md)
