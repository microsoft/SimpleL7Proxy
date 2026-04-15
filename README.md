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

> See [Development & Testing](docs/DEVELOPMENT.md) for local mock backends.  
> See [Container Deployment](docs/CONTAINER_DEPLOYMENT.md) for VNET and high-performance variants.

---

## Key Defaults

| Setting | Default | Unit | Config key | Reload |
|---|---|---|---|---|
| Port | 80 | — | `Server:Port` | Cold |
| Workers | 10 | threads | `Server:Workers` | Cold |
| Max queue depth | 1 000 | requests | `Server:MaxQueueLength` | Cold |
| Default priority | 2 | — | `server:DefaultPriority` | Warm |
| Sync timeout | 20 min | ms | `server:DefaultTimeout` | Warm |
| Request TTL | 300 | s | `server:DefaultTTLSecs` | Warm |
| Async result TTL | 24 h | s | `Async:TTLSecs` | Warm |
| Async trigger timeout | 10 s | ms | `Async:TriggerTimeout` | Warm |

**Units used:** timeout values are in **milliseconds** in config; TTL values are in **seconds**.

---

## Worked Example — Timeout vs TTL

A request arrives at `t = 0` with no override headers.

| Step | Value | Source |
|---|---|---|
| `DefaultTTLSecs` | 300 s | proxy config |
| Request expires at | `t + 300 s` | set on enqueue |
| `Timeout` | 20 min (1 200 000 ms) | proxy config |
| Backend call deadline | `t + 20 min` | set on dequeue |
| Effective deadline | **t + 5 min** | TTL wins (shorter) |

A client can override both per-request via `S7PTimeout` (ms) and `S7PTTL` (s) headers. **If the TTL expires before the request is dequeued, the proxy returns 412.**

---

## Common Errors

| Code | Meaning | Fix |
|---|---|---|
| 400 | `InvalidTTL` — `S7PTTL` header value is not a valid integer | Send a numeric TTL, e.g. `S7PTTL: 120` |
| 403 | Unknown App ID or missing user profile | Add the Entra GUID to `auth.json`; verify `ValidateAuthAppIDUrl` is reachable |
| 412 | Request TTL expired before dequeue | Increase `DefaultTTLSecs` or reduce queue depth |
| 417 | Required header missing or value not in allowlist | Check `RequiredHeaders` and per-user `ValidateHeaders` rules |
| 429 | Queue full, circuit breaker open, or no active backends | Scale workers, check circuit breaker, or add backends |
| 503 | All backends failed | Check backend health; review circuit breaker timeslice |

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
| Development & Testing | [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) |
| Response Codes | [docs/RESPONSE_CODES.md](docs/RESPONSE_CODES.md) |

---

## Contributing

Issues and pull requests are welcome. **Open an issue first** to discuss significant changes before submitting a PR.

## License

MIT — see [LICENSE](LICENSE). Copyright (c) Microsoft Corporation.

