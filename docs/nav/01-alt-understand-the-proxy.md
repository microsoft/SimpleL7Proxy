# Understand How the Proxy Controls AI Traffic

SimpleL7Proxy controls when requests run, which backends receive them, how failures are contained, and when clients stop waiting synchronously. These six topics explain the decisions that matter most when designing an AI traffic path.

---

## Quick Answers

<table>
<tr>
<td width="33%" valign="top">

### [How do user profiles determine when requests run?](#how-do-user-profiles-determine-when-requests-run-1)
A user profile can assign a queue priority and override the requested model. Lower-numbered priorities run first, while a model override rewrites the request before it reaches the backend.

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

When profiles are enabled, the proxy looks up the caller using the configured user ID field. A matched profile can provide `S7PPriorityKey`, such as `high`, `medium`, or `low`. The proxy maps that value using the paired `PriorityKeys` and `PriorityValues` settings.

**Example:** With `PriorityKeys=high,medium,low` and `PriorityValues=1,2,3`, a profile containing `"S7PPriorityKey": "high"` receives priority `1`.

#### What does the priority number change?

It changes when a queued request is selected by a worker. Lower integers run first, so priority `1` is selected before priorities `2` and `3`. Requests with the same primary priority are ordered by a secondary user-fairness value and then enqueue time.

#### When does the profile priority take effect?

The proxy resolves the profile before admitting the request to the queue. It assigns the mapped priority when the request is enqueued, so the value affects dispatch order as soon as the request begins waiting for a worker.

#### Can a profile change the requested model?

Yes. A user profile can specify a model override. The proxy rewrites the original request to use that model before forwarding it, so model selection can be controlled per user without requiring the caller to change the request.

#### What happens when no profile priority is available?

The proxy uses `DefaultPriority`, which defaults to `2`, unless the configured priority header contains a recognized key. An unknown or missing key does not create a new priority.

#### How does the proxy prevent one user from dominating a priority?

The proxy tracks each user's share of active requests. A user below `UserPriorityThreshold` receives a secondary fairness boost within the same primary priority. This boost does not allow a lower primary priority to overtake a higher one.

See [User Profiles](../USER_PROFILES.md) for profile structure and loading.

---

### How are proxy capacity and unhealthy backends protected?

#### What is backpressure?

Backpressure controls admission rather than execution speed. As undrained telemetry events accumulate in memory, the proxy progressively delays accepting new work so the event sink can catch up. Requests that are already admitted continue processing as fast as possible, and the admission delay disappears after the backlog drains.

#### What is circuit breaking?

Circuit breaking protects a specific backend from repeated calls while it is failing. When failures reach `CBErrorThreshold` inside the `CBTimeslice` sliding window, the circuit opens and the backend selector skips that host. It closes automatically after enough failures age out.

#### How are they different?

Backpressure answers **"How quickly can this replica accept more work?"** Circuit breaking answers **"Can this backend safely receive an attempt?"** Admission delay protects the proxy's telemetry pipeline; delaying or skipping a failing backend protects the downstream service.

#### What does the caller observe when protection activates?

New requests take progressively longer to be admitted while the telemetry backlog is elevated. After admission, they are not intentionally slowed by backpressure. A new request receives `429` if the backlog exceeds `MaxUndrainedEvents` or the queue reaches `MaxQueueLength`.

#### What happens before a circuit fully opens?

The proxy circuit breaker adds progressive delays as failures rise from 50% to 90% of the configured threshold. This slows traffic into a degrading backend before the circuit blocks that backend completely.

See [Circuit Breaker](../CIRCUIT_BREAKER.md) for thresholds, delays, and recovery.

---

### How does APIM determine which backends receive requests?

#### Does queue priority select a native proxy backend?

No. Native SimpleL7Proxy backend selection filters hosts by request path, orders them by `LoadBalanceMode`, and skips unhealthy or open-circuit hosts. Queue priority controls when a worker receives the request, not which `HostN` receives it.

#### Where does priority-aware backend routing happen?

The supplied APIM priority policy reads `llm_proxy_priority`. Each APIM backend declares `acceptablePriorities`; backends that do not accept the request's priority are removed before attempts begin. `priorityGroup` determines the order among eligible backends.

#### How does the APIM policy handle a throttled endpoint?

When an endpoint returns `429`, the supplied policy records its retry time and marks it as throttled. Later requests skip that endpoint until the retry time passes, so APIM can use another endpoint instead of immediately repeating an attempt that is expected to throttle.

#### What controls native SimpleL7Proxy backend selection?

The native selector first filters `HostN` entries by request path, orders the matching hosts using `LoadBalanceMode`, and then skips unhealthy or open-circuit hosts. It does not use queue priority to determine backend eligibility.

#### What happens when the preferred backend fails?

APIM can try the next backend that accepts the same request priority. A priority-1 request can therefore move from a reserved priority-1 backend to another priority-1-eligible backend without becoming eligible for a priority-2-only backend.

#### What happens when every endpoint in an APIM region is throttled?

After the APIM policy exhausts its eligible endpoints, it can return `429` with `S7PREQUEUE: true` and a retry delay. SimpleL7Proxy collects that response and tries the next configured APIM host, allowing the request to move to another region.

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
Host1="host=https://model.example.com;mode=direct;path=/model"
```

#### When should I use direct mode?

Use it when probing would be unsafe or undesirable—for example, when a serverless target scales to zero or has no suitable probe endpoint. Because direct mode has no probe-derived latency, it sorts first when `LoadBalanceMode=latency`.

#### When should I route through APIM?

Use APIM in the backend path when requests need gateway policies, transformations, subscriptions, caller authentication, or priority-aware selection across the services behind APIM. This adds APIM as an operational dependency, so use it for capabilities the direct path does not provide.

#### What is an APIM backend?

An APIM backend points a `HostN` entry at Azure API Management. `mode=apim` is standard non-direct behavior: the proxy calls the configured probe and can remove APIM from the active set when health falls below the required success rate.

```bash
Host2="host=https://gateway.azure-api.net;mode=apim;path=/shared;probe=/health"
```

#### Why put APIM behind the proxy?

APIM can supply API gateway capabilities such as caller authentication, subscriptions, transformations, and priority-aware backend policies. The proxy adds its own queue, worker controls, health tracking, circuit breaking, and telemetry around that gateway path.

See [Backend Host Configuration](../BACKEND_HOSTS.md) for all host options.

---

### How does autoscaling expand proxy capacity?

#### What causes the proxy to scale out?

Azure Container Apps uses KEDA-based triggers. HTTP concurrency is useful for bursty, streaming, or long-lived traffic; CPU is useful for steadier compute-bound traffic. When demand exceeds the configured target, ACA adds replicas up to `maxReplicas`.

#### What does each new replica contain?

Each replica has its own in-memory queue, worker pool, user-share counters, backend health observations, and circuit-breaker state. A request already queued on one replica does not move to a newly created replica.

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
