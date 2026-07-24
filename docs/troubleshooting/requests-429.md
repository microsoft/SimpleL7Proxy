# Diagnose 429 Too Many Requests

Use response content, headers, logs, and request-stage evidence to determine where a `429` originated before changing capacity or retry settings.

## TL;DR

1. Capture the response body and headers; a `429` can be generated before enqueue or after backend attempts.
2. Correlate `S7P-ID`, attempt headers, queue timing, and proxy logs to identify the request stage.
3. Treat `/readiness` as supporting evidence only because several independent health conditions can make it return `503`.

## Reference Settings

| Setting | Default | Unit | Reload | Purpose |
|---------|---------|------|--------|---------|
| `MaxQueueLength` | `1000` | requests | Cold | Maximum queued requests before admission rejects new work |
| `Workers` | `10` | workers | Cold | Concurrent proxy workers; the default is intended for local testing |
| `EVENTHUB_MAX_UNDRAINED_EVENTS` | `10000` | events | Cold | Admission threshold for undrained telemetry events |
| `CBErrorThreshold` | `50` | failures | Warm | Failure count that makes the circuit check report failed |
| `CBTimeslice` | `60` | seconds | Warm | Sliding window used by the circuit check |
| `PollInterval` | `15000` | milliseconds | Cold | Backend health-poll interval and no-active-host retry value |
| `PollTimeout` | `3000` | milliseconds | Cold | Timeout for each backend health probe |

## Capture the Evidence

**Preserve the complete response before retrying because the status code alone does not identify the source.**

```bash
curl -sS -D response.headers -o response.body \
	-H "S7PDEBUG: true" http://<proxy-host>/<request-path>
cat response.headers response.body
```

Record the matching proxy log entries by `S7P-ID` or request ID, the queue length, active-host count, poller output, circuit-breaker output, and backend or APIM diagnostics such as `backendLog`.

## Identify the Request Stage

**Classify the response as pre-enqueue or post-enqueue before diagnosing the cause.**

| Evidence | Request stage | What it establishes |
|----------|---------------|---------------------|
| `S7P-ID` and `Retry-After`, with body `Queue is full`, `No active hosts`, `Max Events Exceeds Threshold`, `Too many failures in last ... seconds`, or `Failed to enqueue request` | Admission or enqueue | The proxy generated the response before a worker dispatched the request |
| Log contains `... => 429` and `ErrorDetail=EnqueueFailed` | Admission or enqueue | The log message identifies the check that rejected the request |
| `Attempts`, `Lifetime-Attempts`, `x-Request-Queue-Duration`, and `x-Total-Latency` | Backend-attempt exhaustion | The request was queued and a worker processed host attempts |
| Body begins `Error processing request. No active hosts were able to handle the request.` and contains attempt summaries with status `429` | Backend-attempt exhaustion | Available hosts returned `429` without producing a successful response |
| APIM `backendLog`, `retry-after-ms`, `Retry-After`, or `S7PREQUEUE` appears in attempt diagnostics | Backend or APIM | The throttle originated downstream, not in admission control |

> [!NOTE]
> Headers can vary by response path. Use the body, headers, and correlated logs together; do not diagnose from one missing or present header alone.

## Diagnose Admission and Enqueue Rejections

**The response body identifies which ordered admission check or enqueue operation rejected the request.**

| Response body | Corresponding evidence | What to investigate |
|---------------|------------------------|---------------------|
| `Max Events Exceeds Threshold` | Log contains `MAX EVENTS => 429`; event count exceeds `EVENTHUB_MAX_UNDRAINED_EVENTS` | Event sink health, drain rate, and telemetry backlog |
| `Too many failures in last <n> seconds` | Logs contain `Circuit breaker on => 429`, `[CB-ERROR]`, or `[CB LOCK]` | Recent failed statuses and the affected circuit-breaker window |
| `Queue is full` | Log reports queue length at or above `MaxQueueLength` | Arrival rate, queue wait, worker utilization, and backend latency |
| `No active hosts` | Log reports `Active Hosts: 0`; poller or configuration logs explain host state | Host configuration, disabled or unavailable hosts, probe results, authentication, DNS, and startup state |
| `Failed to enqueue request` | Earlier admission checks passed, but the queue rejected the later enqueue operation | A queue-capacity race or concurrent admission spike |

The circuit response does not prove that every backend circuit is open. The admission path reacts when its circuit status check reports failure; below the threshold, the same check can add a delay as failures increase. Use circuit logs and per-host status rather than inferring global state from the response text.

`No active hosts` also does not prove that every configured backend failed a probe. The active set can be empty during startup or after configuration, authentication, DNS, timeout, disabled-host, or probe failures. Direct-mode and probed hosts also enter the active set differently.

## Diagnose Backend or APIM Throttling

**A downstream `429` occurs after enqueue and can trigger another host attempt or delayed requeue.**

| Backend response | Proxy behavior | Observable result |
|------------------|----------------|-------------------|
| `429` without `S7PREQUEUE: true` | Records the attempt and tries the next available host | A later host can succeed; if all attempts exhaust with `429`, the client receives an error summary with attempt and timing evidence |
| `429` with `S7PREQUEUE: true` | Records the retry delay, continues trying hosts, then requeues after attempts exhaust | The worker releases the request for delayed retry; the client can later receive success or another terminal result |

For requeue, the proxy chooses the shortest eligible delay from `retry-after-ms` in milliseconds or `Retry-After` in seconds. If neither is usable, it defaults to `1000` ms. Requeue remains bounded by the request TTL, so repeated throttling can eventually produce `412` instead of a client-visible `429`.

For APIM, inspect `backendLog`, `x-Backend-Attempts`, `x-PolicyCycleCounter`, `S7PREQUEUE`, and retry headers. These show whether APIM exhausted its own backend choices, requested a proxy requeue, or returned a terminal throttle.

## Use Readiness as Supporting Evidence

**A `/readiness` failure narrows the investigation but does not uniquely identify a `429` cause.**

```bash
curl -i http://<proxy-host>/readiness
curl -i http://<proxy-host>/healthdetail
```

Readiness combines startup participants, active-host count, circuit status, event-client health and backlog, and asynchronous blob-queue health. Compare its output with the original `429` body and correlated logs. A readiness `503` is consistent with multiple failure modes and does not by itself prove open circuits or failed health probes.

## Correct the Bottleneck

**Change capacity only after the evidence identifies the constrained stage.**

- For event backlog, restore the event sink or drain path before increasing `EVENTHUB_MAX_UNDRAINED_EVENTS`; a larger limit only absorbs a longer burst.
- For circuit failures, fix the failed dependency or status pattern before changing `CBErrorThreshold` or `CBTimeslice`.
- For queue saturation, compare arrival rate, queue duration, worker utilization, backend latency, and downstream quotas. Increasing `Workers` helps only when worker concurrency is the constraint and downstream capacity can accept more traffic.
- Increasing `MaxQueueLength` absorbs bursts but increases wait time and memory use; confirm the request TTL can tolerate the added delay.
- Adding backend hosts increases throughput only when those hosts are eligible for the request path, healthy, independently provisioned, and selected by the load-balancing policy. It does not bypass APIM, model, quota, network, or worker bottlenecks.
- For no active hosts, inspect host configuration and poller results before changing `PollInterval` or `PollTimeout`.
- For backend/APIM throttling, align retry and requeue behavior with downstream `Retry-After`, quota, and capacity signals.

## Related

- [Response Codes and Headers](../reference/headers-and-status-codes.md)
- [Circuit Breaker Reference](../reference/circuit-breaker.md)
- [Circuit Breaker Troubleshooting](circuit-breaker.md)
- [Backend Host Troubleshooting](backend-hosts.md)
- [APIM Backend Routing](../faq/apim-backend-routing.md)
