# Diagnose 412 Precondition Failed

Distinguish proxy TTL expiration from a backend-originated `412` before changing queue or timeout settings.

## TL;DR

1. A `412` can be generated when the proxy TTL expires or can originate from a backend response.
2. Queue duration is supporting evidence; compare the body, attempt summaries, `Expires-At`, and backend logs.
3. Increasing `MaxQueueLength` does not reduce wait time and can increase the number of requests that expire in the queue.

## Reference Settings

| Setting | Default | Unit | Reload | Purpose |
|---------|---------|------|--------|---------|
| `DefaultTTLSecs` | `300` | seconds | Warm | Total request lifetime from enqueue across queueing, attempts, and requeues |
| `S7PTTL` | none | seconds or supported absolute timestamp | Per request | Overrides the default expiration for that request |
| `Timeout` | `120000` | milliseconds | Warm | Per-attempt limit, capped by the remaining TTL |
| `MaxQueueLength` | `1000` | requests | Cold | Admission capacity; it does not accelerate queue drain |
| `Workers` | `10` | workers | Cold | Concurrent proxy workers; the default is intended for local testing |

## Capture the Evidence

**Preserve response and attempt evidence before retrying.**

```bash
curl -sS -D response.headers -o response.body \
    -H "S7PDEBUG: true" http://<proxy-host>/<request-path>
cat response.headers response.body
```

Correlate the response with proxy logs by request ID. Record `EnqueueTime`, `ExpiresAt`, `Expires-At` in attempt events, queue duration, attempt count, backend host, and the request state where `412` was recorded.

## Identify the Source

**Use body and request-stage evidence; status and queue duration alone are not conclusive.**

| Evidence | Source | Interpretation |
|----------|--------|----------------|
| Body contains `Request has expired: Time: ... Reason: ...` and logs contain `validation failed: expired at` | Proxy TTL check | The proxy compared the current time with `ExpiresAt` before a backend attempt or retry |
| Generated exhausted-host body contains `Attempt-N` entries with `Status: 412` and backend details | Backend-derived status | A backend returned `412`; host iteration recorded it and the final status resolved to `412` |
| Original backend response body or backend-specific headers are present | Backend response | The backend response path determined the client result |
| Queue duration is near the TTL, with no backend attempt | Likely queue-stage expiration | Confirm with the proxy expiration body and logs before assigning the cause |
| One or more attempts or requeue events occurred before expiration | Processing or retry-stage expiration | The TTL budget was consumed after dequeue, during backend work, or while retrying |

`412` is included in the default `AcceptableStatusCodes`, so it is not recorded as a circuit-breaker failure. A backend-originated `412` can therefore determine the client status rather than being converted to `503`. The worker can still advance to another host after a backend `412`; if exhausted attempt statuses resolve to `412`, the client receives a generated attempt summary with status `412` instead of necessarily receiving the original backend body. Do not assume that every `412` means the proxy rejected the request before contacting a backend.

## Understand the TTL Scope

**TTL is a total wall-clock budget, not only a queue-wait limit.**

```text
EnqueueTime + TTL = ExpiresAt
queue wait + backend attempts + retry/requeue delays must finish before ExpiresAt
```

The worker validates expiration before each backend send. It also caps an attempt timeout at the earlier of the per-attempt timeout and `ExpiresAt`. A request can therefore expire:

- Before the first backend attempt after waiting in the queue.
- Before a later host attempt.
- After backend processing consumes the remaining TTL.
- During delayed retry or `S7PREQUEUE` requeue handling.

Queue duration can show that substantial budget was spent waiting, but it cannot prove whether the final `412` came from the proxy or a backend.

## Correct Proxy TTL Expiration

**Change the constraint that consumed the TTL budget rather than increasing limits blindly.**

- Confirm clients are not sending an unexpectedly small or absolute `S7PTTL` value.
- Increase `DefaultTTLSecs` or the per-request TTL only when the longer end-to-end wait is acceptable.
- Reduce queue wait by addressing the measured bottleneck: arrival bursts, worker saturation, backend latency, retry volume, or downstream quota.
- Increase `Workers` only when worker concurrency is the constraint and backends can accept more parallel work.
- Reduce per-attempt timeout only when faster failure and failover are appropriate.
- Do not use a larger `MaxQueueLength` as a latency fix. It admits more waiting requests, can lengthen queue time, and can increase expiration risk.

```bash
curl -H "S7PTTL: 600" http://<proxy-host>/<request-path>
```

> [!WARNING]
> A client-supplied `S7PTTL` overrides `DefaultTTLSecs` for that request.

## Correct Backend-Originated 412

**Investigate the backend precondition instead of changing proxy TTL.**

Inspect the attempt’s destination, backend body, and headers. Common HTTP preconditions include conditional request headers, resource versions, or backend-specific validation, but use the backend’s actual response rather than assuming one of these causes. Reproduce the same request directly against the recorded backend when access and authentication permit.

## Related

- [Timeouts and TTL](../reference/timeouts.md)
- [Response Codes and Headers](../reference/headers-and-status-codes.md)
- [429 Troubleshooting](requests-429.md)
