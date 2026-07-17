# Understand How the Proxy Controls AI Traffic

Sending every request to an endpoint works during POC and early dev phases. As products mature they need greater control with priorities, routing, cost controls, reliability and observability all thrown into the mix.  The SimpleL7Proxy lets you decide how to route, retry , retry later, share costs and fullfill requests based on the application / user profile.

When deployed in front of APIM, the proxy adds a User Profile governance layer that applies workload-specific policies for validation, routing,
prioritization, and execution. This helps organizations balance reliability, performance, compliance, and cost across AI workloads.

<img width="1308" height="534" alt="image" src="https://github.com/user-attachments/assets/60b20f0c-cee1-44b7-8f6a-b97d84f590bf" />

At a high level, the platform continuously balances reliability, performance, service quality, and cost by intelligently routing requests across regions, endpoints, priority queues, and AI models. Critical workloads receive preferential treatment, while less time-sensitive workloads are processed in a cost-efficient manner without impacting business-critical operations.

---

## Quick Answers

<table>
<tr>
<td width="33%" valign="top">

### [How do user profiles determine when requests run?](#how-do-user-profiles-determine-when-requests-run-1)
A user profile can assign a queue priority and override the requested model. High priorities run first, while a model override rewrites the request before it reaches the backend.

</td>
<td width="33%" valign="top">

### [How are proxy capacity and unhealthy backends protected?](#how-are-proxy-capacity-and-unhealthy-backends-protected-1)
Backpressure slows how quickly a replica admits new work as its telemetry backlog grows; admitted work continues at full speed. The proxy circuit breaker delays and eventually blocks attempts to a failing backend until it recovers.

</td>
<td width="33%" valign="top">

### [How does APIM determine which backends receive requests?](#how-does-apim-determine-which-backends-receive-requests-1)
The supplied APIM policy selects endpoints by priority and tracks each endpoint's throttle period. It skips throttled endpoints until their retry time, while the proxy can try another APIM region and requeue the request when every region is throttled.

</td>
</tr>
<tr>
<td width="33%" valign="top">

### [When should traffic go directly to a backend or through APIM?](#when-should-traffic-go-directly-to-a-backend-or-through-apim-1)
Use direct mode when active probing is unsuitable. Route through APIM when the traffic path needs gateway policies, transformations, caller controls, or priority-aware backend selection.

</td>
<td width="33%" valign="top">

### [How does autoscaling expand proxy capacity?](#how-does-autoscaling-expand-proxy-capacity-1)
Azure Container Apps adds independent proxy replicas from a trigger such as HTTP concurrency. Each replica adds workers and queue capacity but keeps its own queue, health observations, and circuit state.

</td>
<td width="33%" valign="top">

### [When should clients stop waiting synchronously?](#when-should-clients-stop-waiting-synchronously-1)
Use async mode when processing may exceed the practical HTTP wait budget. The proxy can return `202`, continue processing, store the result in Blob Storage, and publish status through Service Bus.

</td>
</tr>
</table>

---

## Full Answers

### How do user profiles determine when requests run?

#### Where does a user's priority come from?

Incoming requests can pass in the priority as a header.

When user profiles are enabled, the proxy caches the list of user profiles from CosmosDB into memory. For every incoming request, it matches the request to a profile and then assigns the priority.

**Example:** With `PriorityKeys=high,medium,low` and `PriorityValues=1,2,3`, a profile containing `"S7PPriorityKey": "high"` receives priority `1`.

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

### How are proxy capacity and unhealthy backends protected?

#### What is backpressure?

Backpressure protects the proxy when work arrives faster than it can be processed. The proxy first slows admission, then rejects requests with HTTP 429 when event backlog, queue capacity, backend availability, or enqueue limits are unsafe.

#### What is circuit breaking?

Circuit breaking protects unhealthy backends. It counts failures within a configured time window and stops admitting new work when the failure threshold is reached. Defaults are 50 failures in 60 seconds.

#### How are they different?

* Backpressure: Responds to proxy/system capacity pressure.
* Circuit breaker: Responds specifically to repeated backend failures.
* Circuit breaking is one signal that can activate the proxy’s broader backpressure behavior.

#### What does the caller observe when protection activates?

The circuit breaker does not switch from healthy to blocked immediately. As error rates, queue depth, or resource pressure increase, the proxy first enters a protective mode where requests are intentionally slowed down. This added latency acts as backpressure, giving downstream services time to recover and reducing the likelihood of a cascading failure.

If conditions continue to deteriorate and configured thresholds are exceeded, the circuit fully opens and new requests are rejected with HTTP 429 (Too Many Requests). The response includes a specific reason, such as:

* Max Events Exceeds Threshold
* Too many failures in the last 60 seconds
* Queue is full
* No active hosts
* Failed to enqueue request

A Retry-After header is returned so callers know when to try again.

During a planned shutdown or maintenance event, the proxy instead returns HTTP 503 (Service Unavailable).

#### What happens before a circuit fully opens?

Once failures reach 50% of the threshold, the proxy progressively delays incoming requests:
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

The selector first filters `Host` entries by request path, orders the matching hosts using `LoadBalanceMode`, and then skips unhealthy or open-circuit hosts. It does not use queue priority to determine backend eligibility.

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

An APIM backend points a `Host_<name>` entry at Azure API Management. `mode=apim` is standard non-direct behavior: the proxy calls the configured probe and can remove APIM from the active set when health falls below the required success rate.

```bash
Host_<name>="host=https://gateway.azure-api.net;mode=apim;path=/shared;probe=/health"
```

#### Why put APIM behind the proxy?

APIM can supply API gateway capabilities such as caller authentication, subscriptions, transformations, and priority-aware backend policies. The proxy adds its own queue, worker controls, health tracking, circuit breaking, and telemetry around that gateway path.

See [Backend Host Configuration](../BACKEND_HOSTS.md) for all host options.

---

### How does autoscaling expand proxy capacity?

#### What causes the proxy to scale out?

Azure Container Apps uses KEDA-based triggers. HTTP concurrency is useful for bursty, streaming, or long-lived traffic; CPU is useful for steadier compute-bound traffic. When demand exceeds the configured target, ACA adds replicas up to `maxReplicas`.  Each replica is allowed `terminationGracePeriodSeconds` to complete requests before being forced closed.

#### What does each new replica contain?

Replicas operate independently. Each replica maintains its own queue, workers, fairness counters, backend health observations, and circuit-breaker state in memory. When a new replica is created, it starts with no queued work or operational history. Requests already queued on existing replicas are not moved, although `async` workloads can be redistributed through shared queueing systems.

#### Does scale-out redistribute queued requests or state?

No. Scale-out adds capacity for newly routed traffic, but queued requests stay on the replica that admitted them. Backend health and circuit-breaker decisions also remain local to each replica.

#### What happens when a replica shuts down?

The replica stops accepting new work and allows in-progress requests to complete for up to 30 minutes. Azure Container Apps starts a replacement replica set while the old replica drains, so new traffic moves to replacement capacity instead of entering the terminating replica.

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

## You Should Now Be Able To

- [ ] Explain how a profile becomes a queue priority
- [ ] Explain how a profile can override the requested model
- [ ] Distinguish backpressure from circuit breaking
- [ ] Explain where priority-aware backend routing occurs
- [ ] Choose between direct and APIM backend modes
- [ ] Describe what changes and what stays local during autoscale and shutdown
- [ ] Decide when a request should use async processing

---

## Related Documents

| Document | What it covers |
|----------|----------------|
| [User Profiles](../USER_PROFILES.md) | Per-user priority and async fields |
| [Load Balancing](../LOAD_BALANCING.md) | Native backend selection and retry |
| [Backend Hosts](../BACKEND_HOSTS.md) | Direct and probed host configuration |
| [Get It Running](02-get-it-running.md) | The next discovery path for running the proxy |

---
