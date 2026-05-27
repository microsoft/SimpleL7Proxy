# Quick Start

> [!IMPORTANT]
> Current deployment scripts are Docker-based. `deploy.sh` and `deploy.ps1` build and push images using local Docker.
> If Docker is unavailable, use the remote ACR build workflow in [CONTAINER_DEPLOYMENT.md](CONTAINER_DEPLOYMENT.md), then deploy using the resulting image tags.

## Deploy to Azure Container Apps

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker](https://docs.docker.com/get-docker/) (optional; only needed for local container builds)
- [Azure Developer CLI (azd)](https://learn.microsoft.com/en-us/azure/developer/azure-developer-cli/install-azd)
- [Azure CLI](https://docs.microsoft.com/en-us/cli/azure/install-azure-cli)
- Azure subscription with Container Apps enabled

### Windows

```powershell
.\.azure\setup.ps1
azd provision
.\.azure\deploy.ps1
```

### Linux / macOS

```bash
chmod +x .azure/setup.sh .azure/deploy.sh
./.azure/setup.sh && azd provision && ./.azure/deploy.sh
```

The setup script will prompt for a deployment scenario:

| Scenario | Description |
|----------|-------------|
| `local-proxy-public-apim` | Proxy runs locally; backends on public APIM |
| `aca-proxy-public-apim` | Proxy deployed as ACA; backends on public APIM |
| `vnet-proxy-deployment` | Proxy inside a VNet |

→ For detailed ACA deployment steps and options, see [CONTAINER_DEPLOYMENT.md](CONTAINER_DEPLOYMENT.md).

---

## Run Locally (2 commands)

```bash
git clone https://github.com/your-org/SimpleL7Proxy.git
dotnet run --project src/SimpleL7Proxy
```

The proxy starts on port 8000. Set at least one backend via `Host1` before sending traffic:

```bash
export Host1=host=https://api.example.com;probe=/health
dotnet run --project src/SimpleL7Proxy
```

→ Need a mock backend to test against? See [DUMMY_BACKEND.md](DUMMY_BACKEND.md).

---

## Local Development Paths

### Fastest: Port + Backend Only

```bash
export Port=8080
export Host1=http://localhost:3000
dotnet run --project src/SimpleL7Proxy
```

### Using Azure App Configuration

```bash
export AZURE_APPCONFIG_ENDPOINT=https://your-appconfig.azconfig.io
export AZURE_APPCONFIG_LABEL=dev
dotnet run --project src/SimpleL7Proxy
```

→ For a full walkthrough of local setup options, see [BEGINNER_DEVELOPMENT.md](BEGINNER_DEVELOPMENT.md).  
→ Need help diagnosing issues? See [TroubleshootTOC.md](TroubleshootTOC.md).

