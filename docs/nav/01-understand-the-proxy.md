# Understand How the Proxy Controls AI Traffic

This guide explains the six decisions that determine which requests run, where they run, and whether callers wait for the result.

> **TL;DR**
> - **Queue priority and backend routing are separate decisions:** profiles influence when a request runs; routing determines where it runs.
> - Backpressure protects the proxy, while health checks and circuit breakers protect it from unhealthy backends.
> - Autoscale adds independent proxy replicas; async mode frees callers from waiting on long-running work.

## Quick Answers

| Question | Short answer |
|---|---|
| [How do user profiles determine queue priority?](#prioritizing-users-in-the-queue) | A profile supplies an `S7PPriorityKey`; `PriorityKeys` and `PriorityValues` map that key to an integer. Lower integers leave the queue first. |
| [How do backpressure and circuit breaking differ?](#protecting-capacity-with-backpressure-and-circuit-breaking) | Backpressure rejects excess inbound work. Circuit breaking stops attempts to a backend with too many recent failures. |
| [How does priority route to alternate backends?](#routing-priorities-to-alternate-backends) | APIM policy can restrict each backend to selected request priorities. The proxy's native backend selector routes by path, health, and load-balance order instead. |
| [What is a direct backend versus an APIM backend?](#choosing-direct-or-apim-backends) | Direct mode skips active health probes. APIM mode treats APIM as a standard, probed backend gateway. |
| [How does autoscale work?](#scaling-proxy-replicas) | Azure Container Apps adds replicas from a scale trigger such as HTTP concurrency, within configured minimum and maximum bounds. |
| [When does async become important?](#moving-long-running-work-to-async) | Use async when a caller or gateway must not hold an HTTP connection for the full backend runtime. |

## Settings at a Glance

Units used in this guide: queue limits are requests, circuit-breaker windows are seconds, and async trigger timeouts are milliseconds.

| Setting | Default | What it controls | Reload |
|---|---:|---|---|
| `DefaultPriority` | `2` | Queue priority when no mapped key is present | Warm |
| `PriorityKeys` / `PriorityValues` | `high,medium,low` / `1,2,3` | Maps profile or request keys to queue priorities | Warm |
| `UserPriorityThreshold` | `0.1` | Share below which a user receives the secondary fairness boost | Warm |
| `MaxQueueLength` | `1000` | Maximum queued requests per replica | Cold |
| `CBErrorThreshold` | `50` | Recent failures that open a backend circuit | Warm |
| `CBTimeslice` | `60` s | Circuit-breaker sliding window | Warm |
| `AsyncModeEnabled` | `false` | Enables async processing service-wide | Cold |
| `AsyncTriggerTimeout` | `10000` ms | Wait before an opted-in request changes to async | Warm |
| ACA `minReplicas` / `maxReplicas` | Deployment-specific | Lower and upper replica bounds | Deployment |
| ACA `concurrentRequests` | Deployment-specific | Per-replica HTTP concurrency scale target | Deployment |

> [!NOTE]
> Warm settings reload through Azure App Configuration when configured. Cold settings require a proxy restart.

## One Request, Two Decisions

**The queue decides when work starts; the backend selector or APIM policy decides where it runs.**

```text
Client/profile ──priority key──► [1. per-replica priority queue] ──► worker
                                                                      │
                           [backpressure: reject 429] ◄────────────────┤
                                                                      ▼
                 APIM priority policy OR proxy path/load-balance selector
                                      │
                         [health + circuit gate]
                                      ▼
                    direct service or APIM-fronted backend
                                      │
                   sync response OR async 202 + Blob/Service Bus
```

The first decision protects worker capacity and orders users. The second narrows and orders backend candidates. Health and circuit state can remove a candidate after routing has selected the candidate set.

## Prioritizing Users in the Queue

**A profile's `S7PPriorityKey` maps to the primary queue priority; lower mapped integers run first.**

```bash
PriorityKeys=high,medium,low
PriorityValues=1,2,3
DefaultPriority=2
```

A matched user profile can supply `"S7PPriorityKey": "high"`. The proxy maps `high` to `1`, places that request ahead of priorities `2` and `3`, and uses enqueue time to preserve order among otherwise equal requests.

The proxy also tracks each user's share of active work. When a user is below `UserPriorityThreshold`, the request receives a secondary fairness boost. This favors an underrepresented user within the same primary priority; it does not let a lower primary priority jump ahead of a higher one.

> [!TIP]
> **Troubleshooting:** If a profile appears to have no effect, verify that its key exactly matches a value in `PriorityKeys`, that `PriorityKeys` and `PriorityValues` have the same number of entries, and that the configured user ID header resolves to the expected profile.

See [User Profiles](../USER_PROFILES.md) for profile loading and field structure.

## Protecting Capacity with Backpressure and Circuit Breaking

**Backpressure limits admitted work; circuit breaking prevents admitted work from repeatedly hitting a failing backend.**

```bash
MaxQueueLength=1000
CBErrorThreshold=50
CBTimeslice=60
```

Backpressure acts at ingress on each replica. The proxy returns `429` when its queue is full, when telemetry backlog exceeds its safety limit, when no backend is active, or when all registered circuit breakers block processing. Before reaching the limit, a growing telemetry backlog can add a small admission delay.

Each backend has its own sliding failure window. At the threshold, its circuit opens and the backend selector skips that host. Old failures age out after `CBTimeslice`, allowing the circuit to close automatically. Progressive delays begin before the threshold to slow traffic into a degrading backend.

> [!WARNING]
> **Troubleshooting:** A `429` can mean capacity backpressure or unavailable backends. Read the response message and telemetry before increasing `MaxQueueLength`; a larger queue does not repair an unhealthy backend.

See [Circuit Breaker](../CIRCUIT_BREAKER.md) for failure accounting and recovery.

## Routing Priorities to Alternate Backends

**Use the APIM priority policy when request priority must determine backend eligibility; do not confuse this with the proxy's queue priority.**

```text
priority 1 → Reserved backend accepts [1]
priority 2 → Shared backend accepts [2,3]
priority 3 → Shared or fallback accepts [3]
```

The APIM policy reads `llm_proxy_priority`, builds a candidate set from each backend's `acceptablePriorities`, then orders eligible candidates by `priorityGroup`. A throttled or failed candidate can be bypassed for another eligible backend. If no backend accepts the request priority, APIM returns `503`.

Native SimpleL7Proxy `Host1`…`HostN` selection does not use queue priority to filter hosts. It filters by request path, orders by `LoadBalanceMode`, skips unhealthy or open-circuit hosts, and retries according to `IterationMode`.

> [!TIP]
> **Troubleshooting:** If a priority request reaches an unexpected backend, first identify the routing layer. In APIM, inspect `acceptablePriorities` and `priorityGroup`; in SimpleL7Proxy, inspect `path`, health, circuit state, and load-balance mode.

See [Priority Levels POC](../POC-Priority-configuration.md) for a runnable APIM example.

## Choosing Direct or APIM Backends

**Choose `mode=direct` only when the proxy must not actively probe the target; use `mode=apim` for a probed APIM gateway.**

```bash
Host1="host=https://model.example.com;mode=direct;path=/model"
Host2="host=https://gateway.azure-api.net;mode=apim;path=/shared;probe=/health"
# Both remain subject to per-request circuit breaking.
```

| Backend type | Proxy target | Active probe | Typical reason |
|---|---|---|---|
| Direct | Service or model endpoint | No | The target scales to zero, is on demand, or has no safe probe endpoint |
| APIM | Azure API Management gateway | Yes | APIM supplies authentication, policy, subscriptions, transformations, or priority-aware routing |

Direct mode always admits the host to the active set and relies on real request failures plus the circuit breaker for protection. APIM mode is the standard non-direct behavior: the proxy polls the configured probe and removes the gateway from the active set when probe success falls below the health threshold.

> [!TIP]
> **Troubleshooting:** If a scale-to-zero target wakes unexpectedly, check that it uses `mode=direct`. If an APIM backend never becomes active, call its configured probe path through APIM and verify authentication and network access.

See [Backend Host Configuration](../BACKEND_HOSTS.md) for all host options.

## Scaling Proxy Replicas

**Autoscale creates independent queues and workers; set the scale target from measured per-replica capacity and downstream limits.**

```yaml
minReplicas: 1
maxReplicas: 10
concurrentRequests: "100"
```

Azure Container Apps uses KEDA-based triggers such as CPU or HTTP concurrency. When observed concurrency exceeds the configured target, ACA adds replicas up to `maxReplicas`; it removes replicas as demand falls, but not below `minReplicas`.

Every replica owns its queue, user-share counters, workers, backend health observations, and circuit-breaker state. Scaling out increases total proxy capacity, but state is not pooled across replicas. A request already queued on one replica does not move to a newly created replica.

> [!NOTE]
> Repository deployment variants use different concurrency targets. Treat template values as starting points, not a universal default. Keep at least one replica for a latency-sensitive path and cap scale based on backend capacity.

> [!TIP]
> **Troubleshooting:** If replicas increase while queue latency remains high, compare the ACA ingress concurrency target with `Workers` and backend connection capacity. Autoscaling cannot make a saturated downstream service faster.

See [Day 2 Operations](../../deployment/DAY2_OPERATIONS.md#scaling-considerations) for scaling guidance.

## Moving Long-Running Work to Async

**Use async when processing can outlive the caller's practical HTTP wait budget.**

```bash
AsyncModeEnabled=true
AsyncTriggerTimeout=10000
# Client sends: S7PAsyncMode: true
```

Async requires three opt-ins: the proxy service enables it, the user's profile grants it and names a Blob container and Service Bus topic, and the request sends the configured async header. Fast requests still finish synchronously. Once processing exceeds `AsyncTriggerTimeout`, the proxy returns `202`, stores request and response data in Blob Storage, and publishes lifecycle status through Service Bus.

Async becomes important for long model runs, background jobs, or gateway/client timeouts that are shorter than backend processing. It changes delivery semantics and introduces Storage, Service Bus, RBAC, retention, and client status-handling requirements, so it is unnecessary for reliably short requests.

> [!WARNING]
> **Troubleshooting:** No `202` means one of the three opt-ins is missing or the request completed before `AsyncTriggerTimeout`. Verify the service setting, profile `async-config`, request header, and Azure permissions.

See [Async Operation Configuration](../AsyncOperation.md) for the complete setup.

## Worked Example

**This example shows queue order, backend eligibility, circuit behavior, scale, and async as separate stages.**

| Step | Concrete event | Effective outcome |
|---:|---|---|
| 1 | Premium user profile maps `high` → priority `1`; batch user maps `low` → `3` | Premium request leaves the replica queue first |
| 2 | Premium request carries APIM routing priority `1` | APIM considers only backends whose `acceptablePriorities` contains `1` |
| 3 | Reserved backend circuit is open | It is skipped; another priority-1-eligible backend is tried |
| 4 | Queue reaches `MaxQueueLength=1000` | That replica rejects additional requests with `429` |
| 5 | ACA HTTP concurrency crosses its configured target | ACA starts another replica, which has a new empty queue and independent circuit state |
| 6 | An opted-in batch request runs longer than `10000` ms | Caller receives `202`; completion data moves through Blob Storage and Service Bus |

The mental model is: **identify user → assign queue priority → admit or reject → select eligible backend → skip unhealthy candidates → return synchronously or detach asynchronously**.

## You Should Now Be Able To

- [ ] Explain why queue priority does not automatically select a backend
- [ ] Distinguish capacity backpressure from backend circuit breaking
- [ ] Choose between direct and APIM backend modes
- [ ] Explain what state is local when ACA adds a replica
- [ ] Decide whether a workload needs async delivery

## Related Documents

| Document | What it covers |
|---|---|
| [Overview](../OVERVIEW.md) | Components and high-level workflows |
| [Load Balancing](../LOAD_BALANCING.md) | Native backend selection and retries |
| [User Profiles](../USER_PROFILES.md) | Per-user profile fields and loading |
| [Get It Running](02-get-it-running.md) | Local and Azure startup paths |
