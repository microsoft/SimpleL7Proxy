# Understand SimpleL7Proxy

SimpleL7Proxy is a self-hosted HTTP data-plane proxy that protects Azure AI workloads from backend contention and failure.

## TL;DR

- **It governs backend traffic:** requests are validated, queued by priority, and sent to healthy backends selected by path and load-balancing rules.
- **It contains backend failures:** per-host circuit breakers, cross-host retry, and optional requeue keep transient backend failures away from callers.
- **It complements an API gateway:** Azure API Management (APIM) can manage callers and API products upstream while SimpleL7Proxy manages backend execution.

> [!IMPORTANT]
> **SimpleL7Proxy owns the request path from its queue to the backend. It is not a managed AI service or a replacement for a full API gateway.**

## Identify What It Is

**Use SimpleL7Proxy when the problem is backend execution—capacity contention, priority, failover, or AI telemetry—not API product management.**

| Need | SimpleL7Proxy | APIM or another API gateway | Layer-4 load balancer |
|---|---:|---:|---:|
| Priority queue and worker concurrency | Yes | Policy-dependent | No |
| Backend health, circuit breaking, and cross-host retry | Yes | Policy-dependent | Transport-level only |
| Path-aware backend selection | Yes | Yes | No |
| Streaming AI token telemetry | Yes, with a stream processor | Policy-dependent | No |
| Developer portal, products, and subscriptions | No | Yes | No |
| Caller authentication and policy transformation | Limited validation | Yes | No |

> [!NOTE]
> APIM is optional. A client can call SimpleL7Proxy directly, or APIM can sit in front of it. APIM policy behavior and native SimpleL7Proxy behavior are separate layers.

### What problem does SimpleL7Proxy solve that a standard Layer-4 load balancer cannot?

A Layer-4 load balancer routes TCP connections using addresses and ports. SimpleL7Proxy reads HTTP paths, headers, and status codes, so it can prioritize requests, select an eligible backend, retry a failed attempt, and extract telemetry from an AI response.

### What is a Layer 7 proxy and why does that distinction matter for AI workloads?

Layer 7 is the HTTP application layer. Access to HTTP semantics lets the proxy distinguish a successful response from a backend `429` or `5xx`, route `/openai/` and `/embeddings/` differently, and process streaming response content without changing the client protocol.

### What are the core capabilities in plain language (routing, queuing, circuit breaking, governance, telemetry)?

- **Routing:** filters backends by request path, then orders them using latency, round-robin, or random load balancing.
- **Queuing:** holds accepted work in memory and dispatches lower-numbered priorities first.
- **Circuit breaking:** temporarily skips a host after its recent failure count reaches the configured threshold.
- **Governance:** validates requests and limits how shared worker capacity is allocated.
- **Telemetry:** records request timing, attempts, backend identity, and—when configured—AI token usage.

## Follow One Request

**Every accepted request follows one native proxy pipeline; APIM, when present, is upstream of this flow.**

```text
client or APIM
      │ HTTP request
      ▼
[validate] → [priority queue + TTL] → [worker]
                                           │
                                           ▼
                       [path filter → load-balance order → circuit gate]
                                           │
                           failed attempt ──┴──► next eligible backend
                                           │ success
                                           ▼
                   response headers + body + telemetry event → caller
```

### Where does the proxy sit in the architecture — between what and what?

SimpleL7Proxy sits between clients—or APIM—and backend HTTP endpoints. It is the operator-owned data plane through which governed backend requests pass. It commonly runs inside the operator's network boundary, with private connectivity to Azure AI endpoints where required.

### What does a request look like going in, and what comes back?

The caller sends the same HTTP method, path, headers, and body expected by the backend. On success, the proxy returns the backend response and adds diagnostic headers such as `BackendHost`, `Request-Queue-Duration`, `Request-Process-Duration`, `Total-Latency`, `Attempts`, and `Lifetime-Attempts`.

```bash
curl -i http://proxy:8000/openai/v1/chat/completions
# BackendHost: https://selected-backend.example.com
# Attempts: 2
```

### What Azure services does it depend on (App Insights, Event Hubs, Service Bus, Blob, App Configuration)?

Only a reachable backend is required for a basic synchronous run. Application Insights and Event Hubs are optional telemetry sinks. Azure App Configuration enables live reload of Warm settings. Blob Storage and Service Bus are required only for the corresponding async result and notification flows.

### What is the request flow from ingress to backend response? (priority queue → worker → backend selector → circuit breaker)

The listener validates and enqueues the request, starting its TTL clock. A worker dequeues it and builds the backend candidate order using path filtering and load balancing. Open circuits are skipped. The worker sends the request until it gets a pass-through response, exhausts eligible hosts, or reaches the request TTL.

### What are "workers" and why do they matter?

Workers are concurrent proxy loops that dequeue and process requests. `Workers` defaults to `10` for local testing; additional requests wait in the queue. Raising the value can reduce queue time but increases concurrent backend pressure and resource use.

### What is a "backend host" and how is it different from a URL?

A backend host is a configured endpoint plus its proxy behavior. A `Host1` through `Host9` connection string can define the endpoint, probe path, path filter, authentication, direct mode, and response stream processor.

```bash
export Host1="host=https://api.example.com;probe=/health"
export Host2="host=https://fallback.example.com;probe=/health"
export Port=8000
```

### What is a priority queue and how does it affect which requests go first?

The in-memory queue dispatches lower integer priorities before higher integers; priority `1` precedes priority `2`. Priority changes dispatch order, not which native proxy backend is eligible. The queue is not durable, so queued requests are lost if the process stops.

### What is a circuit breaker and when does it open?

Each host has an independent sliding-window failure counter. The circuit opens when failures within `CBTimeslice` reach `CBErrorThreshold`—defaults are `60` seconds and `50` failures—and the host is skipped. It closes automatically after enough failures age out of the window.

> [!TIP]
> Health polling controls whether a host is in the active pool; the circuit breaker controls whether an active candidate is temporarily skipped. Check both when a backend receives no traffic.

## Worked Example

**The observable result must explain both the routing decision and the caller outcome.**

| Step | Concrete state | Result |
|---|---|---|
| 1. Enqueue | Priority `1`, TTL `60 s` | Request enters ahead of lower-priority queued work. |
| 2. Dispatch | Queue wait `12 ms`; one worker becomes free | The worker builds the eligible host order. |
| 3. First attempt | Backend A returns `429`; `429` is not pass-through | The failure is recorded and the worker advances. |
| 4. Second attempt | Backend B returns `200` in `180 ms` | Backend B's response is returned. |
| 5. Observe | `BackendHost` identifies B; `Attempts: 2` | The caller can verify that native cross-host retry occurred. |

> [!WARNING]
> A `429` is requeued only when the backend supplies the configured requeue signal and delay. Otherwise it is a failed attempt and the worker advances according to the retry mode.

## Decide Whether It Fits

**Choose the proxy when its operator-owned, in-memory execution model matches the workload's reliability boundary.**

### What is the supported deployment target (Azure Container Apps)?

Azure Container Apps is the primary production deployment target. It provides the container runtime, scaling, managed identity, ingress, and VNet integration used by the supplied deployment assets.

### Can it run locally? Can it run in other environments?

It can run locally from source or as a container. Other container platforms can run it when they provide equivalent networking, configuration, identity, and health-probe wiring.

### What network topologies does it support (public, VNet, sovereign)?

The deployment assets support public and VNet-connected Azure topologies, including sovereign-cloud configuration. The selected topology must allow the proxy to reach every backend and optional Azure integration it uses.

### When should I use this vs APIM? vs Azure API Gateway?

Use SimpleL7Proxy for backend execution concerns: priority dispatch, host health, circuit breaking, retry, requeue, and request telemetry. Use APIM or another API gateway for API products, subscriptions, broad caller authentication, transformations, and developer experience. Use both when both boundaries are required.

### What does it NOT do (non-goals)?

- It does not provide managed hosting; operators deploy, scale, and monitor it.
- It does not provide a developer portal, API products, or subscriptions.
- It does not perform model inference or manage model deployments.
- It does not provide a durable queue; queued work is lost on restart.
- It does not share circuit-breaker state across proxy replicas.
- It does not translate HTTP into gRPC, WebSocket, or another protocol.

## Choose the Next Path

| If you want to… | Continue with… |
|---|---|
| Run the smallest working setup | [Get it running](02-get-it-running.md) |
| Review the formal system overview | [Overview](../OVERVIEW.md) |
| Configure hosts, retries, and limits | [Configure backends and settings](03-configure-backends-and-settings.md) |
| Validate an observable behavior | [Try a proof of concept](04-try-a-proof-of-concept.md) |
| Trace classes and source files | [Develop and contribute](06-develop-and-contribute.md) |
| Look up one term | [Glossary](../Glossary.md) |

## You Should Now Be Able To

- [ ] Explain the proxy's identity and boundary in two minutes.
- [ ] Trace a request from validation through telemetry.
- [ ] Distinguish native proxy behavior from upstream APIM policy behavior.
- [ ] Decide whether the proxy fits the workload.
- [ ] Choose one next path without searching the documentation tree.
