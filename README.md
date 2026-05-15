# SimpleL7Proxy

SimpleL7Proxy is a lightweight Layer 7 proxy for routing and managing LLM traffic across multiple backends.

LLM workloads often require handling retries, throttling, and failover across providers, which can be difficult to reason about and control. This project focuses on making those behaviors predictable and observable, so traffic can be routed reliably under real-world conditions.

It is easy to integrate, while supporting patterns that are commonly needed in high-volume and enterprise environments.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-purple)](https://dotnet.microsoft.com)
[![Platform](https://img.shields.io/badge/platform-Azure%20Container%20Apps-0078D4)](https://learn.microsoft.com/en-us/azure/container-apps/overview)
[![Build](https://img.shields.io/badge/build-passing-brightgreen)](docs/DEVELOPMENT.md)

**TL;DR**
- **Run locally:** `git clone … && dotnet run --project src/SimpleL7Proxy`
- **Deploy to ACA:** `./.azure/setup.sh && azd provision && ./.azure/deploy.sh`
- **Use async mode** for long LLM calls (>60 s); see [AsyncOperation.md](docs/AsyncOperation.md)
- **Full setup steps:** [docs/QUICKSTART.md](docs/QUICKSTART.md)

---

<details>
<summary>Architecture diagram</summary>

![SimpleL7Proxy routes client requests through a priority queue to multiple Azure OpenAI backends, with health checking and circuit breaking on each backend.](docs/arch.png)

*Incoming requests are priority-queued and dispatched to healthy backends; degraded backends are isolated automatically.*

</details>

---

## Key Capabilities

- **Priority queuing** — routes high-priority users ahead of batch traffic.
- **Per-user validation** — blocks callers whose model or header values aren't in their allowlist.
- **Entra App ID gating** — unknown app IDs rejected at the gate; no backend hit.
- **Circuit breaker** — progressive back-off; auto-recovery when backends respond.
- **Async orchestration** — blob + Service Bus hand-off for calls that exceed the sync timeout.
- **Hot-reload config** — allowlists, routing rules, and profiles update without restart.

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

