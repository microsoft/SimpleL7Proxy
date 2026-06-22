# SimpleL7Proxy

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-purple)](https://dotnet.microsoft.com)
[![Platform](https://img.shields.io/badge/platform-Azure%20Container%20Apps-0078D4)](https://learn.microsoft.com/en-us/azure/container-apps/overview)
[![Build](https://img.shields.io/badge/build-passing-brightgreen)](docs/DEVELOPMENT.md)

AI backends behave differently from normal HTTP services — they throttle, retry, and partially fail in ways that standard load balancers cannot interpret. When a primary endpoint slows down, when callers compete for limited capacity, or when token spend must be attributed per user, a generic Layer‑4 balancer has no answer. SimpleL7Proxy fills that gap.

Governance and compliance are equally important. Enterprises need to control which callers can access which models, validate Entra App IDs, block disallowed headers, assign priority tiers, and produce auditable per‑request logs for chargeback and compliance. Traditional Layer‑4 balancers have no concept of “model gating,” “per‑caller policy,” or “token‑level attribution.” SimpleL7Proxy adds these controls at the edge, so every request is validated, governed, and fully observable before it reaches an AI backend.

---
## TL;DR — try the POCs first

Download the latest release, then run each walkthrough. [Releases](https://github.com/microsoft/SimpleL7Proxy/releases/)

- Install the proxy first.
- Try out the POC's — each POC is purposeful to illustate one concept.

If those make sense, explore the rest of the [Docs](docs).

---

## Quick Start

Follow the [Quick Start guide](docs/QUICKSTART.md) to get the proxy running.  You can run in one of two scenarios:

- **Azure Container Apps** — Can be deployed to ACA, reachable either public or private VNET.
- **Locally** — Run it locally on port 8000 and route to any backend specified in the configuration.
- 

**Once running, try these walkthroughs to verify key behaviors using the included LLM simulator:**

- [POC: Failover](docs/POC-Failover-configuration.md) — watch the policy detect a throttled (or slow) primary and route to a healthy secondary in real time
- [POC: Priority Levels](docs/POC-Priority-configuration.md) — confirm that each priority tier is directed to its designated backend pool
- [POC: Chargeback](docs/POC-Chargeback.md) — verify that per-user token consumption is captured in Application Insights and queryable by user, tier, and backend
- [More POCs](#more-pocs) — OpenAI failover and the security/OAuth runbooks
---

## How it works

```text
Client → Priority Queue → Worker → Backend Selector → Circuit Breaker → Azure AI
                                              ↓
                                       Telemetry + Chargeback
```

A request enters a **priority queue** and waits there until a **healthy backend** is available. **Circuit breakers** isolate failing hosts automatically, **progressive backoff** smooths retries, and **per-request telemetry** makes token consumption and latency visible per caller. Rules and user profiles **hot‑reload** without a restart.

The proxy runs as a container in Azure Container Apps and integrates with Azure App Configuration, Application Insights, Event Hubs, Blob Storage, and Service Bus.


---

## Capabilities

**Routing & resilience**
- Health‑aware routing around slow or failing backends.
- Circuit breakers, progressive backoff, and observable retry/failover.

**Governance & cost**
- Cost‑aware decisions that balance latency and spend per user or tier.
- Policy & priority enforcement: per‑user allowlists, model gating, and priority queuing.
- Per‑caller validation & App gating: block disallowed headers/models; reject unknown Entra App IDs.

**Operations**
- Async orchestration: hand off long calls to blob + Service Bus.
- Hot‑reload config: update rules and profiles without restarting.
- Observability & chargeback: per‑request telemetry and usage logs.

---

## Architecture at a glance

Incoming requests are priority-queued and dispatched to healthy backends; degraded backends are isolated automatically.

<details>
<summary>Architecture diagram</summary>

![SimpleL7Proxy routes client requests through a priority queue to multiple Azure OpenAI backends, with health checking and circuit breaking on each backend.](docs/arch.png)

</details>

→ **[Full architecture and use-case analysis](docs/OVERVIEW.md)**

---

### More POCs

| POC | What it demonstrates |
|-----|----------------------|
| [OpenAI Failover via APIM](docs/POC-OpenAI-Failover.md) | Retry across PTU + PAYGO backends on `429`; client still sees `200 OK` |
| [Security & OAuth (index)](docs/POC-Security-OAuth-Configuration.md) | Entry point linking the two OAuth 2.0 runbooks below |
| [Secure the Proxy (EasyAuth)](docs/POC-Secure-the-proxy.md) | Protect the ACA proxy from unauthorized access with Container Apps EasyAuth |
| [ACA Proxy Authorization](docs/POC-ACA-Proxy-Security-Authorization.md) | Inbound OAuth 2.0 authentication and caller validation at the ACA proxy |
| [APIM Authorization](docs/POC-APIM-Security-Authorization.md) | OAuth 2.0 auth at APIM for ACA→APIM calls, with `validate-jwt` enforcement |
| [Secure APIM (JWT)](docs/POC-security-the-apim.md) | Secure APIM with Entra JWT validation |

---

## Documentation map

<details>
<summary><strong>Expand</strong></summary>

### Getting Started

| Topic | Document | What it covers |
|-------|----------|----------------|
| **Quick Start** | [docs/QUICKSTART.md](docs/QUICKSTART.md) | Get the proxy running locally or in Azure Container Apps in minutes |
| Overview & Architecture | [docs/OVERVIEW.md](docs/OVERVIEW.md) | Full architecture, request flow, and use-case analysis |
| Getting Started — Local Development | [docs/BEGINNER_DEVELOPMENT.md](docs/BEGINNER_DEVELOPMENT.md) | Build, run, and debug the proxy on your machine |
| Container Deployment | [docs/CONTAINER_DEPLOYMENT.md](docs/CONTAINER_DEPLOYMENT.md) | Package and deploy the proxy as a container |
| Mock Backends for Testing | [docs/DUMMY_BACKEND.md](docs/DUMMY_BACKEND.md) | Use the included LLM simulator to exercise the proxy without real backends |
| POC: Failover | [docs/POC-Failover-configuration.md](docs/POC-Failover-configuration.md) | Throttle the primary and watch traffic route to a healthy secondary |
| POC: Priority Levels | [docs/POC-Priority-configuration.md](docs/POC-Priority-configuration.md) | Confirm each priority tier is directed to its designated backend pool |
| POC: Chargeback | [docs/POC-Chargeback.md](docs/POC-Chargeback.md) | Track and attribute per-user token consumption across a shared deployment |


### Documentation by Domain

For a complete concept-oriented index across all documentation, see the [full Table of Contents](docs/TABLE_OF_CONTENTS.md).

| Domain | What it covers |
|--------|----------------|
| [Request Lifecycle](docs/TABLE_OF_CONTENTS.md#request-lifecycle) | Ingress, priority queue, workers, TTL, response codes |
| [Backend Management](docs/TABLE_OF_CONTENTS.md#backend-management) | Host configuration, health polling, load balancing, path routing |
| [Reliability](docs/TABLE_OF_CONTENTS.md#reliability) | Circuit breaker, retry, requeue, timeout model |
| [Request Governance](docs/TABLE_OF_CONTENTS.md#request-governance) | Validation pipeline, user profiles, priority mapping, throttling |
| [Async Mode](docs/TABLE_OF_CONTENTS.md#async-mode) | Long-running requests, blob storage, Service Bus status events |
| [Observability](docs/TABLE_OF_CONTENTS.md#observability) | Telemetry sinks, token tracking, health endpoints |
| [Configuration Management](docs/TABLE_OF_CONTENTS.md#configuration-management) | Warm/Cold/Hidden settings, App Configuration, env vars |
| [Authentication and Security](docs/TABLE_OF_CONTENTS.md#authentication-and-security) | Managed Identity, keyless auth, App ID validation |
| [Deployment Architecture](docs/TABLE_OF_CONTENTS.md#deployment-architecture) | Container Apps, sidecar deployment, APIM integration |
| [Protocol and Headers](docs/TABLE_OF_CONTENTS.md#protocol-and-headers) | S7P* request headers, injected response headers |
</details>

---

## Contributing

Issues and pull requests are welcome. **Open an issue first** to discuss significant changes before submitting a PR.

## License

MIT — see [LICENSE](LICENSE). Copyright (c) Microsoft Corporation.

