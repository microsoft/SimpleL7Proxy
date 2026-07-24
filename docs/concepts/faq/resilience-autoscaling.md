# How does the proxy stay resilient and autoscale with ACA?

Backpressure, circuit breaking, KEDA scale triggers, health probes, replica lifecycle, and draining.

[← Back to FAQ index](README.md)

---

### What is backpressure?

Backpressure is the proxy's admission control. Before enqueueing a request, the proxy runs it through a fixed, ordered set of checks and either delays, rejects, or admits the request. Circuit-breaker failures are simply one of those checks — the circuit breaker is a signal that feeds into backpressure, not a mechanism that acts on its own.

### What is circuit breaking?

Circuit breaking tracks backend failures within a configured time window (default: 50 failures in 60 seconds) and is the second check in the backpressure sequence. As the failure count approaches the threshold, backpressure progressively delays admission; once the threshold is reached, new requests are rejected with `429`.

### What checks make up backpressure, and in what order?

Before enqueueing a request, the proxy runs it through a fixed, ordered sequence of admission checks — event backlog, then circuit-breaker failures, then queue capacity, then backend availability — delaying or rejecting with `429` as soon as one check fails. This staged design lets the proxy degrade gracefully under load: light pressure adds a small delay, sustained pressure rejects new work while in-flight requests keep draining. Probe requests bypass all of these checks.

During a planned shutdown or maintenance event, the proxy instead returns HTTP 503 (Service Unavailable).

See [Response Codes](../../RESPONSE_CODES.md) for every `429` cause and response header, and [Circuit Breaker](../../CIRCUIT_BREAKER.md) for the exact failure thresholds and progressive delays.

### Who is responsible for autoscaling?

Autoscaling is handled by Azure Container Apps, not the proxy itself. ACA decides when to add or remove replicas; the proxy's role is to report its health accurately and drain in-flight work cleanly when asked.

### What causes the proxy to scale out?

Azure Container Apps uses KEDA-based triggers. HTTP concurrency is useful for bursty, streaming, or long-lived traffic; CPU is useful for steadier compute-bound traffic. ACA monitors the number of incoming connections per replica, and when that count exceeds the configured threshold, it starts a new replica and begins routing traffic to it. When demand exceeds the configured target, ACA adds replicas up to `maxReplicas`.

### What role do health probes play in autoscaling?

The proxy exposes three health probes — `/startup`, `/liveness`, and `/readiness` — that ACA uses to track each replica's health. ACA uses these signals, together with per-replica connection counts, to decide whether a replica is healthy enough to keep receiving traffic.

### What does each new replica contain?

Replicas operate independently. Each replica maintains its own queue, workers, fairness counters, backend health observations, and circuit-breaker state in memory. When a new replica is created, it starts with no queued work or operational history. Requests already queued on existing replicas are not moved, although `async` workloads can be redistributed through shared queueing systems.

### Does scale-out redistribute queued requests or state?

No. Scale-out adds capacity for newly routed traffic, but queued requests stay on the replica that admitted them. Backend health and circuit-breaker decisions also remain local to each replica.

### What happens when a replica shuts down?

During scale-in, ACA stops routing new requests to a replica and starts its `terminationGracePeriodSeconds` timer, giving the replica time (configurable up to 30 minutes) to drain active connections before it is shut down. The proxy uses this window to finish in-flight work before winding down.

If the replica will be replaced, Azure Container Apps starts a replacement replica while the old one drains, so new traffic moves to replacement capacity instead of the terminating replica. Any requests still in flight when `terminationGracePeriodSeconds` elapses are forced closed.

### How does the proxy signal distress to ACA?

When backpressure is high, the proxy signals distress to ACA by failing the readiness probe. This tells ACA to stop routing new requests to that replica. If a replica stays in distress long enough, ACA recycles it.

### How does autoscale interact with workers and queue length?

`Workers` limits concurrent processing inside each replica, while `MaxQueueLength` limits waiting work in that replica — both are independent of, and should be sized alongside, ACA's own replica-level scale trigger.

See [Day 2 Operations](../../../deployment/DAY2_OPERATIONS.md#scaling-considerations) for choosing replica counts, scale triggers, and concurrency targets together.
