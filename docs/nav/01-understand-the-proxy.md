# Content Brief: 🔍 Understand the Proxy

> **Purpose:** Serve as the orientation entry point for architects, evaluators, and anyone new to the project. This section must answer every "what is this and should I care" question before a reader ever tries to deploy or configure anything.

---

## Quick Answers

<table>
<tr>
<td width="33%" valign="top">

### What problem does it solve?
Standard load balancers can't handle AI throttling, fairness, or token telemetry. SimpleL7Proxy sits between clients and Azure AI backends to add priority queuing, circuit breaking, and per-request governance.

[→ Overview Summary](../OVERVIEW.md#overview-summary)

</td>
<td width="33%" valign="top">

### What does "Layer 7" mean here?
The proxy inspects and acts on HTTP content — headers, paths, response bodies — not just TCP connections. This lets it read throttle signals, extract token counts, and route by request context.

[→ Core Concepts](../OVERVIEW.md#core-concepts)

</td>
<td width="33%" valign="top">

### Where does it sit architecturally?
It runs between APIM (or clients) and Azure AI backends, inside the operator's VNet. Clients never talk directly to a backend — the proxy is the single data-plane entry point.

[→ High-Level Architecture](../OVERVIEW.md#high-level-architecture)

</td>
</tr>
<tr>
<td width="33%" valign="top">

### What happens to a request end to end?
Validate → priority queue → worker picks up → backend selection (path → load balance → circuit gate) → forward → inject headers → telemetry event. Total budget controlled by TTL.

[→ Synchronous Request Flow](../OVERVIEW.md#high-level-workflows)

</td>
<td width="33%" valign="top">

### Where does it run in Azure?
Azure Container Apps is the primary deployment target, with optional VNet integration. It can also run locally from source for development. Sovereign cloud is supported.

[→ Deployment Architecture](../TABLE_OF_CONTENTS.md#deployment-architecture)

</td>
<td width="33%" valign="top">

### What does it NOT do?
No managed hosting, no gRPC/WebSocket, no model inference, no full API gateway features (portals, subscriptions), no distributed circuit breaker state, no durable queue.

[→ Non-Goals](../OVERVIEW.md#non-goals)

</td>
</tr>
</table>

> **⚠️ GAP — Comparison table missing:** There is no "use this vs APIM vs Azure Front Door" decision guide in any existing document. A reader evaluating alternatives has no single place to compare. → [Content gap details](#content-gaps-to-fill)

---

## Reader Profile

| | |
|---|---|
| **Who** | Architects, technical leads, evaluators, senior engineers onboarding to a new team |
| **Why they come here** | They need to understand what the proxy does, why it exists, and whether it fits their use case — before committing to a deployment |
| **When they read this** | First contact with the project; architecture review; onboarding a new team member |

---

## Questions this section MUST answer

### What is it?
- [ ] What problem does SimpleL7Proxy solve that a standard Layer-4 load balancer cannot?
  **Answer:** A Layer-4 load balancer routes traffic based only on IP addresses and TCP ports — it cannot see inside the HTTP request. This means it cannot detect a `429 Too Many Requests` throttle signal, choose a backend based on the request URL path, or extract token counts from a streaming AI response. SimpleL7Proxy operates at the HTTP level (see next question), so it can add priority queuing, [circuit breaking](../Glossary.md#reliability), and per-request telemetry that a Layer-4 balancer simply cannot provide.
- [ ] What is a Layer 7 proxy and why does that distinction matter for AI workloads?
  **Answer:** In networking, traffic moves through layers — Layer 4 handles raw TCP (source IP, destination IP, port) while Layer 7 is the application protocol, which for us means HTTP. A Layer 7 proxy reads request paths, inspects headers, and parses response bodies; a Layer-4 router can only forward packets. For AI workloads, this matters: the proxy must detect throttle codes like `429`, route `/openai/` and `/embeddings/` to different backends, and extract [token telemetry](../Glossary.md#observability) from streaming responses — none of which are possible at Layer 4.
- [ ] What are the core capabilities in plain language (routing, queuing, circuit breaking, governance, telemetry)?
  **Answer:** In plain terms: routing picks a healthy backend and balances load across several; queuing holds incoming requests in a [priority queue](../Glossary.md#request-lifecycle) so critical work is processed first when workers are busy; [circuit breaking](../Glossary.md#reliability) stops sending requests to a failing backend and resumes automatically when it recovers; governance controls which callers can use the proxy and how much capacity each is allowed; telemetry records per-request timing and AI token counts for observability and chargeback.

### What does it look like from the outside?
- [ ] Where does the proxy sit in the architecture — between what and what?
  **Answer:** It sits between clients (or Azure API Management, which handles developer-facing gateway concerns like subscriptions and authentication) on the ingress side and Azure AI backend endpoints on the egress side. Clients never call backends directly — the proxy is the single data-plane entry point.
- [ ] What does a request look like going in, and what comes back?
  **Answer:** A caller sends a standard HTTP request — the same format they would send to the backend directly. The proxy returns the backend's response unchanged but adds diagnostic headers: `BackendHost` (which backend was used), `x-Request-Worker` (which worker handled it), `x-Request-Queue-Duration` (milliseconds the request waited in the queue before a worker picked it up), `x-Request-Process-Duration` (milliseconds the backend took to respond), and `Total-Latency` (the end-to-end round-trip). These headers are primarily for debugging and operations monitoring.
- [ ] What Azure services does it depend on (App Insights, Event Hubs, Service Bus, Blob, App Configuration)?
  **Answer:** The core proxy needs only a backend to call — no Azure services are required to get started. Optional integrations: Application Insights and Event Hubs add telemetry sinks for observability; Service Bus and Blob Storage are needed only for async mode (where the proxy detaches long-running requests from the HTTP connection and writes results to a blob — see [→ Async Mode](../Glossary.md#async-mode)); App Configuration enables [Warm setting](../Glossary.md#configuration-management) hot-reload, which lets you change certain settings without restarting the container.

### How does it work inside?
- [ ] What is the request flow from ingress to backend response? (priority queue → worker → backend selector → circuit breaker)
  **Answer:** The request is validated at ingress, placed in the [priority queue](../Glossary.md#request-lifecycle), and picked up by the next available worker. The worker runs the backend selection pipeline: the [path filter](../Glossary.md#backend-management) narrows candidates by URL prefix, load balancing sets their order, and the [circuit breaker](../Glossary.md#reliability) skips any host with too many recent failures. The proxy forwards the request to the first eligible backend, returns the response to the caller, and emits a telemetry event.
- [ ] What are "workers" and why do they matter?
  **Answer:** Workers are the processes that pick requests from the [priority queue](../Glossary.md#request-lifecycle) and forward them to backends. The proxy runs exactly `Workers` requests at the same time (default 10); any additional requests wait in the queue. More workers mean lower queue wait times but also higher memory and CPU consumption. See [→ Workers](../Glossary.md#request-lifecycle).
- [ ] What is a "backend host" and how is it different from a URL?
  **Answer:** A backend host is a configuration object for one backend service. Unlike a bare URL, it also carries: the health-check path the proxy polls to confirm the backend is alive (`probe=`), the authentication method (`usemi=true` for Managed Identity or `api-key=` for key-based auth), an optional URL path prefix for routing (`path=`), and optionally a stream processor for extracting AI token counts from responses. Each backend is configured as `Host1`, `Host2`, etc., using a semicolon-delimited connection string. See [→ Connection String Format](../Glossary.md#backend-management).
- [ ] What is a priority queue and how does it affect which requests go first?
  **Answer:** The [priority queue](../Glossary.md#request-lifecycle) holds requests until a worker is free. Each request is assigned a [priority level](../Glossary.md#request-lifecycle): lower integers are dispatched first (priority 1 runs before priority 2). Without priority configuration, all requests share the default priority and are served in arrival order. Because the queue is in-memory, it does not survive a proxy restart — requests waiting when the container stops are lost.
- [ ] What is a circuit breaker and when does it open?
  **Answer:** The [circuit breaker](../Glossary.md#reliability) is a safety mechanism (the name comes from electrical circuit breakers that trip to prevent overload damage) that stops the proxy from sending requests to a backend that is clearly failing. When a backend's failure count exceeds `CBErrorThreshold` (default 50) within the last `CBTimeslice` seconds (default 60), the circuit opens and that host is skipped. It closes automatically once old failures age out of the window — no manual reset is needed. See [→ Auto-Recovery](../Glossary.md#reliability).

### Where does it run?
- [ ] What is the supported deployment target (Azure Container Apps)?
  **Answer:** Azure Container Apps is the documented primary deployment target for production use.
- [ ] Can it run locally? Can it run in other environments?
  **Answer:** Yes, it can run locally from source or as a container, and other environments are possible if they can run the container and provide the required network and config wiring.
- [ ] What network topologies does it support (public, VNet, sovereign)?
  **Answer:** The docs describe public ingress, private or VNet-connected deployments, and sovereign cloud support.

### How does it compare?
- [ ] When should I use this vs APIM? vs Azure API Gateway?
  **Answer:** Use SimpleL7Proxy when you need AI-specific reliability features: priority queuing, circuit breaking, retry across backends, and per-request token telemetry. Use Azure API Management (APIM) when you need API lifecycle management — developer portals, subscription management, caller authentication, and complex policy transformations. The two are complementary: APIM can sit in front of SimpleL7Proxy, handling caller concerns while SimpleL7Proxy manages backend reliability.
- [ ] What does it NOT do (non-goals)?
  **Answer:** SimpleL7Proxy is not a managed service (you host and operate it yourself), not a full API gateway (no developer portal, subscription management, or caller authentication), and not a protocol translator (HTTP only — no gRPC or WebSocket). Its [priority queue](../Glossary.md#request-lifecycle) is in-memory and does not survive container restarts. Circuit breaker state is local to each container instance — two proxy replicas do not share failure counters, so a backend that trips one instance's circuit may still receive traffic from another.

---

## What the reader can do AFTER reading this

- [ ] Explain the proxy to a colleague in 2 minutes
- [ ] Draw the architecture (client → queue → worker → backend → telemetry)
- [ ] Decide whether this is the right tool for their scenario
- [ ] Know which document to go to next (QUICKSTART, OVERVIEW, or SCENARIOS)

---

## Existing documents that cover this area

| Document | What it covers | Gap? |
|----------|----------------|------|
| [OVERVIEW.md](../OVERVIEW.md) | Architecture, components, high-level flows | Covers most questions — verify completeness |
| [README.md](../../README.md) | First-contact summary | May duplicate OVERVIEW — check for overlap |
| [design.md](../design.md) | Code-level request flow | Too deep for this audience — link from developer section only |
| [Glossary.md](../Glossary.md) | Term definitions | Should be linked early in this section |

---

## Content gaps to fill

- [ ] A single annotated architecture diagram (one diagram, not many) covering the full pipeline: client → queue → worker → backend selector → circuit breaker → backend → telemetry
- [ ] A "not this, but that" table comparing the proxy to common alternatives (APIM, nginx, Azure Front Door)
- [ ] A "non-goals" list so readers know what to stop looking for
- [ ] A one-paragraph plain-English answer to "what problem does this solve?"
