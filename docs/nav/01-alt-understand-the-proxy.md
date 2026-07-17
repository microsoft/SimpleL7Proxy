# Understand How the Proxy Controls AI Traffic

Sending every request to an endpoint works great during POC and early dev phases, however as products mature they need greater control with priorities, routing, cost controls, reliability and observability all thrown into the mix.  The SimpleL7Proxy lets you decide how to route, retry , retry later, share costs and fullfill requests based on the application / user profile.

<img width="1531" height="591" alt="image" src="https://github.com/user-attachments/assets/835a47ea-d6cc-4106-8be6-71f2f4cbb838" />

There's a lot to unpack in this diagram, however the main points are:
1. Retry from the other region(s) when endpoints are **throttled**, **overloaded** or **unreachable**
2. Try the next endpoint if the first one is in a **throttled** state
3. Send higher priority requests ahead of the others, but don't let older requests starve
4. Decide which requests are given the white glove treatment and which ones can go to the end of the line.
5. Decide which LLM model is best for the type of workload to further control costs.

The user profile adds a layer of control by allowing requests to be:

1. **Validated** by requiring fields
2. Cleaned by **stripping headers**
3. **Rerouted** to specific endpoints and models
4. **Prioritized** into multiple levels:  P1, P2, P3, ...
5. Nominated for **Async** priocessing if the response takes a long time

---

## Quick Answers

<table>
<tr>
<td width="33%" valign="top">

### [User profiles can determine Model and Priority?](#how-do-user-profiles-determine-queue-priority-1)
The proxy can map users to profiles and then to capabilities.  The combination of model and priority can be used to route requests to different endpoints. 

</td>
<td width="33%" valign="top">

### [How do backpressure and circuit breaking protect the proxy?](#how-do-backpressure-and-circuit-breaking-protect-the-proxy-1)
Backpressure slows down the rate of incoming requests when a replica is busy fulfilling existing requests.  Circuit breaking stops receiving new work and to backends that have to many failures.

</td>
<td width="33%" valign="top">

### [How does priority route requests to alternate backends?](#how-does-priority-route-requests-to-alternate-backends-1)
An APIM priority policy can restrict each backend to selected request priorities, then try another eligible backend when the first is throttled or fails.

</td>
</tr>
<tr>
<td width="33%" valign="top">

### [What is a direct backend versus an APIM backend?](#what-is-a-direct-backend-versus-an-apim-backend-1)
A direct backend skips active health probes and is always admitted to the active set. An APIM backend is probed like a standard gateway backend and can provide policies, and transformations. Both support using Oauth or key based auth.

</td>
<td width="33%" valign="top">

### [How does autoscale work?](#how-does-autoscale-work-1)
Azure Container Apps adds or removes independent proxy replicas from a trigger such as HTTP concurrency, within configured minimum and maximum replica limits.

</td>
<td width="33%" valign="top">

### [When does async become important?](#when-does-async-become-important-1)
Async matters when backend processing may exceed the practical HTTP wait budget. The proxy can return `202`, write results to Blob Storage, and publish status through Service Bus.

</td>
</tr>
</table>

---

## Full Answers

### How do user profiles determine queue priority?

#### Where does a user's priority come from?

When profiles are enabled, the proxy looks up the caller using the configured user ID field. A matched profile can provide `S7PPriorityKey`, such as `high`, `medium`, or `low`. The proxy maps that value using the paired `PriorityKeys` and `PriorityValues` settings.

**Example:** With `PriorityKeys=high,medium,low` and `PriorityValues=1,2,3`, a profile containing `"S7PPriorityKey": "high"` receives priority `1`.

#### What does the priority number change?

It changes when a queued request is selected by a worker. Lower integers run first, so priority `1` is selected before priorities `2` and `3`. Requests with the same primary priority are ordered by a secondary user-fairness value and then enqueue time.

#### What happens when no profile priority is available?

The proxy uses `DefaultPriority`, which defaults to `2`, unless the configured priority header contains a recognized key. An unknown or missing key does not create a new priority.

#### How does the proxy prevent one user from dominating a priority?

The proxy tracks each user's share of active requests. A user below `UserPriorityThreshold` receives a secondary fairness boost within the same primary priority. This boost does not allow a lower primary priority to overtake a higher one.

See [User Profiles](../USER_PROFILES.md) for profile structure and loading.

---

### How do backpressure and circuit breaking protect the proxy?

#### What is backpressure?

Backpressure is the proxy's refusal to admit more work when a replica is already at a safety limit. The proxy returns `429` when its queue reaches `MaxQueueLength`, when telemetry backlog exceeds its limit, when no host is active, or when circuit state blocks all backends.

#### What is circuit breaking?

Circuit breaking protects a specific backend from repeated calls while it is failing. When failures reach `CBErrorThreshold` inside the `CBTimeslice` sliding window, the circuit opens and the backend selector skips that host. It closes automatically after enough failures age out.

#### How are they different?

Backpressure answers **"Can this proxy replica accept the request?"** Circuit breaking answers **"Can this backend safely receive an attempt?"** Increasing queue capacity does not repair an unhealthy backend, and raising a circuit threshold does not create more proxy capacity.

#### What happens before a circuit fully opens?

The proxy adds progressive delays as failures rise from 50% to 90% of the configured threshold. This slows traffic into a degrading backend before the circuit blocks it completely.

See [Circuit Breaker](../CIRCUIT_BREAKER.md) for thresholds, delays, and recovery.

---

### How does priority route requests to alternate backends?

#### Does queue priority select a native proxy backend?

No. Native SimpleL7Proxy backend selection filters hosts by request path, orders them by `LoadBalanceMode`, and skips unhealthy or open-circuit hosts. Queue priority controls when a worker receives the request, not which `HostN` receives it.

#### Where does priority-aware backend routing happen?

The supplied APIM priority policy reads `llm_proxy_priority`. Each APIM backend declares `acceptablePriorities`; backends that do not accept the request's priority are removed before attempts begin. `priorityGroup` determines the order among eligible backends.

#### What happens when the preferred backend fails?

APIM can try the next backend that accepts the same request priority. A priority-1 request can therefore move from a reserved priority-1 backend to another priority-1-eligible backend without becoming eligible for a priority-2-only backend.

#### What happens when no backend accepts the priority?

APIM returns `503 Service Unavailable`. Changing retry count cannot help because the candidate set is empty.

See [Priority Levels POC](../POC-Priority-configuration.md) for a runnable example.

---

### What is a direct backend versus an APIM backend?

#### What is a direct backend?

A direct backend uses `mode=direct`. The proxy does not send active health probes to it and always includes it in the active host set. Real request failures are still recorded by the circuit breaker.

```bash
Host1="host=https://model.example.com;mode=direct;path=/model"
```

#### When should I use direct mode?

Use it when probing would be unsafe or undesirable—for example, when a serverless target scales to zero or has no suitable probe endpoint. Because direct mode has no probe-derived latency, it sorts first when `LoadBalanceMode=latency`.

#### What is an APIM backend?

An APIM backend points a `HostN` entry at Azure API Management. `mode=apim` is standard non-direct behavior: the proxy calls the configured probe and can remove APIM from the active set when health falls below the required success rate.

```bash
Host2="host=https://gateway.azure-api.net;mode=apim;path=/shared;probe=/health"
```

#### Why put APIM behind the proxy?

APIM can supply API gateway capabilities such as caller authentication, subscriptions, transformations, and priority-aware backend policies. The proxy adds its own queue, worker controls, health tracking, circuit breaking, and telemetry around that gateway path.

See [Backend Host Configuration](../BACKEND_HOSTS.md) for all host options.

---

### How does autoscale work?

#### What causes the proxy to scale out?

Azure Container Apps uses KEDA-based triggers. HTTP concurrency is useful for bursty, streaming, or long-lived traffic; CPU is useful for steadier compute-bound traffic. When demand exceeds the configured target, ACA adds replicas up to `maxReplicas`.

#### What does each new replica contain?

Each replica has its own in-memory queue, worker pool, user-share counters, backend health observations, and circuit-breaker state. A request already queued on one replica does not move to a newly created replica.

#### How should minimum and maximum replicas be chosen?

Use `minReplicas` of at least `1` to avoid cold starts on a latency-sensitive path, and increase it for availability. Set `maxReplicas` from downstream backend capacity rather than proxy CPU alone; scaling the proxy cannot increase a backend's quota.

#### How does autoscale interact with workers and queue length?

`Workers` limits concurrent processing inside each replica, while `MaxQueueLength` limits waiting work in that replica. The ACA scale target should reflect measured per-replica worker and backend connection capacity. Repository deployment templates contain different concurrency targets, so their values are starting points rather than a universal default.

See [Day 2 Operations](../../deployment/DAY2_OPERATIONS.md#scaling-considerations) for scaling guidance.

---

### When does async become important?

#### What problem does async solve?

Async separates backend processing time from the caller's HTTP connection lifetime. It is useful for long model runs, background jobs, or any request likely to exceed a client, gateway, or network timeout.

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
- [ ] Distinguish backpressure from circuit breaking
- [ ] Explain where priority-aware backend routing occurs
- [ ] Choose between direct and APIM backend modes
- [ ] Describe what changes and what stays local during autoscale
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
