# Getting 412 Precondition Failed

> **TL;DR**
> `412` means the request's TTL expired while it was waiting in the priority queue — the proxy never sent it to a backend. Shorten queue wait time or increase the TTL.

---

## What causes 412

Every request has a time-to-live (TTL). The proxy stamps an `ExpiresAt` on each request when it is enqueued. If the request has not been dispatched to a backend by `ExpiresAt`, the worker discards it with a `412`.

```
Enqueue time + TTL = ExpiresAt
                          │
                          ▼
         Request reaches worker after ExpiresAt → 412
```

---

## Diagnose

Check the `x-Request-Queue-Duration` response header — if it equals or exceeds the TTL, the request expired in the queue.

```bash
curl -i http://<proxy-host>/api/...
# Look for:
# HTTP/1.1 412 Precondition Failed
# x-Request-Queue-Duration: 300123.4 ms   ← expired after 300 s
```

---

## Fix options

### 1 — Increase the default TTL

The default TTL is 300 s (`DefaultTTLSecs`). Increase it if requests legitimately take longer to process.

| Setting | Env Var | App Config key |
|---------|---------|----------------|
| Default TTL (seconds) | `DefaultTTLSecs=<n>` | `Warm:Request:DefaultTTLSecs` |

### 2 — Override TTL per request

Clients can send a per-request TTL via the `S7PTTL` header (value in seconds):

```bash
curl -H "S7PTTL: 600" http://<proxy-host>/api/...
```

> [!WARNING]
> A client-supplied `S7PTTL` **overrides** `DefaultTTLSecs` for that request. If clients send a very small value, they will see 412s regardless of the server default.

### 3 — Reduce queue wait time

If the queue is consistently long, workers are not draining fast enough. Options:

- Increase `Workers` count (Cold — restart required)
- Increase `MaxQueueLength` to smooth burst absorption
- Add more backend capacity
- Reduce per-attempt `Timeout` so failed attempts free workers faster

| Setting | Env Var | App Config key |
|---------|---------|----------------|
| Worker count | `Workers=<n>` | `Cold:Server:Workers` |
| Per-attempt timeout (ms) | `Timeout=<ms>` | `Warm:Request:DefaultTimeout` |

---

## Related

- [TIMEOUTS.md](../reference/timeouts.md) — TTL, Timeout, and AsyncTimeout interactions
- [RESPONSE_CODES.md](../reference/headers-and-status-codes.md) — full list of proxy-originated codes
- [requests-429.md](requests-429.md) — queue full (upstream cause of 412 under load)
