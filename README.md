# SimpleL7Proxy

> Self-hosted Layer-7 AI gateway for Azure — priority queuing, async orchestration, and per-user governance inside your own VNET.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-purple)](https://dotnet.microsoft.com)
[![Platform](https://img.shields.io/badge/platform-Azure%20Container%20Apps-0078D4)](https://learn.microsoft.com/en-us/azure/container-apps/overview)
[![Build](https://img.shields.io/badge/build-passing-brightgreen)](docs/DEVELOPMENT.md)

**TL;DR**
- **Run locally:** `git clone … && dotnet run --project src/SimpleL7Proxy`
- **Deploy to ACA:** `./.azure/setup.sh && azd provision && ./.azure/deploy.sh`
- **Use async mode** for long LLM calls (>60 s); see [AsyncOperation.md](docs/AsyncOperation.md)

---

![SimpleL7Proxy routes client requests through a priority queue to multiple Azure OpenAI backends, with health checking and circuit breaking on each backend.](docs/arch.png)

*Incoming requests are priority-queued and dispatched to healthy backends; degraded backends are isolated automatically.*

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

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker](https://docs.docker.com/get-docker/) (container builds)
- [Azure Developer CLI (azd)](https://learn.microsoft.com/en-us/azure/developer/azure-developer-cli/install-azd) (cloud deployment)
- Azure subscription with Container Apps; optionally AI Foundry / APIM

## Quick Start

**Local (2 commands):**
```bash
git clone https://github.com/your-org/SimpleL7Proxy.git
dotnet run --project src/SimpleL7Proxy
```

**Azure Container Apps — Windows:**
```powershell
.\.azure\setup.ps1
azd provision
.\.azure\deploy.ps1
```

**Azure Container Apps — Linux / macOS:**
```bash
chmod +x .azure/setup.sh .azure/deploy.sh
./.azure/setup.sh && azd provision && ./.azure/deploy.sh
```

> See [Getting Started — Local Development](docs/BEGINNER_DEVELOPMENT.md) for the fastest setup paths.  
> See [Container Deployment](docs/CONTAINER_DEPLOYMENT.md) for VNET and high-performance variants.

---

## Local Development Paths

**Fastest: Port + Backend Only**
```bash
export Port=8080
export Host1=http://localhost:3000
dotnet run
```

**Second-fastest: Azure App Configuration**
```bash
export AZURE_APPCONFIG_ENDPOINT=https://your-appconfig.azconfig.io
export AZURE_APPCONFIG_LABEL=dev
dotnet run
```

→ **Need mock backends?** See [DUMMY_BACKEND.md](docs/DUMMY_BACKEND.md) for null server and Python HTTP server setups.  
→ **Need help diagnosing?** See [TroubleshootTOC.md](docs/TroubleshootTOC.md) for issue-driven guidance.

---

## Documentation

**New here?** Start with [Quick Start](#quick-start) → [Overview](docs/OVERVIEW.md) → [Advanced Configuration](docs/ADVANCED_CONFIGURATION.md).

| Topic | Document |
|-------|----------|
| Overview & Architecture | [docs/OVERVIEW.md](docs/OVERVIEW.md) |
| Backend Host Configuration | [docs/BACKEND_HOSTS.md](docs/BACKEND_HOSTS.md) |
| Load Balancing | [docs/LOAD_BALANCING.md](docs/LOAD_BALANCING.md) |
| Priority Queuing & User Governance | [docs/ADVANCED_CONFIGURATION.md](docs/ADVANCED_CONFIGURATION.md) |
| Circuit Breaker | [docs/CIRCUIT_BREAKER.md](docs/CIRCUIT_BREAKER.md) |
| Health Checking | [docs/HEALTH_CHECKING.md](docs/HEALTH_CHECKING.md) |
| Async Operations | [docs/AsyncOperation.md](docs/AsyncOperation.md) |
| User Profiles | [docs/USER_PROFILES.md](docs/USER_PROFILES.md) |
| Request Validation | [docs/REQUEST_VALIDATION.md](docs/REQUEST_VALIDATION.md) |
| Observability & Telemetry | [docs/OBSERVABILITY.md](docs/OBSERVABILITY.md) |
| Security | [docs/SECURITY.md](docs/SECURITY.md) |
| Configuration Settings | [docs/CONFIGURATION_SETTINGS.md](docs/CONFIGURATION_SETTINGS.md) |
| Azure App Configuration | [docs/AZURE_APP_CONFIGURATION.md](docs/AZURE_APP_CONFIGURATION.md) |
| Environment Variables | [docs/ENVIRONMENT_VARIABLES.md](docs/ENVIRONMENT_VARIABLES.md) |
| AI Foundry Integration | [docs/AI_FOUNDRY_INTEGRATION.md](docs/AI_FOUNDRY_INTEGRATION.md) |
| APIM Policy | [APIM-Policy/readme.md](APIM-Policy/readme.md) |
| Container Deployment | [docs/CONTAINER_DEPLOYMENT.md](docs/CONTAINER_DEPLOYMENT.md) |
| Getting Started — Local Development | [docs/BEGINNER_DEVELOPMENT.md](docs/BEGINNER_DEVELOPMENT.md) |
| Advanced Development & Tuning | [docs/ADVANCED_DEVELOPMENT.md](docs/ADVANCED_DEVELOPMENT.md) |
| Mock Backends for Testing | [docs/DUMMY_BACKEND.md](docs/DUMMY_BACKEND.md) |
| Response Codes | [docs/RESPONSE_CODES.md](docs/RESPONSE_CODES.md) |
| Troubleshooting (Quick Diagnosis TOC) | [docs/TroubleshootTOC.md](docs/TroubleshootTOC.md) |

---

## Contributing

Issues and pull requests are welcome. **Open an issue first** to discuss significant changes before submitting a PR.

## License

MIT — see [LICENSE](LICENSE). Copyright (c) Microsoft Corporation.

