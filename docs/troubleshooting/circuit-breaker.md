# Circuit Breaker Stuck Open

> **TL;DR**
> The circuit breaker self-heals — it closes automatically once old failures age out of the sliding window. If it stays open, the backends are still actively failing or `CBTimeslice` is set very large.

---

## How the circuit breaker works

The circuit breaker tracks failure timestamps in a sliding window. It opens when the count inside the window exceeds `CBErrorThreshold`. Once failures age out (older than `CBTimeslice` seconds), the count drops and the circuit closes itself — no manual reset is needed.

```
Failures in last CBTimeslice seconds >= CBErrorThreshold → OPEN (host skipped)
Failures in last CBTimeslice seconds <  CBErrorThreshold → CLOSED (host used)
```

---

## Diagnose

### Check readiness probe

```bash
curl -v http://<proxy-host>/readiness
# 503 → at least one circuit is open
# 200 → all circuits are closed
```

### Check logs

Search for `[CB-DELAY]` and `Circuit breaker BLOCKING` log entries:

```
[CB-DELAY] Circuit breaker <id> is experiencing elevated error rates. Count: 42, Introducing delay: 300ms
[ProxyToBackEnd] ⚠ Circuit breaker BLOCKING host: https://api.backend.com
```

The count in `[CB-DELAY]` tells you how close you are to the threshold.

---

## Common causes and fixes

### Backends are genuinely failing

The circuit is doing its job. Fix the backend first.

```bash
# Test the backend probe path directly
curl -v <backend-url>/<probe-path>
```

### Threshold too low for normal error rate

If backends occasionally return 5xx during normal operation (e.g., transient errors), the circuit may open too easily.

**Fix:** Raise `CBErrorThreshold` or increase `CBTimeslice` so transient bursts don't accumulate enough to trip the circuit.

| Setting | Env Var | App Config key |
|---------|---------|----------------|
| Failure threshold | `CBErrorThreshold=<n>` | `Warm:CircuitBreaker:ErrorThreshold` |
| Window width (seconds) | `CBTimeslice=<n>` | `Warm:CircuitBreaker:Timeslice` |

> [!NOTE]
> Both settings have the `Warm:` prefix — update them in App Configuration and bump `Warm:Sentinel`; no restart needed.

### Status codes counted as failures incorrectly

By default, any code not in `AcceptableStatusCodes` counts as a failure. If a backend legitimately returns `503` or `500` in normal operation, add it to the acceptable list to stop it triggering the circuit breaker.

| Setting | Env Var | App Config key |
|---------|---------|----------------|
| Acceptable codes | `AcceptableStatusCodes=[200,202,503]` | `Warm:Response:AcceptableStatusCodes` |

### Window too large — old failures keeping circuit open

If `CBTimeslice` is very large (e.g., 3600 s), a burst of failures from an hour ago is still counted.

**Fix:** Reduce `CBTimeslice` so the window reflects recent behaviour.

### Progressive delay making requests slow before full open

As the failure count approaches the threshold, the proxy adds a 100–500 ms artificial delay per request. If you see elevated latency but no 429s, the circuit is in the delay zone (50–99% of threshold).

This is intentional — it slows traffic to the struggling host before fully blocking it. No action is needed unless the latency is unacceptable, in which case raise `CBErrorThreshold`.

---

## Related

- [CIRCUIT_BREAKER.md](../reference/circuit-breaker.md) — full circuit breaker reference
- [requests-429.md](requests-429.md) — 429 responses caused by open circuits
- [backend-hosts.md](backend-hosts.md) — backend health and probing
