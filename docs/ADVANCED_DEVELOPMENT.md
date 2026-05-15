# Advanced Development & Tuning

For fine-tuning proxy behavior during local development, optimizing throughput, and testing advanced features.

> **See also:** [BEGINNERDEVELOPMENT.md](BEGINNERDEVELOPMENT.md) for basic local setup. This guide covers performance tuning and feature-specific configuration.

---

## Startup Performance Tuning

Adjust these settings after the proxy is running successfully with default values.

> **Units used in this doc:** time values are in **milliseconds** unless the setting name ends with `Secs`.

| Variable | Default | Description |
|----------|---------|-------------|
| `Workers` | `10` | Concurrent worker count — increase for higher throughput, decrease to reduce resource usage |
| `Timeout` | `1200000` ms (20 min) | Per-host request timeout — lower for faster failure detection, raise for slow backends |
| `MaxQueueLength` | `1000` | Max queued requests before returning 429 — raise if backends are slow, lower to fail fast |
| `PollInterval` | `15000` ms (15 s) | Backend health check frequency — lower for faster circuit breaker recovery, raise to reduce overhead |

### Quick Tuning Guide

**For high throughput (many concurrent requests):**
```bash
export Workers=20
export MaxQueueLength=2000
export PollInterval=10000
```

**For slow backends:**
```bash
export Timeout=3600000  # 1 hour
export MaxQueueLength=5000
export PollInterval=30000  # 30 seconds
```

**For fast failure detection:**
```bash
export Timeout=300000  # 5 minutes
export MaxQueueLength=500
export PollInterval=5000  # 5 seconds
```

---

## Health Probes — Advanced Configuration

Setup internal sidecar health probes for Kubernetes and orchestration platforms.

| Variable | Default | Description |
|----------|---------|-------------|
| `HealthProbeSidecar` | `Enabled=false;url=http://localhost:9000` | Sidecar health probe config — format: `Enabled=true/false;url=http://host:port` |
| `PollTimeout` | `3000` ms | Health probe timeout — increase if network is slow |

### Enabling Sidecar Health Probes

For Kubernetes or Container Apps with sidecar health checks:

```bash
export HealthProbeSidecar="Enabled=true;url=http://localhost:9000"
dotnet run
```

The proxy will expose:
- `/liveness` — is the proxy running?
- `/readiness` — is the proxy ready to accept requests?
- `/startup` — has the proxy completed startup?

Use in your Kubernetes probes (e.g., `livenessProbe.httpGet.path=/liveness`).

---

## User Profiles — Advanced Setup

Multi-tenant configuration with user profile enrichment and access control.

| Variable | Default | Description |
|----------|---------|-------------|
| `UseProfiles` | `false` | Enable user profile enrichment |
| `UserConfigUrl` | `""` | URL for user profile config (file: or http:) |
| `UserProfileHeader` | `X-UserProfile` | Header to inject with profile data |
| `UserIDFieldName` | `userId` | JSON field used as user identifier |

### Configuration File Format

Create a `users.json`:

```json
{
  "user1": {
    "userId": "user1",
    "tier": "premium",
    "quota": 1000
  },
  "user2": {
    "userId": "user2",
    "tier": "standard",
    "quota": 100
  }
}
```

### Setup with Local File

```bash
export UseProfiles=true
export UserConfigUrl="file:users.json"
export UserProfileHeader="X-UserProfile"
dotnet run
```

Now each request with `X-UserID: user1` will be enriched with the profile data.

### Setup with Remote Config

```bash
export UseProfiles=true
export UserConfigUrl="http://localhost:8080/api/users"
export UserProfileHeader="X-UserProfile"
dotnet run
```

The proxy will periodically fetch user configs from the URL.

---

## Event Hub Logging

Stream request/response events to Azure Event Hub for centralized logging and analytics.

| Variable | Default | Description |
|----------|---------|-------------|
| `EVENTHUB_CONNECTIONSTRING` | `""` | Event Hub connection string |
| `EVENTHUB_NAME` | `""` | Event Hub name |
| `EVENTHUB_NAMESPACE` | `""` | Event Hub namespace |
| `EVENT_LOGGERS` | `file` | Comma-separated list of event sinks (file, eventhub) |

### Setup

```bash
export EVENTHUB_CONNECTIONSTRING="Endpoint=sb://my-namespace.servicebus.windows.net/;..."
export EVENTHUB_NAME="proxy-events"
export EVENT_LOGGERS="file,eventhub"
dotnet run
```

> [!NOTE]
> Event Hub is optional. The default is to write events to a local file only (`EVENT_LOGGERS=file`).

---

<details>
<summary>Mock Backends for Load Testing</summary>

Use the included null server to simulate slow/fast backends without actual HTTP calls.

```bash
# Terminal 1 — start the included mock backend
cd test/nullserver/Python
python streamserver.py --delay=100  # 100ms delay per request

# Terminal 2 — start the proxy
export Port=8080
export Host1=http://localhost:3000
export Workers=20
export MaxQueueLength=2000
dotnet run
```

Now send load from a test client and monitor queue depth with the `/debug/queues` endpoint (if available).

</details>

---

<details>
<summary>Debugging Tips</summary>

### Enable all header logging

```bash
export LogAllRequestHeaders=true
export LogAllResponseHeaders=true
dotnet run
```

### Increase log verbosity

```bash
export LOG_LEVEL=Debug
dotnet run
```

### Check queue depth in real-time

```bash
# Terminal 1 — start the proxy
dotnet run

# Terminal 2 — send requests in a loop
for i in {1..100}; do curl http://localhost:8080/api/endpoint & done

# Terminal 3 — check event log
tail -f eventslog.json | jq '.QueueLength'
```

</details>

---

<details>
<summary>Performance Baselines</summary>

These are typical baseline metrics on a 4-core machine with 8GB RAM:

| Setting | Baseline | Notes |
|---------|----------|-------|
| Max concurrent requests | ~500 | With `Workers=10` and healthy backend |
| Throughput | ~1000 req/s | Depends on backend latency |
| Queue depth (typical) | <10 | With balanced load |
| Health check overhead | <1% | With `PollInterval=15000` ms |

Adjust `Workers` and `PollInterval` based on your workload profiling.

</details>
