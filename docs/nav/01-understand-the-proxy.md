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
  **Answer:** A Layer-4 balancer can spread connections, but it cannot inspect HTTP paths, headers, retry signals, or streamed token data, so SimpleL7Proxy adds AI-specific routing, fairness, failover, and telemetry.
- [ ] What is a Layer 7 proxy and why does that distinction matter for AI workloads?
  **Answer:** A Layer 7 proxy understands HTTP requests and responses, which matters here because AI traffic needs path-aware routing, header-based policy, retry handling, and token extraction from streamed responses.
- [ ] What are the core capabilities in plain language (routing, queuing, circuit breaking, governance, telemetry)?
  **Answer:** In plain terms, it picks a healthy backend, queues work by priority, stops sending traffic to failing hosts, enforces caller rules, and records per-request timing and token data.

### What does it look like from the outside?
- [ ] Where does the proxy sit in the architecture — between what and what?
  **Answer:** It sits between clients or APIM on the front side and backend AI endpoints on the back side.
- [ ] What does a request look like going in, and what comes back?
  **Answer:** A caller sends a normal HTTP request, and the proxy returns the backend response plus proxy-added headers such as worker, queue, and backend timing details.
- [ ] What Azure services does it depend on (App Insights, Event Hubs, Service Bus, Blob, App Configuration)?
  **Answer:** The core proxy only needs a backend to call, while Application Insights, Event Hubs, Service Bus, Blob Storage, and App Configuration are optional integrations for telemetry, async mode, and hot reload.

### How does it work inside?
- [ ] What is the request flow from ingress to backend response? (priority queue → worker → backend selector → circuit breaker)
  **Answer:** The request is validated, placed in the priority queue, picked up by a worker, matched to candidate backends, filtered by circuit state, forwarded, and then returned with telemetry.
- [ ] What are "workers" and why do they matter?
  **Answer:** Workers are the concurrent request-processing threads that drain the queue, so their count directly affects throughput and queue wait time.
- [ ] What is a "backend host" and how is it different from a URL?
  **Answer:** A backend host is a configured routing target that includes a base URL plus behavior such as probe path, path prefix, auth mode, and stream processor.
- [ ] What is a priority queue and how does it affect which requests go first?
  **Answer:** The priority queue is an in-memory queue where lower-numbered priority requests are dispatched before lower-priority work.
- [ ] What is a circuit breaker and when does it open?
  **Answer:** The circuit breaker is a per-host failure guard that opens when recent failures exceed `CBErrorThreshold` within `CBTimeslice`, causing that host to be skipped until it recovers.

### Where does it run?
- [ ] What is the supported deployment target (Azure Container Apps)?
  **Answer:** Azure Container Apps is the documented primary deployment target for production use.
- [ ] Can it run locally? Can it run in other environments?
  **Answer:** Yes, it can run locally from source or as a container, and other environments are possible if they can run the container and provide the required network and config wiring.
- [ ] What network topologies does it support (public, VNet, sovereign)?
  **Answer:** The docs describe public ingress, private or VNet-connected deployments, and sovereign cloud support.

### How does it compare?
- [ ] When should I use this vs APIM? vs Azure API Gateway?
  **Answer:** Use SimpleL7Proxy when you need backend-aware queuing, circuit breaking, retry, and token telemetry, and use APIM or another gateway for API products, policies, caller auth, and developer-facing gateway features.
- [ ] What does it NOT do (non-goals)?
  **Answer:** It is not a managed service, full API gateway, protocol translator, model runtime, durable queue, or shared cross-instance circuit breaker.

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
