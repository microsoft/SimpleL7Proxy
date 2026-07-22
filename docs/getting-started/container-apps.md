# Run SimpleL7Proxy in Azure Container Apps

Provision and deploy SimpleL7Proxy to Azure Container Apps using the repository deployment workflow.

## TL;DR

- Install Azure CLI and `azd`, then authenticate to Azure.
- Run `.azure/setup.sh`, `azd provision`, and `.azure/deploy.sh`.
- Configure Container Apps ingress to target proxy port `8000`.

| Setting | Value used here | Unit | Reload |
|---------|-----------------|------|--------|
| `Port` | `8000` | TCP port | Startup |
| Ingress target port | `8000` | TCP port | Deployment |
| `Host1` | Backend connection string | N/A | Startup |

## Provision and Deploy

**Use the repository workflow so resource names, identities, registry access, and ingress remain consistent.**

```bash
.azure/setup.sh
azd provision
.azure/deploy.sh
```

For the full workflow and parameters, see [Deploy to Azure Container Apps](../how-to/deploy-container-apps.md) and [`deployment/README.md`](../../deployment/README.md).

> [!WARNING]
> If the app starts but ingress returns an error, confirm that the Container Apps ingress target port matches `Port=8000`.

## Configure the Backend

**Set at least one `Host1` connection string before sending traffic.**

```bash
export Port=8000
export Host1="host=https://api.example.com;probe=/health"
azd deploy
```

> [!TIP]
> Use [Azure App Configuration](../how-to/configure-app-configuration.md) when operators need centralized configuration and warm reloads.

## Next Step

Continue with [Connect a Backend](connect-backend.md), then [Verify the Proxy](verify.md).
