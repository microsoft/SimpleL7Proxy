# Content Brief: 🔍 Understand the Proxy

> **Purpose:** Serve as the orientation entry point for architects, evaluators, and anyone new to the project. This section must answer every "what is this and should I care" question before a reader ever tries to deploy or configure anything.

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
- [ ] What is a Layer 7 proxy and why does that distinction matter for AI workloads?
- [ ] What are the core capabilities in plain language (routing, queuing, circuit breaking, governance, telemetry)?

### What does it look like from the outside?
- [ ] Where does the proxy sit in the architecture — between what and what?
- [ ] What does a request look like going in, and what comes back?
- [ ] What Azure services does it depend on (App Insights, Event Hubs, Service Bus, Blob, App Configuration)?

### How does it work inside?
- [ ] What is the request flow from ingress to backend response? (priority queue → worker → backend selector → circuit breaker)
- [ ] What are "workers" and why do they matter?
- [ ] What is a "backend host" and how is it different from a URL?
- [ ] What is a priority queue and how does it affect which requests go first?
- [ ] What is a circuit breaker and when does it open?

### Where does it run?
- [ ] What is the supported deployment target (Azure Container Apps)?
- [ ] Can it run locally? Can it run in other environments?
- [ ] What network topologies does it support (public, VNet, sovereign)?

### How does it compare?
- [ ] When should I use this vs APIM? vs Azure API Gateway?
- [ ] What does it NOT do (non-goals)?

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
