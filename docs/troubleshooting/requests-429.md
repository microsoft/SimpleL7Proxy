# Getting 429 Too Many Requests

> **TL;DR**
> A `429` from the proxy means it rejected the request before ever sending it to a backend. It is always one of three causes: queue full, all circuit breakers open, or no active hosts.

---

## Diagnose the cause

The proxy includes a reason in the response body. Inspect it first:

| Response body contains | Cause | Jump to |
|------------------------|-------|---------|
| `Circuit breaker on` | All backend circuit breakers are open | [Circuit breaker open](#circuit-breaker-open) |
| `Queue full` / `MaxQueueLength` | Incoming rate exceeds worker throughput | [Queue full](#queue-full) |
| `No active hosts` | No backends passed the health check | [No active hosts](#no-active-hosts) |
| `Max events` | Event logger buffer full | [Max undrained events](#max-undrained-events) |

---

## Circuit breaker open

All backend hosts have exceeded their failure threshold. The proxy will not attempt any backend until the failure window ages out.

**Immediate check:**

```bash
curl http://<proxy-host>/readiness
# Returns 503 when any circuit is open
```

**Fix:**
- Wait for the sliding window (`CBTimeslice`, default 60 s) to age out failures — the circuit self-heals.
- If backends are genuinely down, fix the backend first.
- To tune the threshold so the circuit opens less aggressively, raise `CBErrorThreshold`.

| Setting | Env Var | App Config key |
|---------|---------|----------------|
| Failure threshold | `CBErrorThreshold=<n>` | `Warm:CircuitBreaker:ErrorThreshold` |
| Window width (seconds) | `CBTimeslice=<n>` | `Warm:CircuitBreaker:Timeslice` |

> [!TIP]
> See [circuit-breaker.md](circuit-breaker.md) for a full diagnosis guide.

---

## Queue full

The priority queue has reached `MaxQueueLength`. New requests are rejected with 429 until workers drain the backlog.

**Fix options:**

1. **Increase worker count** — more workers drain the queue faster (Cold setting, requires restart).
2. **Increase queue length** — absorbs bursts, but increases memory usage (Cold setting, requires restart).
3. **Add more backend hosts** — higher throughput means faster drain.
4. **Reduce per-request timeout** — shorter timeouts free workers sooner.

| Setting | Env Var | App Config key |
|---------|---------|----------------|
| Queue size | `MaxQueueLength=<n>` | `Cold:Server:MaxQueueLength` |
| Worker count | `Workers=<n>` | `Cold:Server:Workers` |

---

## No active hosts

Every configured backend has failed health probes and been removed from the active pool.

**Check:**

```bash
curl http://<proxy-host>/readiness
# Body: "Not Healthy. Active Hosts: 0"
```

**Fix:**
- Verify backend URLs and probe paths are correct.
- Check backend health directly: `curl <backend-url>/<probe-path>`.
- Review `PollInterval` and `PollTimeout` — if they are too aggressive they may mark healthy backends as failed.

> [!TIP]
> See [backend-hosts.md](backend-hosts.md) for a full diagnosis guide.

---

## Max undrained events

The Event Hub logger buffer (`EVENTHUB_MAX_UNDRAINED_EVENTS`) is full. This typically means the Event Hub connection is degraded and events are not being flushed.

**Fix:**
- Check the Event Hub connection. See [event-hub.md](event-hub.md).
- Increase `EVENTHUB_MAX_UNDRAINED_EVENTS` to absorb spikes (Cold setting).

| Setting | Env Var | App Config key |
|---------|---------|----------------|
| Buffer limit | `EVENTHUB_MAX_UNDRAINED_EVENTS=<n>` | `Cold:Server:MaxUndrainedEvents` |

---

## Related

- [RESPONSE_CODES.md](../RESPONSE_CODES.md) — full list of proxy-originated codes
- [CIRCUIT_BREAKER.md](../CIRCUIT_BREAKER.md) — circuit breaker reference
- [circuit-breaker.md](circuit-breaker.md) — circuit breaker troubleshooting
- [backend-hosts.md](backend-hosts.md) — backend host troubleshooting
