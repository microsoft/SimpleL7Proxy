# Development and Testing

Get SimpleL7Proxy running locally in under five minutes using the automated setup script, or configure it manually with the steps below.

> **TL;DR**
> - **Fastest path:** run `.azure/local-setup.sh` — it generates your environment file interactively.
> - **Minimum config:** set `Host1`, `Port`, and `Workers`; everything else has a working default.
> - **Debugging:** add `LogAllRequestHeaders=true` and `LogAllResponseHeaders=true` to see all headers in the log.

---

## Reference — Key Development Settings

| Variable | Default | Description |
|----------|---------|-------------|
| `Port` | `80` | Proxy listen port |
| `Host1` / `Host2` | — | Backend URLs (at least one required) |
| `Workers` | `10` | Concurrent worker count |
| `Timeout` | `1200000` ms | Per-host request timeout |
| `IgnoreSSLCert` | `false` | Skip TLS verification (dev only) |
| `LogAllRequestHeaders` | `false` | Log every inbound header |
| `LogAllResponseHeaders` | `false` | Log every outbound header |
| `LOGFILE_NAME` | `eventslog.json` | Path for event log output |
| `MaxQueueLength` | `1000` | Max queued requests before 429 |
| `AZURE_APPCONFIG_ENDPOINT` | — | App Configuration endpoint URL |
| `AZURE_APPCONFIG_LABEL` | *(none)* | Label filter (use `dev` for local work) |
| `AZURE_APPCONFIG_REFRESH_SECONDS` | `30` | Sentinel poll interval in seconds |

---

## Setting Up Locally

**Rule: Use the automated script for the fastest, error-free setup; fall back to manual steps only if the script cannot reach your backends.**

```bash
cd .azure
./local-setup.sh    # interactive wizard → generates .env file
```

> [!NOTE]
> **Prerequisites:** .NET SDK 10.0+, Git. Docker is optional (for containerized testing).

> [!TIP]
> **Troubleshooting:** If the script fails with a permission error, run `chmod +x .azure/local-setup.sh` first.

### Manual setup

```bash
export Port=8080
export Host1=http://localhost:3000
dotnet run
```

### Using Azure App Configuration in dev mode

**All settings (Warm and Cold) are loaded from App Configuration at startup. Warm settings are then re-applied every `AZURE_APPCONFIG_REFRESH_SECONDS` seconds via the sentinel key — no restart needed for those changes.**

```bash
export AZURE_APPCONFIG_ENDPOINT=https://nvm2-tc26-appcfg.azconfig.io
export AZURE_APPCONFIG_LABEL=dev
export AZURE_APPCONFIG_REFRESH_SECONDS=30
dotnet run
```

Before running, assign both roles to your developer account on the App Configuration resource:

```bash
APPCONFIG_ID=$(az appconfig show --name nvm2-tc26-appcfg --query id -o tsv)
USER_ID=$(az ad signed-in-user show --query id -o tsv)

az role assignment create --role "App Configuration Data Reader" \
  --assignee $USER_ID --scope $APPCONFIG_ID

az role assignment create --role "App Configuration Data Owner" \
  --assignee $USER_ID --scope $APPCONFIG_ID
```

> [!NOTE]
> **Data Reader** is sufficient if you only read settings. **Data Owner** is required if you also update keys or bump the sentinel from the CLI during development.

> [!TIP]
> **Troubleshooting:** If the proxy fails to connect, run `az login` to refresh your developer credentials — the SDK uses the default Azure credential chain. Role assignments can take a few minutes to propagate.

---

## Running with Mock Backends

**Rule: Use the included null server for the fastest mock backend; it requires only Python and no extra dependencies.**

```bash
# Terminal 1 — start the included mock backend
cd test/nullserver/Python
python streamserver.py

# Terminal 2 — start the proxy pointing at it
export Port=8080
export Host1=http://localhost:3000
dotnet run
```

> [!NOTE]
> `Host1` must be reachable before the proxy starts or the initial health check will mark it as OPEN.

> [!TIP]
> **Troubleshooting:** Run `curl http://localhost:3000/` to confirm the mock backend is up before starting the proxy.

---

## Testing Scenarios

**Rule: Use targeted `curl` commands to exercise priority, TTL, and async paths individually before running load tests.**

```bash
# Priority + TTL override
curl -H "S7PPriorityKey: 12345" -H "S7PTTL: 60" http://localhost:8080/test

# Async mode
curl -H "AsyncMode: true" -H "X-UserID: user1" http://localhost:8080/async-test
```

```bash
# Load test (curl loop)
for i in {1..100}; do curl -s http://localhost:8080/test & done; wait
```

> [!NOTE]
> Async mode also requires `AsyncBlobStorageConnectionString` and `AsyncSBConnectionString` to be set.

> [!WARNING]
> **Error:** A `429` response during load testing means `MaxQueueLength` was reached — increase it or reduce concurrency.

---

## Container Development

**Rule: Build the image locally and inject environment variables at `docker run` time; do not bake secrets into the image.**

```bash
docker build -t proxy-dev -f Dockerfile .

docker run -p 8080:443 \
  -e Host1=http://host.docker.internal:3000 \
  -e Host2=http://host.docker.internal:5000 \
  -e LogAllRequestHeaders=true \
  -e Workers=5 \
  proxy-dev
```

> [!NOTE]
> Use `host.docker.internal` to reach mock backends running on the host from inside the container.

> [!TIP]
> **Troubleshooting:** If the container exits immediately, check logs with `docker logs <container-id>` — a missing `Host1` value is the most common cause.

---

## Worked Example — Full Local Stack

> **Goal:** Proxy on port 8080 with two nginx mock backends, header logging enabled, 10-worker pool.

| Step | Command | Expected result |
|------|---------|----------------|
| Start backend 1 | `python -m http.server 3000` | Listening on :3000 |
| Start backend 2 | `python -m http.server 5000` | Listening on :5000 |
| Export config | `export Port=8080 Host1=http://localhost:3000 Host2=http://localhost:5000 Workers=10 LogAllRequestHeaders=true` | — |
| Start proxy | `dotnet run` | `Listening on port 8080` |
| Smoke test | `curl -v http://localhost:8080/` | `200 OK` from backend 1 or 2 |
| Check failover | Stop backend 1; `curl http://localhost:8080/` | `200 OK` routed to backend 2 |

**Stopping backend 1 while the proxy is running triggers circuit-breaker logic — subsequent requests route automatically to backend 2.**

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
