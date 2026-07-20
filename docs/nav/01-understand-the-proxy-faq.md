# Understanding the SimpleL7Proxy — FAQ

Detailed questions and answers for each [Quick Topic](01-understand-the-proxy.md#quick-topics) in the proxy discovery path.

---

### How do user profiles determine when requests run?

#### Where does a user's priority come from?

A request's priority can come from an incoming request header or from the user's profile. When user profiles are used, the proxy caches them from CosmosDB into memory, refreshing the cache every hour, and matches each incoming request to a profile to assign its priority.

See [Priority Levels](#what-does-a-requests-priority-level-control) for how priority values are structured and what they control.

#### When does the profile priority take effect?

The proxy resolves the profile before admitting the request to the queue. It assigns the mapped priority when the request is enqueued, so the value affects dispatch order as soon as the request begins waiting for a worker.

#### Can a profile change the requested model?

Yes. A user profile can specify a model override. The proxy rewrites the original request to use that model before forwarding it, so model selection can be controlled per user without requiring the caller to change the request.

#### What happens when no profile priority is available?

The proxy uses `DefaultPriority` when it cannot override it.

See [User Profiles](../USER_PROFILES.md) for profile structure and loading.

---

### What does a request's priority level control?

#### What is a priority level made of?

Each priority level has two parts: a human-friendly string sent in a header (`PriorityKeys`) and its mapped numeric value used for ordering (`PriorityValues`).

**Example:** With `PriorityKeys=high,medium,low` and `PriorityValues=1,2,3`, the strings map to values as follows:

| Header string (`PriorityKeys`) | Numeric value (`PriorityValues`) |
|---|---|
| `high` | `1` |
| `medium` | `2` |
| `low` | `3` |

A profile containing `"S7PPriorityKey": "high"` therefore receives priority `1`.

#### What does a request's priority actually control?

In the **proxy**, priority changes the order in which a queued request is selected by a worker. In **APIM**, it selects the order and priority of eligible endpoints.

#### Does priority affect which backends or capacity a request can use?

Yes, but not through SimpleL7Proxy's own backend selection — that filters by request path and `LoadBalanceMode`, not priority. Within APIM, though, each backend declares which priorities it accepts, so a capacity pool can be reserved for higher-priority traffic while lower-priority requests are routed elsewhere.

#### Do different priorities get different retry behavior?

Retry count and whether an exhausted request may be requeued are both configured per priority level in the APIM policy, so retry aggressiveness can be tuned independently for each priority.

#### What happens when no backend accepts a given priority?

APIM returns `503 Service Unavailable`. Changing retry count cannot help because the candidate set is empty.

See [Priority Levels POC](../POC-Priority-configuration.md) for a runnable example.

---

### How does the proxy keep queueing fair across users?

#### How does the proxy keep high-priority traffic from starving lower-priority requests?

Each priority level has its own dedicated pool of workers, so higher-priority traffic can't consume the capacity reserved for lower-priority requests.

#### How does the proxy keep one user from monopolizing a priority level?

Within a priority level, the proxy tracks each user's share of active requests. A user who stays under `UserPriorityThreshold` (default `0.1`, i.e. 10%) gets a fairness boost ahead of other users at that level; once their share crosses the threshold, the boost is withheld until it drops back down.

See [Advanced Configuration](../ADVANCED_CONFIGURATION.md#userprioritythreshold) for how to tune that threshold, with a worked example.

---

### How does APIM determine which backends receive requests?

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

See [Load Balancing](../LOAD_BALANCING.md) for backend selection and retry mechanics.

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

### How does the proxy stay resilient and autoscale with ACA?

#### What is backpressure?

Backpressure is the proxy's admission control. Before enqueueing a request, the proxy runs it through a fixed, ordered set of checks and either delays, rejects, or admits the request. Circuit-breaker failures are simply one of those checks — the circuit breaker is a signal that feeds into backpressure, not a mechanism that acts on its own.

#### What is circuit breaking?

Circuit breaking tracks backend failures within a configured time window (default: 50 failures in 60 seconds) and is the second check in the backpressure sequence. As the failure count approaches the threshold, backpressure progressively delays admission; once the threshold is reached, new requests are rejected with `429`.

#### What checks make up backpressure, and in what order?

Before enqueueing a request, the proxy runs it through a fixed, ordered sequence of admission checks — event backlog, then circuit-breaker failures, then queue capacity, then backend availability — delaying or rejecting with `429` as soon as one check fails. This staged design lets the proxy degrade gracefully under load: light pressure adds a small delay, sustained pressure rejects new work while in-flight requests keep draining. Probe requests bypass all of these checks.

During a planned shutdown or maintenance event, the proxy instead returns HTTP 503 (Service Unavailable).

See [Response Codes](../RESPONSE_CODES.md) for every `429` cause and response header, and [Circuit Breaker](../CIRCUIT_BREAKER.md) for the exact failure thresholds and progressive delays.

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

#### How does autoscale interact with workers and queue length?

`Workers` limits concurrent processing inside each replica, while `MaxQueueLength` limits waiting work in that replica — both are independent of, and should be sized alongside, ACA's own replica-level scale trigger.

See [Day 2 Operations](../../deployment/DAY2_OPERATIONS.md#scaling-considerations) for choosing replica counts, scale triggers, and concurrency targets together.

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

Yes — the proxy defines an `IEventClient`/`IHostedService` extensibility point and fans events out to every registered backend through `CompositeEventClient`, so a custom sink runs alongside the built-in ones rather than replacing them.

#### What happens if a telemetry backend is misconfigured or fails?

The proxy still starts. A backend that fails to initialize — an unreachable Event Hub, for example — is silently disabled while the others keep running, and an invalid `EVENT_HEADERS` type falls back to the built-in default with a warning.

See [Observability](../OBSERVABILITY.md) for telemetry channel configuration, adding a custom event sink, and the full event schema.
