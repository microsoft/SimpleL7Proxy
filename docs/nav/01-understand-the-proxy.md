# Understand How the Proxy Controls AI Traffic

A single endpoint is fine when you're just getting started. But as things grow, you start needing more — priorities, smarter routing, cost tracking, reliability, observability, all at once. SimpleL7Proxy is a straightforward way to handle that layer: it lets you route requests, retry them now or later, split costs, and treat requests differently depending on the app or user behind them.

When deployed in front of APIM, the proxy adds a user profile governance layer that applies workload-specific policies for validation, routing,
prioritization, and execution. This helps organizations balance reliability, performance, compliance, and cost across AI workloads.

The platform continuously balances reliability, performance, service quality, and cost by intelligently routing requests across regions, endpoints, priority queues, and AI models. Critical workloads receive preferential treatment, while less time-sensitive workloads are processed in a cost-efficient manner without impacting business-critical operations.

<img width="1308" height="534" alt="image" src="https://github.com/user-attachments/assets/60b20f0c-cee1-44b7-8f6a-b97d84f590bf" />
---

## Quick Topics

<table>
<tr>
<td width="33%" valign="top">

### [The User Profile](#how-do-user-profiles-determine-when-requests-run)

The proxy loads user profiles from CosmosDB and, at receive time, enriches each incoming request with its matching profile data, mapped to key-value settings. The priority setting controls processing order, while other settings can override the requested model or report metrics. Additional fields can validate, map, or clean up the request, and drive further routing and policy decisions in both the proxy and APIM. When no profile matches, the proxy falls back to a default priority.

</td>
<td width="33%" valign="top">

### [Coordinating with Backends](#how-does-apim-determine-which-backends-receive-requests-1)

The proxy and **APIM** each apply their own layer of smart routing and retry, working together so a request keeps retrying across backends and regions until it succeeds or its TTL expires. APIM independently adds extensive governance — routing, compliance, and more via its policy engine.

Because **direct** backends skip active probing, no latency data is available for them, so they rely on path-based routing combined with `random` or `roundrobin` load balancing.

</td>

<td width="33%" valign="top">

### [Autoscaling with ACA](#how-does-autoscaling-expand-proxy-capacity)

Azure Container Apps makes all scaling decisions, guided by the proxy's `/startup`, `/liveness`, and `/readiness` probes. ACA **scales out** by comparing connections per replica to a configured threshold, and recycles any replica showing sustained backpressure.

When a replica is scaled in or terminated, ACA stops routing new requests to it and gives it a grace period to **drain** its active connections. The proxy uses that window to finish in-flight work before shutting down.

</td>
</tr>
<tr>
<td width="33%" valign="top">

### [Resiliency](#how-does-the-proxy-stay-healthy-and-recover-from-failure-1)

Each replica protects itself by signaling distress to ACA if needed. On the inbound side, **backpressure** progressively delays requests as the replica saturates. On the backend side, a **circuit breaker** stops sending traffic to a failing backend until it recovers.

</td>

<td width="33%" valign="top">

### [Sync to Async](#when-should-clients-stop-waiting-synchronously-1)

If a request runs longer than a configured trigger timeout, the proxy promotes it to async: it returns `202`, continues processing in the background, stores the result in Blob Storage, and publishes status through Service Bus. This can wrap any API call, giving it background-processing capability but especially use for long running LLM queries.

</td>
<td width="33%" valign="top">

### [Observability](#how-does-the-proxy-handle-observability-and-telemetry)

The proxy logs full request activity — including AI token usage pulled from streaming responses — to Application Insights, Event Hub, local files, or your own custom code.

</td>
</tr>
</table>

---

## Full Answers

### How do user profiles determine when requests run?

#### Where does a user's priority come from?

A request's priority can come from an incoming request header or from the user's profile. When user profiles are used, the proxy caches them from CosmosDB into memory, refreshing the cache every hour, and matches each incoming request to a profile to assign its priority.

Each priority has two parts: a human-friendly string that is sent in the header and its mapped numeric value used for priority ordering.

**Example:** With `PriorityKeys=high,medium,low` and `PriorityValues=1,2,3`, the strings map to values as follows:

| Header string (`PriorityKeys`) | Numeric value (`PriorityValues`) |
|---|---|
| `high` | `1` |
| `medium` | `2` |
| `low` | `3` |

A profile containing `"S7PPriorityKey": "high"` therefore receives priority `1`.

#### What does the priority affect?

In the **proxy**, the priorty changes the order in which queued request is selected by a worker. In the **APIM** it is used to select the order and priority of endpoints.

#### When does the profile priority take effect?

The proxy resolves the profile before admitting the request to the queue. It assigns the mapped priority when the request is enqueued, so the value affects dispatch order as soon as the request begins waiting for a worker.

#### Can a profile change the requested model?

Yes. A user profile can specify a model override. The proxy rewrites the original request to use that model before forwarding it, so model selection can be controlled per user without requiring the caller to change the request.

#### What happens when no profile priority is available?

The proxy uses `DefaultPriority` when it cannot override it.

#### How does the proxy prevent one user from dominating a priority?

The proxy tracks each user's share of active requests. A user below `UserPriorityThreshold` percentage receives a fairness boost that places it ahead of other similar priority requests. If a user uses more than their fair share, they will be processed after the others.

See [User Profiles](../USER_PROFILES.md) for profile structure and loading.

---

### How does the proxy stay healthy and recover from failure?

#### What is backpressure?

Backpressure is the proxy's admission control. Before enqueueing a request, the proxy runs it through a fixed, ordered set of checks and either delays, rejects, or admits the request. Circuit-breaker failures are simply one of those checks — the circuit breaker is a signal that feeds into backpressure, not a mechanism that acts on its own.

#### What is circuit breaking?

Circuit breaking tracks backend failures within a configured time window (default: 50 failures in 60 seconds) and is the second check in the backpressure sequence. As the failure count approaches the threshold, backpressure progressively delays admission; once the threshold is reached, new requests are rejected with `429`.

#### What checks make up backpressure, and in what order?

The proxy evaluates these conditions, in order, before enqueueing a request:

1. **Telemetry/event backlog** — above 50% of `MaxUndrainedEvents` (default `10,000`), admission is delayed; above the limit, the request is rejected with `429 Max Events Exceeds Threshold`.
2. **Circuit-breaker failures** — at 50–90% of the failure threshold, admission is delayed 100–500 ms; at the threshold (default 50 failures in 60 seconds), the request is rejected with `429`.
3. **Request queue full** — rejected with `429 Queue is full` once the queue reaches `MaxQueueLength` (default `1,000`).
4. **No active backend hosts** — rejected with `429 No active hosts` when there are no active hosts available.
5. **Concurrent enqueue failure** — the queue independently re-checks its capacity; a race can fill it after check 3 passes, so the enqueue attempt can still fail and produce a `429`.

Rejected requests receive a `Retry-After` header: the configured poll interval when no hosts are active, otherwise the literal value `500`. Probe requests bypass all of these admission checks.

During a planned shutdown or maintenance event, the proxy instead returns HTTP 503 (Service Unavailable).

#### What happens as circuit-breaker failures approach the threshold?

Once circuit-breaker failures reach 50% of the threshold, backpressure progressively delays incoming requests:
|Failure threshold reached |	Delay|
|--|--|
|50%	| 100 ms |
|60%	| 200 ms |
|70%	| 300 ms |
|80%	| 400 ms |
|90%	| 500 ms |
|100%	| Reject with 429 |
|--|--|

Failures older than the configured time window are removed, allowing the circuit to recover and close again.

See [Circuit Breaker](../CIRCUIT_BREAKER.md) for thresholds, delays, and recovery.

---

### How does APIM determine which backends receive requests?

#### Does queue priority select a SimpleL7Proxy backend?

No. SimpleL7Proxy backend selection filters hosts by request path, orders them by `LoadBalanceMode`, and skips unhealthy or open-circuit hosts. Queue priority controls when a worker receives the request.

#### Where does priority-aware backend routing happen?

The supplied APIM priority policy uses the request priority and determines the eligible backends for each request.

#### How does the APIM policy handle a throttled endpoint?

When an endpoint returns `429`, the policy records its retry time and marks it as throttled. Later requests skip that endpoint until the retry time passes, so APIM can use another endpoint instead of immediately repeating an attempt that is expected to throttle.

#### What controls SimpleL7Proxy backend selection?

The selector first filters `Host` entries by request path, orders the matching hosts using `LoadBalanceMode` (`latency`, `roundrobin`, or `random`), and then skips unhealthy or open-circuit hosts. The same `LoadBalanceMode` ordering and per-host circuit-breaker gating applies whether the matched hosts are direct or APIM-mode — both are ordered together in one candidate list. It does not use queue priority to determine backend eligibility.

#### What happens when a backend attempt fails or returns 429?

The proxy tries the next host in the ordered candidate list. A `429` response with `S7PREQUEUE: true` is collected as requeue-eligible before moving on. Once every host in the list has been tried, the proxy requeues the request — using the shortest eligible delay from any collected `429` responses — instead of failing it outright. This is bounded by the request's overall TTL: if the TTL expires first, iteration stops with `412` rather than continuing to retry.

#### What happens when the preferred backend fails?

##### Proxy
If the preferred backend is unavailable, throttled, or unhealthy, the proxy automatically retries the request against the next available backend in the configured list.

##### APIM
When using APIM backend pools, APIM evaluates backend priority groups in their configured order and selects an available endpoint within the highest-priority group. If no healthy endpoints remain in that group, APIM fails over to the next priority group.

This approach enables organizations to reserve specific endpoints or capacity pools for different request priorities, ensuring that critical workloads continue to receive service during capacity constraints or backend failures.

#### What happens when every endpoint in an APIM region is throttled?

When APIM has exhausted all eligible endpoints in a region, it can return an HTTP 429 (Too Many Requests) response along with the S7PREQUEUE: true|false header and a recommended retry interval.

SimpleL7Proxy interprets this response as a regional capacity constraint and automatically retries the request against the next configured APIM host. This allows traffic to fail over to another region with available capacity, improving resiliency and reducing the impact of localized throttling.

#### What happens when every APIM region is throttled?

After all configured APIM hosts return a requeue response, the proxy selects the shortest eligible retry delay, places the request back in its priority queue, and tries again after that delay. The request remains subject to its overall TTL while it waits and retries.

#### What happens when no backend accepts the priority?

APIM returns `503 Service Unavailable`. Changing retry count cannot help because the candidate set is empty.

See [Priority Levels POC](../POC-Priority-configuration.md) for a runnable example.

---

### When should traffic go directly to a backend or through APIM?

#### What is a direct backend?

A direct backend uses `mode=direct`. The proxy does not send active health probes to it and always includes it in the active host set. Real request failures are still recorded by the circuit breaker.

```bash
Host_<name>="host=https://model.example.com;mode=direct;path=/model"
```

#### When should I use direct mode?

Use it when probing would be unsafe or undesirable—for example, when a serverless target scales to zero or has no suitable probe endpoint. Because direct mode has no probe-derived latency, it sorts first when `LoadBalanceMode=latency`.

#### When should I route through APIM?

Use APIM in the backend path when requests need gateway policies, transformations, subscriptions, caller authentication, or priority-aware selection across the services behind APIM. This adds APIM as an operational dependency, so use it for capabilities the direct path does not provide.

#### What is an APIM backend?

An APIM backend points a `Host_<name>` entry at Azure API Management. `mode=apim` is standard non-direct behavior: the proxy sends the configured probe on every `PollInterval`, recording both a rolling success rate and latency on each successful probe. It can remove APIM from the active set when health falls below the required success rate, and uses the recorded latency to order hosts when `LoadBalanceMode=latency`.

```bash
Host_<name>="host=https://gateway.azure-api.net;mode=apim;path=/shared;probe=/health"
```

#### Why put APIM behind the proxy?

APIM can supply API gateway capabilities such as caller authentication, subscriptions, transformations, and priority-aware backend policies. The proxy adds its own queue, worker controls, health tracking, circuit breaking, and telemetry around that gateway path.

See [Backend Host Configuration](../BACKEND_HOSTS.md) for all host options.

---

### How does autoscaling expand proxy capacity?

#### Who is responsible for autoscaling?

Autoscaling is handled by Azure Container Apps, not the proxy itself. ACA decides when to add or remove replicas; the proxy's role is to report its health accurately and drain in-flight work cleanly when asked.

#### What causes the proxy to scale out?

Azure Container Apps uses KEDA-based triggers. HTTP concurrency is useful for bursty, streaming, or long-lived traffic; CPU is useful for steadier compute-bound traffic. ACA monitors the number of incoming connections per replica, and when that count exceeds the configured threshold, it starts a new replica and begins routing traffic to it. When demand exceeds the configured target, ACA adds replicas up to `maxReplicas`. 

#### What role do health probes play in autoscaling?

The proxy exposes three health probes — `/startup`, `/liveness`, and `/readiness` — that ACA uses to track each replica's health. ACA uses these signals, together with per-replica connection counts, to decide whether a replica is healthy enough to keep receiving traffic.

#### What does each new replica contain?

Replicas operate independently. Each replica maintains its own queue, workers, fairness counters, backend health observations, and circuit-breaker state in memory. When a new replica is created, it starts with no queued work or operational history. Requests already queued on existing replicas are not moved, although `async` workloads can be redistributed through shared queueing systems.

#### Does scale-out redistribute queued requests or state?

No. Scale-out adds capacity for newly routed traffic, but queued requests stay on the replica that admitted them. Backend health and circuit-breaker decisions also remain local to each replica.

#### What happens when a replica shuts down?

During scale-in, ACA stops routing new requests to a replica and starts its `terminationGracePeriodSeconds` timer, giving the replica time (configurable up to 30 minutes) to drain active connections before it is shut down. The proxy uses this window to finish in-flight work before winding down.

If the replica will be replaced, Azure Container Apps starts a replacement replica while the old one drains, so new traffic moves to replacement capacity instead of the terminating replica. Any requests still in flight when `terminationGracePeriodSeconds` elapses are forced closed.

#### How does the proxy signal distress to ACA?

When backpressure is high, the proxy signals distress to ACA by failing the readiness probe. This tells ACA to stop routing new requests to that replica. If a replica stays in distress long enough, ACA recycles it.

#### How should minimum and maximum replicas be chosen?

Use `minReplicas` of at least `1` to avoid cold starts on a latency-sensitive path, and increase it for availability. Set `maxReplicas` from downstream backend capacity rather than proxy CPU alone; scaling the proxy cannot increase a backend's quota.

#### How does autoscale interact with workers and queue length?

`Workers` limits concurrent processing inside each replica, while `MaxQueueLength` limits waiting work in that replica. The ACA scale target should reflect measured per-replica worker and backend connection capacity. Repository deployment templates contain different concurrency targets, so their values are starting points rather than a universal default.

See [Day 2 Operations](../../deployment/DAY2_OPERATIONS.md#scaling-considerations) for scaling guidance.

---

### When should clients stop waiting synchronously?

#### What problem does async solve?

Async separates backend processing time from the caller's HTTP connection lifetime. It is useful for long model runs, background jobs, or any request likely to exceed a client, gateway, or network timeout.

#### Does async make backend processing faster?

No. Async changes how the client waits for and retrieves the result; it does not reduce backend execution time. The benefit is releasing the HTTP connection while work continues.

#### How does a request enter async mode?

Three conditions must be true: `AsyncModeEnabled=true` for the proxy, the user's profile must allow async and name a Blob container and Service Bus topic, and the request must send the configured async header. Fast requests still complete synchronously.

#### What happens after the async trigger timeout?

When an opted-in request runs longer than `AsyncTriggerTimeout`, the proxy returns `202 Accepted`. Processing continues, response data is written to Azure Blob Storage, and lifecycle status is published through Azure Service Bus.

#### When should I keep requests synchronous?

Keep them synchronous when they reliably finish inside the caller's wait budget. Async adds Storage and Service Bus dependencies, RBAC configuration, result retention, and client-side status handling.

See [Async Operation Configuration](../AsyncOperation.md) for the complete setup.

---

### How does the proxy handle observability and telemetry?

#### Where does the proxy send telemetry?

Two mechanisms run side by side. Standard ASP.NET telemetry (requests, dependencies, exceptions) goes straight to Azure Application Insights via `TelemetryClient` when `APPINSIGHTS_CONNECTIONSTRING` is set. Separately, every proxied request also produces a `ProxyEvent` that fans out through `EVENT_LOGGERS` to a local JSON log file, Azure Event Hubs, and/or a custom logger — any combination of these can run at the same time.

#### How does the proxy fan events out to multiple sinks?

`CompositeEventClient` holds a zero-lock, zero-allocation snapshot of the registered backends and dispatches each serialized `ProxyEvent` to all of them. Each backend buffers events in its own queue and flushes them asynchronously in the background, so a slow sink doesn't block the request path.

#### What does each event contain?

Standard fields include a correlation ID (`S7P_RequestId`), the backend that handled the request (`BackendHost`), its priority (`S7P_Priority`), circuit-breaker status, and retry count, plus request timing and, for streaming AI responses, token usage.

#### How are tokens counted for streaming responses?

Standard gateways struggle to count tokens in Server-Sent Events streams because the usage data often only appears in the final chunk. For hosts configured with `processor=OpenAI`, the proxy's stream processor parses the response on the fly — without buffering it — and extracts `Usage.Prompt_Tokens`, `Usage.Completion_Tokens`, and `Usage.Total_Tokens`.

#### Can I plug in my own telemetry backend?

Yes. Implement `IEventClient` and `IHostedService`, register with `CompositeEventClient` during `StartAsync`, and reference the class by its fully-qualified type name in `EVENT_LOGGERS`. Only types inside the `SimpleL7Proxy` assembly can be loaded this way.

#### What happens if a telemetry backend is misconfigured or fails?

The proxy still starts. A backend that fails to initialize — an unreachable Event Hub, for example — is silently disabled while the others keep running, and an invalid `EVENT_HEADERS` type falls back to the built-in default with a warning.

#### How do I avoid logging sensitive headers?

`LogAllRequestHeaders` / `LogAllResponseHeaders` turn on full header capture for debugging, and `LogAllRequestHeadersExcept` blacklists specific headers — such as `Authorization` or `api-key` — so they're never logged.

See [Observability](../OBSERVABILITY.md) for telemetry channel configuration and the full event schema.

---

## You Should Now Be Able To

- [ ] Explain how a profile becomes a queue priority
- [ ] Explain how a profile can override the requested model
- [ ] Distinguish backpressure from circuit breaking
- [ ] Explain where priority-aware backend routing occurs
- [ ] Choose between direct and APIM backend modes
- [ ] Describe what changes and what stays local during autoscale and shutdown
- [ ] Decide when a request should use async processing
- [ ] Explain where telemetry goes and how to add a custom sink

---

## Related Documents

| Document | What it covers |
|----------|----------------|
| [User Profiles](../USER_PROFILES.md) | Per-user priority and async fields |
| [Load Balancing](../LOAD_BALANCING.md) | Native backend selection and retry |
| [Backend Hosts](../BACKEND_HOSTS.md) | Direct and probed host configuration |
| [Get It Running](02-get-it-running.md) | The next discovery path for running the proxy |

---
