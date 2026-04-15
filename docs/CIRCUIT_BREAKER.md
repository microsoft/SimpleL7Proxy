# Circuit Breaker

The circuit breaker stops traffic to a failing backend host automatically, then restores it once recent failures drop back below the threshold — no manual intervention required.

> **TL;DR**
> - **Open circuit = host skipped** — the load balancer moves on to the next host without counting an attempt.
> - **Auto-recovery** — old failures age out of the sliding window; the circuit closes itself when the count drops below `CBErrorThreshold`.
> - **Progressive delays** — as failures accumulate toward the threshold, the proxy adds a small artificial delay (100–500 ms) to slow traffic before fully opening the circuit.

---

## Reference — Settings

| Config name | Default | Description |
|-------------|---------|-------------|
| `CBErrorThreshold` | `50` | Number of failures inside the window that opens the circuit |
| `CBTimeslice` | `60` s | Sliding window width — failures older than this are discarded |
| `AcceptableStatusCodes` | `[200,202,400,401,403,404,408,410,412,417]` | HTTP codes **not** counted as failures |

> [!NOTE]
> `CBErrorThreshold` and `CBTimeslice` are **Warm** settings — change them in Azure App Configuration and bump `Sentinel`; no restart needed.

---

## How the Circuit Breaker Works

```
Request to host
      │
      ▼
CheckFailedStatusAsync()
      │
      ├── failures in window < threshold?
      │       │
      │       ├── count ≥ 50% threshold → add delay (100–500 ms), then CLOSED → proceed
      │       └── count < 50% threshold → CLOSED → proceed immediately
      │
      └── failures in window ≥ threshold?
              │
              └── prune expired entries → still ≥ threshold?
                      ├── Yes → OPEN → return true (host skipped by load balancer)
                      └── No  → CLOSED → proceed (circuit self-heals)

TrackStatus(code, wasFailure, state) — called after every backend response
      │
      └── code not in AcceptableStatusCodes OR wasFailure=true
              └── enqueue failure timestamp → emit CircuitBreakerError event
```

**Progressive delay thresholds (not configurable):**

| Failure count | Delay added |
|---------------|-------------|
| ≥ 50% of threshold | 100 ms |
| ≥ 60% | 200 ms |
| ≥ 70% | 300 ms |
| ≥ 80% | 400 ms |
| ≥ 90% | 500 ms |

---

## Configuring the Circuit Breaker

**Rule: Lower `CBErrorThreshold` for fast failover; raise it for flaky backends you want to tolerate.**

```bash
# Fast failover — opens after 5 errors in 10 s
CBErrorThreshold=5
CBTimeslice=10

# Tolerant — absorbs bursts before opening
CBErrorThreshold=100
CBTimeslice=60
```

> [!NOTE]
> **Default:** `CBErrorThreshold=50`, `CBTimeslice=60`. At defaults, the circuit opens after 50 failures within the last 60 seconds.

> [!TIP]
> **Troubleshooting:** If hosts are opening too aggressively, check whether transient `5xx` codes are in `AcceptableStatusCodes`. Adding `503` to that list means 503 responses will not count as failures.

---

## Global Safety Net

**Rule: When every registered circuit breaker is OPEN simultaneously, the proxy returns `503` immediately without trying any host.**

`AreAllCircuitBreakersBlocked()` returns `true` when `blockedCount >= totalCount`. This prevents resource exhaustion when the entire backend tier is down.

> [!WARNING]
> **Error:** `503 Service Unavailable` with all circuit breakers OPEN means every backend has hit its failure threshold. Address the backend health issue — raising thresholds is a workaround, not a fix.

---

## Worked Example

> **Setup:** `CBErrorThreshold=10`, `CBTimeslice=30`. Three hosts A, B, C.

| Time | Event | Window failures | Circuit state |
|------|-------|-----------------|---------------|
| 0 s | Startup | 0 | CLOSED |
| 5 s | 8 failures from Host A | 8 | CLOSED + 400 ms delay (80%) |
| 10 s | 2 more failures | 10 | **OPEN** — Host A skipped |
| 10 s | Requests route to B, C | — | B=CLOSED, C=CLOSED |
| 40 s | All 10 failures age out of 30 s window | 0 | **Auto-CLOSED** — Host A back in pool |

**Host A rejoins the active pool automatically once all its failures age out of the `CBTimeslice` window — no restart or manual reset needed.**

---

## Integration with Load Balancing

During iteration the load balancer calls `CheckFailedStatusAsync()` before sending to each host:

```
FOR EACH HOST in iterator:
    CheckFailedStatusAsync()
        OPEN  → skip (no attempt counted)
        CLOSED → send request
                  success → return to client ✓
                  failure → TrackStatus() → try next host
```

See [LOAD_BALANCING.md](LOAD_BALANCING.md) for how hosts are ordered and how `MaxAttempts` interacts with skipped hosts.

---

## Related Documentation

- [BACKEND_HOSTS.md](BACKEND_HOSTS.md) — Per-host configuration and health polling
- [LOAD_BALANCING.md](LOAD_BALANCING.md) — Iterator and retry settings
- [CONFIGURATION_SETTINGS.md](CONFIGURATION_SETTINGS.md) — Full settings reference

