# SimpleL7Proxy

**SimpleL7Proxy** is a lightweight, practical Layer‑7 proxy that makes routing LLM traffic simple, predictable, and observable. It’s built to do one thing well: route requests to the right model at the right time while keeping cost, health, and policy under control.

LLM traffic is noisy: retries, throttles, and provider quirks make behavior unpredictable. SimpleL7Proxy turns that uncertainty into repeatable outcomes so teams can optimize for latency, cost, and capability without surprises.

It is easy to integrate, while supporting patterns that are commonly needed in high-volume and enterprise environments.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-purple)](https://dotnet.microsoft.com)
[![Platform](https://img.shields.io/badge/platform-Azure%20Container%20Apps-0078D4)](https://learn.microsoft.com/en-us/azure/container-apps/overview)
[![Build](https://img.shields.io/badge/build-passing-brightgreen)](docs/DEVELOPMENT.md)

**TL;DR** -- Try the POCs first ( 5 minutes )
- Download the latest release and open the POCs. [Releases](https://github.com/microsoft/SimpleL7Proxy/releases/)
- **Try out the POCs:**
  - **Failover** — throttle the primary and watch it jump to the secondary.
  - **Priority Levels** — send mixed traffic and see backends targetted requests based on the priorities.
  - **Chargeback** — fire a few users at it and check the usage logs.
  - **Governance** — try calling with the wrong model or App ID and watch it get blocked.

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
- **Locally** — two commands: `git clone` + `dotnet run`. The proxy starts on port 8000 and begins routing to any backend you point it at via `Host1`.

→ **[docs/QUICKSTART.md](docs/QUICKSTART.md)**

**Once running, try these walkthroughs to verify key behaviors using the included LLM simulator:**

- [POC: Failover](docs/POC-Failover-configuration.md) — watch the policy detect a throttled (or slow) primary and route to a healthy secondary in real time
- [POC: Priority Levels](docs/POC-Priority-configuration.md) — confirm that `acceptablePriorities` routes each tier to the right backend
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

### Configuration

| Topic | Document |
|-------|----------|
| Configuration Settings (full reference) | [docs/CONFIGURATION_SETTINGS.md](docs/CONFIGURATION_SETTINGS.md) |
| Environment Variables | [docs/ENVIRONMENT_VARIABLES.md](docs/ENVIRONMENT_VARIABLES.md) |
| Azure App Configuration (hot-reload) | [docs/AZURE_APP_CONFIGURATION.md](docs/AZURE_APP_CONFIGURATION.md) |
| Backend Host Configuration | [docs/BACKEND_HOSTS.md](docs/BACKEND_HOSTS.md) |
| Priority Queuing & User Governance | [docs/ADVANCED_CONFIGURATION.md](docs/ADVANCED_CONFIGURATION.md) |
| User Profiles | [docs/USER_PROFILES.md](docs/USER_PROFILES.md) |

### Core Features

| Topic | Document |
|-------|----------|
| Load Balancing | [docs/LOAD_BALANCING.md](docs/LOAD_BALANCING.md) |
| Circuit Breaker | [docs/CIRCUIT_BREAKER.md](docs/CIRCUIT_BREAKER.md) |
| Health Checking | [docs/HEALTH_CHECKING.md](docs/HEALTH_CHECKING.md) |
| Timeouts | [docs/TIMEOUTS.md](docs/TIMEOUTS.md) |
| Async Operations | [docs/AsyncOperation.md](docs/AsyncOperation.md) |
| Request Validation | [docs/REQUEST_VALIDATION.md](docs/REQUEST_VALIDATION.md) |
| Observability & Telemetry | [docs/OBSERVABILITY.md](docs/OBSERVABILITY.md) |
| Response Codes | [docs/RESPONSE_CODES.md](docs/RESPONSE_CODES.md) |
| Security | [docs/SECURITY.md](docs/SECURITY.md) |

### Integrations & Deployment

| Topic | Document |
|-------|----------|
| AI Foundry Integration | [docs/AI_FOUNDRY_INTEGRATION.md](docs/AI_FOUNDRY_INTEGRATION.md) |
| APIM Policy | [APIM-Policy/readme.md](APIM-Policy/readme.md) |
| Sidecar Deployment | [docs/SIDECAR_DEPLOYMENT.md](docs/SIDECAR_DEPLOYMENT.md) |

### Development

| Topic | Document |
|-------|----------|
| Advanced Development & Tuning | [docs/ADVANCED_DEVELOPMENT.md](docs/ADVANCED_DEVELOPMENT.md) |
| Troubleshooting (Quick Diagnosis TOC) | [docs/TroubleshootTOC.md](docs/TroubleshootTOC.md) |


---

## Contributing

Issues and pull requests are welcome. **Open an issue first** to discuss significant changes before submitting a PR.

## License

MIT — see [LICENSE](LICENSE). Copyright (c) Microsoft Corporation.

