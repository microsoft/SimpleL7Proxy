# SimpleL7Proxy

SimpleL7Proxy is a self-hosted Layer 7 proxy for Azure AI workloads. It sits between clients and backend model endpoints and manages every inbound request through a connected chain of behaviors: validating the caller and their headers against per-user rules, placing the request in a priority queue with a time budget, selecting a healthy backend through a configurable load-balance strategy, handling failures with per-backend circuit breakers and automatic retry, escalating long-running requests to an async background flow with results stored in Azure Blob Storage and status delivered over Service Bus, and recording structured telemetry for every completed request. Most runtime behavior updates live without restarting the proxy.

> Need help diagnosing issues quickly? Start at [TroubleshootTOC.md](TroubleshootTOC.md).

<details>
<summary><h2>How It Works</h2></summary>

<details>
<summary><strong>Validation before the queue</strong></summary>

Every inbound request is screened before it can enter the queue. The proxy can verify that the caller belongs to an approved identity list, strip headers that must not reach backends, load the caller's user profile to apply per-user rules, and enforce that required headers are present and contain permitted values. A suspended caller is rejected immediately. All of this runs before the request touches the queue, so invalid work never consumes queue capacity or worker threads.

</details>

<details>
<summary><strong>Priority queue and time budget</strong></summary>

Requests that pass validation enter a priority queue. Each request is assigned a priority tier based on a header value — lower-priority callers such as batch jobs wait behind higher-priority ones such as interactive chat. Every request carries a time budget from the moment it enters the queue; if it waits too long before dispatch, it is rejected rather than forwarded stale. Worker threads can be partitioned by priority tier so high-priority traffic always has dedicated capacity. A per-user cap prevents any single caller from occupying a disproportionate share of the queue, regardless of how fast they submit requests.

</details>

<details>
<summary><strong>Backend selection pipeline</strong></summary>

When a worker picks up a request, it selects a backend through three steps: narrow the candidate list to hosts whose URL prefix matches the request path; order that list by the configured load-balance strategy (round-robin for even distribution, latency-ordered to favour the fastest host, or random); then skip any host whose circuit breaker is open. If no host is available, the proxy returns 503 immediately rather than holding the request indefinitely. Multiple backends can be grouped under the same path prefix to pool capacity for a specific workload.

</details>

<details>
<summary><strong>Circuit breaker and retry</strong></summary>

Each backend is monitored independently. When a backend starts returning errors, the proxy slows traffic to it progressively before cutting it off entirely — this prevents a struggling backend from absorbing a full request load before the failure is detected. Once the error rate clears, the backend is automatically reinstated with no manual action required. When a request fails on one backend, the proxy advances to the next available host and retries within the same time budget. If a backend signals temporary over-capacity, the proxy returns the request to the priority queue for a later attempt rather than discarding it, preserving the work until capacity is available.

</details>

<details>
<summary><strong>Async mode</strong></summary>

Some AI tasks — document summarization, large batch inference, long-form generation — take longer than a synchronous HTTP connection can sustain. Async mode handles this by releasing the client immediately with a reference to a result location, then completing the backend call in the background. The client receives an accepted response with a storage URI to retrieve the result when ready. Throughout processing, the proxy emits lifecycle status events (queued, processing, completed, failed) to a per-user Azure Service Bus topic so the client can track progress in real time rather than polling. The result is stored in Azure Blob Storage and retained for a configured period before being deleted automatically.

</details>

<details>
<summary><strong>Observability and telemetry</strong></summary>

Every request produces a structured event record containing timing, status, backend identity, and token usage. These events are delivered to one or more configured destinations: Application Insights for dashboards and alerts, Azure Event Hubs for high-volume streaming pipelines, or a local file for development. For streaming AI responses, the proxy extracts token counts in flight without buffering the full response, making per-request token telemetry available for billing and chargeback even when clients use streaming mode. Three health endpoints — liveness, readiness, and startup — let orchestration platforms verify that the proxy is running and that at least one backend is reachable. An optional sidecar deployment isolates health checking from proxy traffic so that high load does not trigger false container restarts.

</details>

<details>
<summary><strong>Configuration lifecycle</strong></summary>

Most operational settings — load-balance strategy, circuit-breaker thresholds, user profiles, priority rules — update live without restarting the proxy. Changes pushed to Azure App Configuration propagate to all running instances within about 30 seconds. A smaller set of foundational settings, such as worker count and whether async mode is enabled, requires a container restart to take effect. This split means routine tuning and access-control changes carry no downtime impact, while structural changes are explicit and deliberate.

</details>

</details>

## Scenarios

SimpleL7Proxy is purpose-built for AI workloads that mix request types, users, and latency profiles. The scenarios below show which combinations of built-in behaviors apply to each situation.

| Scenario | Capabilities used |
|----------|------------------|
| Mixed interactive and batch workloads | Priority queue with dedicated workers per tier; batch jobs wait behind interactive chat without requiring separate deployments. |
| Long-running inference (30+ minutes) | Async mode releases the HTTP connection immediately; result delivered to blob storage with Service Bus progress events. |
| Multi-model or multi-region routing | Path-based backend routing directs request types to different pools; circuit breakers and retry handle pool failures automatically. |
| Per-user billing and chargeback | Token telemetry captures prompt and completion counts per request from streaming responses; events route to Event Hubs for downstream processing. |
| Fairness across many callers | Per-user queue caps prevent any single caller from monopolizing capacity; user suspension takes effect immediately without restart. |
| PTU maximization with PayGo fallback | Separate backend pools per priority tier allow PTU-backed backends to serve high-priority traffic while lower-priority requests overflow to PayGo backends. |
| Caller access control without redeployment | Validation rules, user profiles, and suspended-user lists all reload live from Azure App Configuration. |

---

## What Problems Does It Solve?

SimpleL7Proxy is built for AI workloads that outgrow what a standard load balancer or API gateway can handle. The problems below represent the gaps that emerge when multiple clients share a fixed pool of AI backends: capacity contention, failure propagation, invisible token consumption, and requests that simply take too long for synchronous HTTP. Each is addressed by a specific behavior described in [How It Works](#how-it-works).

<details>
<summary>Show problem-solution table</summary>

| Problem | How the proxy addresses it |
|---------|---------------------------|
| Interactive requests blocked by batch jobs | Priority queuing assigns each request a tier; dedicated worker threads per tier ensure high-priority callers are never starved by lower-priority batch work. |
| One caller monopolizing capacity | A per-user queue cap deprioritizes callers who exceed their share, keeping capacity available for all callers. |
| Backend failures cascading to clients | Per-backend circuit breakers detect error spikes, progressively slow traffic to the failing host, and cut it off automatically; failed requests retry on the next available host within the same time budget. |
| Token usage hidden inside streaming responses | The proxy extracts token counts from streaming AI responses in flight, per request, making them available for billing and chargeback without the client needing to parse the stream. |
| Uneven backend response times | Latency-ordered backend selection routes each request to the fastest currently available host; round-robin and random modes are also available. |
| Long-running AI tasks timing out | Async mode releases the HTTP connection immediately and completes the backend call in the background, storing the result in Azure Blob Storage and emitting progress events over Service Bus. |
| Stale requests wasting backend capacity | Every request carries a time budget from the moment it enters the queue; requests that wait too long are rejected before they reach any backend. |
| Unauthorized or invalid callers reaching backends | Caller identity, required headers, and per-user header values are all screened before a request enters the queue. Users can be suspended instantly without a restart. |

</details>

---

## Well-Architected Framework Alignment

The proxy's design maps directly to the five Azure Well-Architected Framework pillars. The behaviors described in [How It Works](#how-it-works) satisfy each pillar's concerns without requiring additional tooling.

<details>
<summary>Show WAF pillar mapping</summary>

| Pillar | How the proxy contributes |
|--------|--------------------------|
| **Reliability** | Per-backend circuit breakers with automatic recovery, retry across hosts, requeue on over-capacity, TTL expiry to shed stale work, and health endpoints for orchestration platforms. |
| **Security** | Caller identity validation before queuing, header stripping to prevent internal value leakage, per-user header allowlists, and immediate user suspension without restart or redeployment. |
| **Performance Efficiency** | Priority queue with dedicated worker threads per tier, latency-ordered backend selection, and a per-user throttle to prevent noisy-neighbour effects. |
| **Operational Excellence** | Most settings propagate live across all instances via Azure App Configuration without restart; structured per-request telemetry goes to Application Insights and Event Hubs; a sidecar isolates health checking from request traffic. |
| **Cost Optimization** | Per-request token telemetry from streaming responses enables per-user billing and chargeback; per-user throttling prevents runaway consumption; requeue on over-capacity preserves work rather than forcing the client to re-submit. |

</details>

---

## Open Source and Self-Hosted

SimpleL7Proxy is open source and runs entirely in your own environment. Nothing leaves your control — the proxy handles authentication, routing, and telemetry inside your own infrastructure. Because it runs as a standard container it deploys anywhere Docker runs. The telemetry pipeline accepts custom sinks, priority mapping is fully configurable per deployment, and user profiles allow per-caller behavior changes without code changes or restarts.

---

## Capabilities

**Request governance**
- Validates caller identity, required headers, and per-user header values before any request enters the queue
- Strips headers that must not reach backends
- Suspends individual callers live, without restart

**Priority queuing**
- Assigns each request a priority tier from a configurable header value
- Allocates dedicated worker threads per tier so high-priority traffic always has capacity
- Enforces a per-user queue share cap to prevent any single caller from starving others

**Backend selection and routing**
- Routes by URL path prefix to workload-specific backend pools
- Supports round-robin, latency-ordered, and random load balancing
- Skips backends with open circuit breakers automatically; returns 503 immediately if no host is available

**Reliability**
- Per-backend circuit breakers with progressive slowdown and automatic recovery
- Retry across available hosts within the original request's time budget
- Requeue on temporary over-capacity rather than discard

**Async mode**
- Releases the HTTP connection immediately for long-running requests
- Completes the backend call in the background and writes the result to Azure Blob Storage
- Emits lifecycle status events (queued, processing, completed, failed) over Azure Service Bus per user

**Observability**
- Structured per-request event records delivered to Application Insights, Azure Event Hubs, or a local file
- Token counts extracted from SSE streams without buffering the full response
- Liveness, readiness, and startup health endpoints for orchestration platforms
- Optional sidecar container isolates health checking from proxy traffic

**Configuration**
- Most operational settings propagate live via Azure App Configuration with no restart
- Foundational settings (worker count, async on/off) require a container restart and are changed deliberately

---

## Architecture

![Architecture Diagram](arch.png)
