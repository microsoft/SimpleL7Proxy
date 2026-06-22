# SimpleL7Proxy

**SimpleL7Proxy** is a self-hosted, open-source Layer‑7 proxy for Azure AI workloads. It routes requests to the right backend at the right time while enforcing priority, cost governance, and policy — without requiring changes to client code.

AI backend traffic carries retries, throttle responses, and partial failures that standard load balancers do not handle. SimpleL7Proxy addresses this directly: a priority queue holds requests until a healthy backend is available, circuit breakers isolate failing hosts automatically, and per-request telemetry makes token consumption and latency visible per caller.

The proxy runs as a container in Azure Container Apps and integrates with Azure App Configuration, Application Insights, Event Hubs, Blob Storage, and Service Bus.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-purple)](https://dotnet.microsoft.com)
[![Platform](https://img.shields.io/badge/platform-Azure%20Container%20Apps-0078D4)](https://learn.microsoft.com/en-us/azure/container-apps/overview)
[![Build](https://img.shields.io/badge/build-passing-brightgreen)](docs/DEVELOPMENT.md)

**TL;DR** — Try the POCs first (5 minutes)
- Download the latest release and open the POCs. [Releases](https://github.com/microsoft/SimpleL7Proxy/releases/)
- **Try out the POCs:**
  - **Failover** — throttle the primary and observe automatic routing to the secondary.
  - **Priority Levels** — send mixed traffic and confirm each tier is directed to its designated backend.
  - **Chargeback** — submit requests from multiple callers and confirm per-user token usage appears in the logs.
  - **Governance** — call with an invalid model or App ID and confirm the request is rejected.

If those make sense, then explore the other capabilities [Docs](docs)

---

<details>
<summary>Architecture diagram</summary>

![SimpleL7Proxy routes client requests through a priority queue to multiple Azure OpenAI backends, with health checking and circuit breaking on each backend.](docs/arch.png)

*Incoming requests are priority-queued and dispatched to healthy backends; degraded backends are isolated automatically.*

</details>

---

## What it does

- **Health‑aware routing** — route around slow or failing backends.
- **Cost‑aware decisions** — balance latency and spend per user or tier.
- **Policy & priority enforcement** — per‑user allowlists, model gating, and priority queuing.
- **Per‑caller validation & App gating** — block disallowed headers/models; reject unknown Entra App IDs.
- **Resilience** — circuit breakers, progressive backoff, and observable retry/failover.
- **Async orchestration** — hand off long calls to blob + Service Bus.
- **Hot‑reload config** — update rules and profiles without restarting.
- **Observability & chargeback** — per‑request telemetry and usage logs.


→ **[Full architecture and use-case analysis](docs/OVERVIEW.md)**

---

## Quick Start

Follow the [Quick Start guide](docs/QUICKSTART.md) to get the proxy running in minutes:

- **Azure Container Apps** — `setup.sh → azd provision → deploy.sh` provisions all required Azure resources (ACR, Container Apps environment, managed identity) and deploys the container in one pass.
- **Locally** — two commands: `git clone` + `dotnet run`. The proxy starts on port 8000 and routes to any backend specified in the configuration.

→ **[docs/QUICKSTART.md](docs/QUICKSTART.md)**

**Once running, try these walkthroughs to verify key behaviors using the included LLM simulator:**

- [POC: Failover](docs/POC-Failover-configuration.md) — watch the policy detect a throttled (or slow) primary and route to a healthy secondary in real time
- [POC: Priority Levels](docs/POC-Priority-configuration.md) — confirm that each priority tier is directed to its designated backend pool
- [POC: Chargeback](docs/POC-Chargeback.md) — verify that per-user token consumption is captured in Application Insights and queryable by user, tier, and backend

---

## Documentation

**New here?** Start with [Quick Start](#quick-start) → [Overview](docs/OVERVIEW.md) → [Advanced Configuration](docs/ADVANCED_CONFIGURATION.md).

### Getting Started

| Topic | Document |
|-------|----------|
| **Quick Start** | [docs/QUICKSTART.md](docs/QUICKSTART.md) |
| Overview & Architecture | [docs/OVERVIEW.md](docs/OVERVIEW.md) |
| Getting Started — Local Development | [docs/BEGINNER_DEVELOPMENT.md](docs/BEGINNER_DEVELOPMENT.md) |
| Container Deployment | [docs/CONTAINER_DEPLOYMENT.md](docs/CONTAINER_DEPLOYMENT.md) |
| Mock Backends for Testing | [docs/DUMMY_BACKEND.md](docs/DUMMY_BACKEND.md) |
| POC: Failover | [docs/POC-Failover-configuration.md](docs/POC-Failover-configuration.md) |
| POC: Priority Levels | [docs/POC-Priority-configuration.md](docs/POC-Priority-configuration.md) |
| POC: Chargeback | [docs/POC-Chargeback.md](docs/POC-Chargeback.md) |

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


---

## Contributing

Issues and pull requests are welcome. **Open an issue first** to discuss significant changes before submitting a PR.

## License

MIT — see [LICENSE](LICENSE). Copyright (c) Microsoft Corporation.

