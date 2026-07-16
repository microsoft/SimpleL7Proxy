# Why Would You Put a Proxy in Front of Your AI Backends?

Depending on whether you are serving live users, high-priority business workflows, or low-priority background jobs, you will likely want control over when, where, and how your traffic is fulfilled. AI backends can throttle, regions can become constrained, and models eventually reach the end of their lifecycle. These are some of the reasons teams place a proxy in front of their AI services. The questions below are the ones most teams ask when deciding whether this approach fits their architecture.

---

## Quick Answers

<table>
<tr>
<td width="33%" valign="top">

### What problem does it solve?
When an AI backend throttles or goes down, your users get errors. SimpleL7Proxy catches those failures before they reach callers — retrying transparently, queuing by priority, and keeping your application responsive even when backends struggle.

[→ What problem does it solve?](#what-problem-does-it-solve-1)

</td>
<td width="33%" valign="top">

### What does "Layer 7" mean here?
A standard balancer moves packets. This proxy reads the conversation — throttle codes, token counts, request paths. That difference is what lets it catch a `429` and retry silently on another backend instead of passing the error to your users.

[→ What does "Layer 7" mean here?](#what-does-layer-7-mean-here-1)

</td>
<td width="33%" valign="top">

### Where does it sit architecturally?
It runs between APIM (or clients) and Azure AI backends, inside the operator's VNet. Clients never talk directly to a backend — the proxy is the single point all requests go through.

[→ Where does it sit architecturally?](#where-does-it-sit-architecturally-1)

</td>
</tr>
<tr>
<td width="33%" valign="top">

### What happens to a request end to end?
Your request waits in a queue, a worker picks it up, and the proxy finds a healthy backend to forward it to. If that backend fails, it tries the next one automatically. You get the response plus a few headers showing which backend was used and how long everything took. The whole thing is capped by a total time budget — see [TTL](../Glossary.md#request-lifecycle).

[→ What happens to a request end to end?](#what-happens-to-a-request-end-to-end-1)

</td>
<td width="33%" valign="top">

### Where does it run in Azure?
Azure Container Apps is the primary deployment target, with optional VNet integration. It can also run locally from source for development. Sovereign cloud is supported.

[→ Where does it run in Azure?](#where-does-it-run-in-azure-1)

</td>
<td width="33%" valign="top">

### What does it NOT do?
It doesn't manage your AI models, run a developer portal, or handle caller subscriptions and authentication — that's what APIM is for. 

[→ What does it NOT do?](#what-does-it-not-do-1)

</td>
</tr>
</table>

---

## Full Answers

### What problem does it solve?

#### What problem does SimpleL7Proxy solve that a standard Layer-4 load balancer cannot?

A standard load balancer moves traffic by IP and port — it has no idea what's inside the HTTP messages. This proxy reads those messages: it can catch a `429` throttle response, route by URL path, and pull token counts from streaming AI responses. None of that is possible without reading HTTP.

**Example:** An Azure OpenAI endpoint returns `429` when rate-limited. A Layer-4 balancer passes the `429` back unchanged; SimpleL7Proxy detects it, retries on a different backend, and — if configured — requeues the request transparently so the caller receives `200 OK` instead.

#### What is a Layer 7 proxy and why does that distinction matter for AI workloads?

> See [What does "Layer 7" mean here?](#what-does-layer-7-mean-here-1) below.

#### What are the core capabilities in plain language (routing, queuing, circuit breaking, governance, telemetry)?

Five things it does: **routing** picks a healthy backend and distributes load so no single backend gets overwhelmed; **queuing** holds incoming requests so your most critical work goes first even when the system is busy; **[circuit breaking](../Glossary.md#reliability)** automatically stops sending traffic to a failing backend — and brings it back once it recovers, with no manual reset; **[governance](../Glossary.md#request-governance)** controls which callers can use the proxy and how much capacity any single caller is allowed to consume; and **telemetry** records per-request timing and AI token counts so you can track latency, costs, and diagnose problems. See the [Glossary](../Glossary.md) for any unfamiliar terms.

---

### What does "Layer 7" mean here?

#### What is a Layer 7 proxy and why does that distinction matter for AI workloads?

The practical difference: a standard router only sees IP addresses and TCP ports — it can't read what's inside the HTTP messages. This proxy can, which is how it catches `429` throttle responses before your users see them, routes `/openai/` and `/embeddings/` to different backends, and extracts [token counts](../Glossary.md#observability) from streaming responses for cost tracking. ("Layer 7" is the technical name for the HTTP layer in the network stack.)

---

### Where does it sit architecturally?

![SimpleL7Proxy architecture overview](../arch.png)

#### Where does the proxy sit in the architecture — between what and what?

SimpleL7Proxy sits between clients (or Azure API Management, which handles developer-facing gateway concerns like subscriptions and authentication) on the ingress side and Azure AI backend endpoints on the egress side. Clients never call backends directly — the proxy is the single data-plane entry point.

#### What does a request look like going in, and what comes back?

SimpleL7Proxy accepts a standard HTTP request — the same format a caller would send to the backend directly — and returns the backend's response unchanged, adding diagnostic headers: `BackendHost` (which backend was used), `x-Request-Worker` (which worker handled it), `x-Request-Queue-Duration` (milliseconds the request waited in the queue), `x-Request-Process-Duration` (milliseconds the backend took to respond), and `Total-Latency` (the end-to-end round-trip). These headers are primarily for debugging and operations monitoring.

**Example:** `curl -i http://proxy:8000/openai/deployments/gpt-4/chat/completions` → response includes `BackendHost: https://my-aoai.openai.azure.com` and `x-Request-Queue-Duration: 12`.

#### What Azure services does it depend on (App Insights, Event Hubs, Service Bus, Blob, App Configuration)?

SimpleL7Proxy needs only a backend to call — no Azure services are required to get started. Optional integrations: Application Insights and Event Hubs add telemetry sinks; Service Bus and Blob Storage are needed only for async mode (where the proxy detaches long-running requests and writes results to a blob — see [→ Async Mode](../Glossary.md#async-mode)); App Configuration enables [Warm setting](../Glossary.md#configuration-management) hot-reload without restarting the container.

---

### What happens to a request end to end?

#### What is the request flow from ingress to backend response? (priority queue → worker → backend selector → circuit breaker)

SimpleL7Proxy validates each incoming request, then places it in the [priority queue](../Glossary.md#request-lifecycle). A worker picks it up, runs the backend selection pipeline — the [path filter](../Glossary.md#backend-management) narrows candidates by URL prefix, load balancing sets their order, and the [circuit breaker](../Glossary.md#reliability) skips any host with too many recent failures — and forwards the request to the first eligible backend. The response goes back to the caller, a few diagnostic headers are added, and a telemetry event is recorded. The whole thing is bounded by the request's time budget (TTL): if it expires at any point — waiting in the queue or during retries — the caller gets a `412`.

```
client → [priority queue] → worker → path filter → load balancer → circuit gate → backend → response + telemetry
```

#### What are "workers" and why do they matter?

SimpleL7Proxy uses a fixed pool of workers (default 10) to pick requests from the [priority queue](../Glossary.md#request-lifecycle) and forward them to backends. The proxy runs exactly `Workers` requests simultaneously — any additional requests wait in the queue. More workers mean lower queue wait times but higher memory and CPU consumption. See [→ Workers](../Glossary.md#request-lifecycle).

#### What is a "backend host" and how is it different from a URL?

SimpleL7Proxy models each backend as a configuration object that carries more than just a URL: the health-check path the proxy polls to confirm the backend is alive (`probe=`), the authentication method (`usemi=true` for Managed Identity or `api-key=` for key-based auth), an optional URL path prefix for routing (`path=`), and optionally a stream processor for extracting AI token counts. Each backend is configured as `Host1`, `Host2`, etc., using a semicolon-delimited connection string. See [→ Connection String Format](../Glossary.md#backend-management).

#### What is a priority queue and how does it affect which requests go first?

SimpleL7Proxy holds requests in an in-memory [priority queue](../Glossary.md#request-lifecycle) until a worker is free. Lower integer priorities are dispatched first — priority 1 runs before priority 2. Without priority configuration, all requests share the default priority and are served in arrival order. Because the queue is in-memory, it does not survive a proxy restart — requests waiting when the container stops are lost.

#### What is a circuit breaker and when does it open?

SimpleL7Proxy uses a [circuit breaker](../Glossary.md#reliability) to stop sending requests to a backend that is clearly failing. When a backend's failure count exceeds `CBErrorThreshold` (default 50) within the last `CBTimeslice` seconds (default 60), the circuit opens and that host is skipped. It closes automatically once old failures age out of the window — no manual reset is needed. See [→ Auto-Recovery](../Glossary.md#reliability).

**Example:** A backend starts returning `500` errors. After 50 failures within 60 seconds, the circuit opens. Traffic automatically shifts to other healthy backends. After 60 seconds with no new failures, the circuit closes and the backend re-enters rotation.

---

### Where does it run in Azure?

#### What is the supported deployment target (Azure Container Apps)?

SimpleL7Proxy is designed for Azure Container Apps as its primary production deployment target. ACA provides the container runtime, VNet integration, managed identity support, and scaling controls the proxy relies on.

#### Can it run locally? Can it run in other environments?

SimpleL7Proxy can run locally from source or as a container, and other container-capable environments work if they can provide the required network and configuration wiring.

#### What network topologies does it support (public, VNet, sovereign)?

SimpleL7Proxy supports public ingress, private or VNet-connected deployments, and sovereign cloud configurations.

---

### What does it NOT do?

#### When should I use this vs APIM? vs Azure API Gateway?

Use it when you need reliability and cost visibility specific to AI backends: priority queuing, circuit breaking, retry across backends, and per-request token telemetry. Use Azure API Management when you need API lifecycle management — developer portals, subscription management, caller authentication, and complex policy transformations. The two are complementary: APIM can sit in front of this proxy, handling caller concerns while the proxy manages backend reliability.

| Need | Use |
|------|-----|
| Priority queue, circuit breaking, retry across backends | SimpleL7Proxy |
| Developer portal, subscriptions, caller auth | Azure API Management |
| Both | APIM in front of SimpleL7Proxy |

#### What does it NOT do (non-goals)?

It is not a managed service (you host and operate it yourself), not a full API gateway (no developer portal, subscription management, or caller authentication), and not a protocol translator (HTTP only — no gRPC or WebSocket). The [priority queue](../Glossary.md#request-lifecycle) is in-memory and does not survive container restarts — requests waiting when the container stops are lost. Circuit breaker state is local to each container instance — two proxy replicas do not share failure counters, so a backend that trips one instance's circuit may still receive traffic from another.

---

## You Should Now Be Able To

- [ ] Explain the proxy to a colleague in 2 minutes
- [ ] Draw the architecture (client → queue → worker → backend → telemetry)
- [ ] Decide whether this is the right tool for their scenario
- [ ] Know which document to go to next (QUICKSTART, OVERVIEW, or SCENARIOS)

---

## Related Documents

| Document | What it covers |
|----------|----------------|
| [Overview](../OVERVIEW.md) | Architecture, components, and high-level flows |
| [Design](../design.md) | Code-level request flow |
| [Glossary](../Glossary.md) | Definitions for proxy concepts and terminology |
| [Get It Running](02-get-it-running.md) | The next discovery path for deploying or running the proxy |

---
