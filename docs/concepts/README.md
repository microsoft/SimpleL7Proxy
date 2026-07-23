# Understanding the SimpleL7Proxy

SimpleL7Proxy manages the lifecycle of an AI request from arrival through execution, retry, and completion. Before processing begins, the proxy can enrich the request using a user profile that supplies priority, model overrides, async permissions, and reporting metadata.

Incoming requests are admitted through backpressure controls and placed into priority-aware queues. Priority determines not only processing order, but also which regions, endpoints, capacity pools, and retry paths are available. Critical workloads can access additional capacity and more aggressive failover behavior, while lower-priority workloads may be delayed, requeued, or restricted to specific backend resources.

The proxy continuously selects healthy backends, retries failures across regions when appropriate, and can promote long-running operations to asynchronous processing. Throughout the request lifecycle it records routing decisions, performance metrics, and usage data for observability and governance. Azure Container Apps independently scales proxy capacity to match demand.

Requests that run longer than expected can be converted to background operations so callers are not required to keep HTTP connections open. Throughout the process, the proxy records routing decisions, performance metrics, and cost data for observability and governance.

Azure Container Apps independently scales the proxy up and down to match demand.

<img width="1308" height="534" alt="image" src="https://github.com/user-attachments/assets/60b20f0c-cee1-44b7-8f6a-b97d84f590bf" />

---
## Quick Topics

<table>
<tr>
<td width="33%" valign="top">

### The User Profile

The proxy loads **user profiles** from CosmosDB and, at receive time, enriches each incoming request with its matching profile data, mapped to key-value settings. The **priority** setting controls processing order, while other settings can override the requested **model** or report **metrics**. Additional fields can validate, map, or clean up the request, and drive further routing and policy decisions in both the proxy and APIM. When no profile matches, the proxy falls back to a default priority.

[See the FAQ →](understand-faq.md#how-do-user-profiles-determine-when-requests-run)

</td>
<td width="33%" valign="top">

### Priority Levels

Priority is the mechanism that lets organizations allocate AI capacity according to business importance. A request's **priority** determines its queue position, which backends it may use, whether it can consume **PTU** or **PayGo** capacity, how aggressively it is retried, and how it is treated during periods of contention. Critical workloads receive stronger reliability guarantees, while less important workloads may be delayed, requeued, or restricted to lower-cost capacity.

Rule of thumb:
* **Service** all workloads
* **Control** costs
* **Prioritize** workloads

[See the FAQ →](understand-faq.md#what-does-a-requests-priority-level-control)

</td>
<td width="33%" valign="top">

### Backend Selection & Failover

The proxy builds on top of **APIM** as a cross-region load balancer: it tries each configured backend host in turn — APIM instances and direct endpoints alike — retrying across backends and regions until a request succeeds or its TTL expires. APIM keeps running independently underneath, adding its own extensive governance — routing, compliance, and more — via its policy engine.

Because **direct** backends skip active probing, no latency data is available for them, so they rely on path-based routing combined with `random` or `roundrobin` load balancing.

[See the FAQ →](understand-faq.md#how-does-apim-determine-which-backends-receive-requests)

</td>

</tr>
<tr>
<td width="33%" valign="top">

### Queueing & Fairness

All requests are filtered through a **priority queue** that ensures high priority work is favored over lower priority work.  At the same time, A flood of high-priority traffic can never **starve** out lower-priority requests, and no single user can **monopolize** a priority level at everyone else's expense.

[See the FAQ →](understand-faq.md#how-does-the-proxy-keep-queueing-fair-across-users)

</td>

<td width="33%" valign="top">

### Long-Running Requests

Long-running requests don't need to hold a client connection open. When a request runs past a configured timeout, the proxy promotes it to a **background operation**: it returns `202` right away, keeps working, and delivers the result through **Blob Storage** and **Service Bus** status updates. Any API call can opt in, but it matters most for long-running LLM queries that would otherwise risk a timeout.

[See the FAQ →](understand-faq.md#when-should-clients-stop-waiting-synchronously)

</td>
<td width="33%" valign="top">

### Observability

The proxy logs full request activity — including AI token usage pulled from streaming responses — to Application Insights, Event Hub, local files, or your own custom code.

[See the FAQ →](understand-faq.md#how-does-the-proxy-handle-observability-and-telemetry)

</td>
</tr><tr>
<td colspan="3" width="100%" valign="top">

### Resiliency & Autoscaling with ACA

Each replica protects itself and signals its health to ACA. On the inbound side, **backpressure** progressively delays requests as the replica saturates; on the backend side, a **circuit breaker** stops sending traffic to a failing backend until it recovers.

ACA makes all scaling decisions using the proxy's `/startup`, `/liveness`, and `/readiness` probes — it **scales out** when connections per replica cross a configured threshold, recycles replicas under sustained backpressure, and gives scaled-in replicas a grace period to **drain** active connections before shutdown.

[See the FAQ →](understand-faq.md#how-does-the-proxy-stay-resilient-and-autoscale-with-aca)

</td>
</tr>
</table>

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
