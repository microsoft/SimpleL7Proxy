# Load Balancing & Backend Selection

SimpleL7Proxy uses a sophisticated multi-stage algorithm to select the optimal backend for each request. This document explains how backends are chosen, filtered, and iterated.

## Algorithm Overview

```
REQUEST ARRIVES
       │
       ▼
┌──────────────────────────┐
│ 1. Filter hosts by path  │  → Specific path hosts OR catch-all hosts
└──────────────────────────┘
       │
       ▼
┌──────────────────────────┐
│ 2. Create Iterator       │  → RoundRobin / Latency / Random
│    (LoadBalanceMode)     │
└──────────────────────────┘
       │
       ▼
┌──────────────────────────┐
│ 3. FOR EACH HOST:        │
│    ├─ Circuit breaker OK?│  → Skip if OPEN
│    ├─ TTL not expired?   │  → 412 if expired
│    ├─ Send request       │
│    └─ Success? → RETURN  │
└──────────────────────────┘
       │
       ▼ (all hosts failed)
┌──────────────────────────┐
│ 429s collected? → Requeue│
│ Else → 503 Service       │
│         Unavailable      │
└──────────────────────────┘
```

---

## Stage 1: Path-Based Host Filtering

Before load balancing, hosts are filtered based on the request path. The proxy maintains two categories of hosts:

| Category | Description | Example Path |
|----------|-------------|--------------|
| **Specific Path Hosts** | Hosts with explicit path prefixes | `/api/v1/*`, `/chat/*`, `/embeddings` |
| **Catch-All Hosts** | Hosts that handle any path | `/` or `/*` |

### Matching Rules

1. **Specific paths take precedence**: If any host's path matches the request, only those hosts are used.
2. **Path prefix is stripped by default**: When forwarding to a matched host, the matching prefix is removed from the request path. This can be disabled per-host with `stripprefix=false` (see [BACKEND_HOSTS.md](BACKEND_HOSTS.md#controlling-path-prefix-stripping)).
3. **Catch-all fallback**: If no specific path matches, catch-all hosts are used with the original path.

### Example

```
Configured Hosts:
  Host1: path=/api/v1     → https://api-v1.internal
  Host2: path=/api/v2     → https://api-v2.internal
  Host3: path=/           → https://default.internal

Request: GET /api/v1/users/123

Result (stripprefix=true, default):
  - Matches Host1 (specific path /api/v1)
  - Forwarded as: GET /users/123 to https://api-v1.internal
  - Host2 and Host3 are NOT considered

Result (if Host1 had stripprefix=false):
  - Matches Host1 (specific path /api/v1)
  - Forwarded as: GET /api/v1/users/123 to https://api-v1.internal
  - Original path is preserved
```

---

## Stage 2: Load Balance Mode

Once hosts are filtered, an iterator is created based on the configured `LoadBalanceMode`.

### Available Modes

| Mode | Environment Variable | Behavior |
|------|---------------------|----------|
| **Round Robin** | `LoadBalanceMode=roundrobin` | Uses a **global counter** shared across all workers. Each request gets the "next" host, ensuring fair distribution. |
| **Latency** | `LoadBalanceMode=latency` | Hosts are **sorted by average latency** (lowest first). Fastest hosts are tried first. |
| **Random** | `LoadBalanceMode=random` | Hosts are **shuffled randomly** for each request. All hosts are tried but in unpredictable order. |

### Configuration

```bash
# Default is random
LoadBalanceMode=latency
```

### When to Use Each Mode

| Scenario | Recommended Mode |
|----------|------------------|
| All backends have equal capacity | `roundrobin` |
| Backends have different response times | `latency` |
| Want to avoid predictable patterns | `random` |
| Testing/debugging specific hosts | `roundrobin` with single host |

---

## Stage 3: Iteration Mode

The iteration mode controls how many times the proxy attempts to reach backends before giving up.

| Mode | Environment Variable | Behavior |
|------|---------------------|----------|
| **SinglePass** | `IterationMode=SinglePass` | Try each matching host **once**. If all fail → error. |
| **MultiPass** | `IterationMode=MultiPass` | Retry across all hosts up to `MaxAttempts` total. Will cycle through hosts multiple times. |

### Configuration

```bash
IterationMode=SinglePass
MaxAttempts=30  # Only used in MultiPass mode
```

### Example: MultiPass with 3 Hosts

```
Hosts: [A, B, C]
MaxAttempts: 7

Attempt 1: Host A → 503 (fail)
Attempt 2: Host B → 503 (fail)
Attempt 3: Host C → 503 (fail)
Attempt 4: Host A → 503 (fail)  # Second pass begins
Attempt 5: Host B → 503 (fail)
Attempt 6: Host C → 503 (fail)
Attempt 7: Host A → 200 (success!) ✓
```

---

## Stage 4: Shared vs Per-Request Iterators

Control whether concurrent requests share iterator state or each get their own.

| Setting | Behavior |
|---------|----------|
| `UseSharedIterators=false` (default) | Each request gets its **own iterator**. Simple but may cause uneven distribution under high concurrency. |
| `UseSharedIterators=true` | Requests to the **same path** share an iterator. Ensures fair distribution across concurrent requests. |

### When to Use Shared Iterators

- **High concurrency**: Many simultaneous requests to the same path
- **Fair distribution required**: Need to ensure all backends get equal traffic
- **Round-robin mode**: Most beneficial when combined with `roundrobin`

### Configuration

```bash
UseSharedIterators=true
SharedIteratorTTLSeconds=300          # How long to keep unused iterators
SharedIteratorCleanupIntervalSeconds=60  # Cleanup frequency
```

---

## Stage 5: Per-Host Circuit Breaker Check

Before sending a request to each host, the circuit breaker status is checked.

```
FOR EACH HOST in iterator:
    └─ CheckFailedStatus() ──[OPEN]──► SKIP (continue to next host)
                            └─[CLOSED]──► Proceed with request
```

- **OPEN circuit**: Host is skipped immediately, no request sent
- **CLOSED circuit**: Request is attempted
- **All circuits OPEN**: Returns `503 Service Unavailable`

See [CIRCUIT_BREAKER.md](CIRCUIT_BREAKER.md) for detailed circuit breaker configuration.

---

## Response Handling

After sending a request, the response determines the next action:

| Response | Action |
|----------|--------|
| `2xx` (Success) | Return response to client ✓ |
| `3xx`, `404`, `5xx` | Try next host |
| `429` with `S7PREQUEUE` header | Collect for potential requeue, try next host |
| `412` (Precondition Failed) | Request TTL expired, stop iteration |

### Requeue Behavior

If all hosts return `429` with the `S7PREQUEUE` header, the request is requeued with a delay based on the shortest `retry-after` value.

---

## Monitoring & Diagnostics

### Logging

Enable debug logging to see backend selection:

```bash
LogHeaders=true
```

Log output includes:
- Which hosts matched the path
- Which host was selected
- Circuit breaker status for skipped hosts
- Attempt count and duration

### Metrics

Key metrics to monitor:
- `BackendAttempts`: Number of hosts tried per request
- `Backend-Host`: Which host ultimately served the request
- `Total-Latency`: End-to-end request duration

---

## Configuration Summary

| Variable | Default | Description |
|----------|---------|-------------|
| `LoadBalanceMode` | `random` | Algorithm: `roundrobin`, `latency`, or `random` |
| `IterationMode` | `SinglePass` | Retry strategy: `SinglePass` or `MultiPass` |
| `MaxAttempts` | `30` | Max total attempts (MultiPass only) |
| `UseSharedIterators` | `false` | Share iterators across concurrent requests |
| `SharedIteratorTTLSeconds` | `300` | TTL for unused shared iterators |
| `SharedIteratorCleanupIntervalSeconds` | `60` | Cleanup interval for expired iterators |

---

## Related Documentation

- [BACKEND_HOSTS.md](BACKEND_HOSTS.md) - Host configuration and connection strings
- [CIRCUIT_BREAKER.md](CIRCUIT_BREAKER.md) - Circuit breaker configuration
- [CONFIGURATION_SETTINGS.md](CONFIGURATION_SETTINGS.md) - All configuration options
