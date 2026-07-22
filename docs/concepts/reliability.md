# Reliability

Understand how health probes, backend selection, retries, requeue, and circuit breaking work together.

## TL;DR

- Health checks determine which probed backends enter the active pool.
- Load balancing orders eligible backends; retries advance through that order.
- Circuit breakers stop repeated calls to failing backends until they recover.

## Failure-Handling Flow

**A backend must pass routing, health, and circuit checks before it receives a request.**

```text
select → call → success
          └─ failure → retry next backend → requeue or final error
```

The proxy filters backends by request path, removes unavailable or circuit-blocked hosts, and orders the remainder using the selected load-balancing mode. A failed attempt can advance to another backend while the request TTL and retry policy allow it.

> [!NOTE]
> APIM policy can add priority-aware backend eligibility before the proxy processes the APIM response.

## Canonical Details

| Mechanism | Document |
|-----------|----------|
| Backend eligibility and ordering | [Load Balancing](../reference/load-balancing.md) |
| Circuit states and thresholds | [Circuit Breaker](../reference/circuit-breaker.md) |
| Probe behavior and readiness | [Health Endpoints](../reference/health-endpoints.md) |
| Attempt timeout and total TTL | [Timeouts](../reference/timeouts.md) |

> [!WARNING]
> Increasing retry counts without increasing the TTL cannot create more request budget.
