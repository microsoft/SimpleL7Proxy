# Development and Testing

Purpose: get a local SimpleL7Proxy instance running quickly, validate core request paths, and diagnose common startup issues without deploying to Azure.

> **TL;DR**
> - **Fastest path:** set only `Port` and `Host1`, then run `dotnet run`.
> - **Second-fastest path:** point the proxy to Azure App Configuration (`AZURE_APPCONFIG_ENDPOINT`) and run.

> Need issue-driven guidance? Start at [TroubleshootTOC.md](TroubleshootTOC.md). For advanced tuning, see [ADVANCED_DEVELOPMENT.md](ADVANCED_DEVELOPMENT.md).

---

## Reference — Essential Settings by Feature

> **Units used in this doc:** time values are in **milliseconds** unless the setting name ends with `Secs`.

### Startup

| Variable | Default | Description |
|----------|---------|-------------|
| `Port` | `80` | Proxy listen port |
| `Host1` / `Host2` | — | Backend URLs (at least one required) |
| `AZURE_APPCONFIG_ENDPOINT` | — | App Configuration endpoint URL |
| `AZURE_APPCONFIG_LABEL` | *(none)* | Label filter (use `dev` for local work) |

---

## Diagnosis Checklist

- Confirm the proxy starts and binds the expected port.
- Confirm at least one backend URL in `Host1..Host9` is reachable before startup.
- Confirm one smoke request succeeds before running load tests.
- If using App Configuration, confirm endpoint and label are set.

---

## Setting Up Locally

> [!NOTE]
> **Prerequisites:** .NET SDK 10.0+, Git. Docker is optional (for containerized testing).

### Fastest path — set only Port + Host1

```bash
export Port=8080
export Host1=http://localhost:3000
dotnet run
```

### Second-fastest path — use Azure App Configuration

For detailed setup, role assignment, and configuration seeding instructions, see [AZURE_APP_CONFIGURATION.md](AZURE_APP_CONFIGURATION.md).

**Quick start:**
```bash
export AZURE_APPCONFIG_ENDPOINT=https://your-appconfig.azconfig.io
export AZURE_APPCONFIG_LABEL=dev
dotnet run
```

---

## Next Steps

You can either use real backends or for a mock backend, see [DUMMY_BACKEND.md](DUMMY_BACKEND.md).

---

## IDE Configuration

Add `.vscode/launch.json` to start the proxy from VS Code with F5:

```json
{
  "version": "0.2.0",
  "configurations": [
    {
      "name": ".NET Core Launch (web)",
      "type": "coreclr",
      "request": "launch",
      "preLaunchTask": "build",
      "program": "${workspaceFolder}/bin/Debug/net10.0/SimpleL7Proxy.dll",
      "args": [],
      "cwd": "${workspaceFolder}",
      "stopAtEntry": false,
      "env": {
        "ASPNETCORE_ENVIRONMENT": "Development",
        "Port": "8080",
        "Host1": "http://localhost:3000",
        "Host2": "http://localhost:5000",
        "LogAllRequestHeaders": "true"
      }
    }
  ]
}
```

---

## Related Documentation

- [CONFIGURATION_SETTINGS.md](CONFIGURATION_SETTINGS.md) — All environment variables and config keys
- [LOAD_BALANCING.md](LOAD_BALANCING.md) — Backend selection and retry settings
- [CIRCUIT_BREAKER.md](CIRCUIT_BREAKER.md) — Health check and failover configuration
- [OBSERVABILITY.md](OBSERVABILITY.md) — Logging, metrics, and tracing
- [CONTAINER_DEPLOYMENT.md](CONTAINER_DEPLOYMENT.md) — Building and deploying Docker images to Azure
