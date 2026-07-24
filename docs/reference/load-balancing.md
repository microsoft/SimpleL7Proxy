# Load Balancing & Backend Selection

The proxy selects backends through a three-stage pipeline on every request: filter by path → order by load-balance mode → gate by circuit breaker.

> **TL;DR**
> - **Specific-path hosts always win** — if any configured host matches the request path, catch-all hosts are never tried.
> - **`LoadBalanceMode`** controls host order (round-robin / latency / random); **`IterationMode`** controls how many attempts are made.
> - **A `429` with `S7PREQUEUE`** makes the request eligible for requeue after attempts are exhausted; acceptable status codes return directly; TTL expiry stops iteration with `412`.

---

## Reference — All Settings

| Variable | Default | Description |
|----------|---------|-------------|
| `LoadBalanceMode` | `latency` | Host ordering: `roundrobin`, `latency`, or `random` |
| `IterationMode` | `SinglePass` | Retry strategy: `SinglePass` or `MultiPass` |
| `MaxAttempts` | `10` | Max total attempts (MultiPass only) |
| `UseSharedIterators` | `true` | Share iterator state across concurrent requests to the same path |
| `SharedIteratorTTLSeconds` | `60` | Seconds before an unused shared iterator is discarded |
| `SharedIteratorCleanupIntervalSeconds` | `30` | How often expired shared iterators are cleaned up |

---

## Request Flow

```
Request arrives
      │
      ▼
┌─────────────────────────────────────────────────────┐
│ 1. PATH FILTER                                      │
│    Specific-path hosts match?  ──Yes──► use them    │
│                                ──No───► use catch-all│
└─────────────────────────────────────────────────────┘
      │
      ▼
┌─────────────────────────────────────────────────────┐
│ 2. ITERATOR  (LoadBalanceMode)                      │
│    roundrobin → global counter order                │
│    latency    → sorted lowest avg latency first     │
│    random     → shuffled each request               │
└─────────────────────────────────────────────────────┘
      │
      ▼
┌─────────────────────────────────────────────────────┐
│ 3. FOR EACH HOST  (IterationMode / MaxAttempts)     │
│    circuit OPEN?  ──Yes──► skip, next host          │
│    TTL expired?   ──Yes──► 412, stop                │
│    send request                                     │
│    acceptable?    ──Yes──► return to client ✓       │
│    429+S7PREQUEUE ──────► collect, try next host    │
│    other failure  ──────► try next host             │
└─────────────────────────────────────────────────────┘
      │
      ▼  (all hosts exhausted)
  Any 429+S7PREQUEUE? → requeue with shortest eligible delay
  Else               → 502 or 503 based on failure type
```

**The circuit-breaker gate means an OPEN host is never attempted, so `MaxAttempts` counts only hosts actually tried.**

---

## Selecting a Backend

**Rule: The path filter runs first; within the matched set, `LoadBalanceMode` determines which host is tried first.**

```bash
LoadBalanceMode=latency   # try fastest host first
# Hosts sorted by average response time, lowest first
```

| Mode | Best for |
|------|----------|
| `roundrobin` | Homogeneous backends; fair distribution |
| `latency` | Backends with measurably different response times |
| `random` | Avoiding predictable traffic patterns |

> [!NOTE]
> **Default:** `LoadBalanceMode=latency`. Path prefix is stripped before forwarding unless `stripprefix=false` is set on the host (see [BACKEND_HOSTS.md](backend-hosts.md#configuring-hosts)).

> [!TIP]
> **Troubleshooting:** If a specific host is never reached, verify its configured path prefix matches the inbound request path; a mismatch silently excludes it from the candidate set.

---

## Retrying Across Backends

**Rule: `SinglePass` tries each host once; `MultiPass` cycles through all hosts up to `MaxAttempts` total.**

```bash
IterationMode=MultiPass
MaxAttempts=7
# 3 hosts → up to 2 full passes + 1 extra attempt
```

> [!NOTE]
> **Default:** `IterationMode=SinglePass`. `MaxAttempts` is ignored in SinglePass mode.

> [!TIP]
> **Troubleshooting:** Seeing more failures than expected? Check circuit-breaker state and active-host health. OPEN circuits are skipped and do not consume the `MaxAttempts` budget.

<details>
<summary>Shared Iterators</summary>

Set `UseSharedIterators=true` when many concurrent requests target the same path and you need strict round-robin fairness across them. Each path then maintains a single shared counter instead of per-request counters.

</details>

---

## Handling Responses

**Rule: Every status in `AcceptableStatusCodes` returns to the client; other responses retry, requeue, or stop.**

| Response | Action |
|----------|--------|
| Any `AcceptableStatusCodes` value | Return to client without retry or circuit recording |
| Any non-acceptable status | Try next host |
| `429` + `S7PREQUEUE: true` | Collect; try next host. After attempts are exhausted, requeue if any eligible response was collected, using the shortest delay. |
| `412` Precondition Failed | TTL expired — stop, no further retries |
| Attempts exhausted without requeue | `502` for mixed backend statuses or an unclassified transport error; `503` when no host was attempted or name-resolution/connection failures exhaust hosts |

> [!WARNING]
> **Error:** `412` means the request's TTL expired during iteration. Increase `DefaultTTLSecs` or reduce backend latency — adding more `MaxAttempts` will not help once TTL is gone.

---

<details>
<summary>Worked Example</summary>

> **Setup:** 3 hosts (`A avg 200 ms`, `B avg 80 ms`, `C avg 150 ms`), `LoadBalanceMode=latency`, `IterationMode=MultiPass`, `MaxAttempts=5`.

| Attempt | Host tried (latency order) | Response | Action |
|---------|---------------------------|----------|---------|
| 1 | B (80 ms — fastest) | 503 | try next |
| 2 | C (150 ms) | circuit OPEN | skip (no attempt counted) |
| 3 | A (200 ms) | 503 | try next |
| 4 | B (second pass) | 200 | **return to client** |

**Attempts used: 4 of 5. Host C's open circuit was skipped without spending an attempt budget entry.**

</details>

---

<details>
<summary>Monitoring & Diagnostics</summary>

Enable header logging to trace backend selection:

```bash
LogHeaders=true
```

Key response headers to inspect:

| Header | Meaning |
|--------|---------|
| `Backend-Host` | Host that ultimately served the request |
| `BackendAttempts` | Number of hosts tried |
| `Total-Latency` | End-to-end request duration |

</details>

---

## Related Documentation

- [BACKEND_HOSTS.md](backend-hosts.md) — Host configuration and path prefixes
- [CIRCUIT_BREAKER.md](circuit-breaker.md) — Circuit breaker configuration
- [CONFIGURATION_SETTINGS.md](configuration.md) — All configuration options
