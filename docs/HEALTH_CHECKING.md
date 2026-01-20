# Health Checking & Reliability

SimpleL7Proxy exposes standard Kubernetes-compatible health endpoints to monitor the application's lifecycle. These endpoints allow orchestrators (like Kubernetes or Azure Container Apps) to know when the application is alive, ready to accept traffic, or has finished starting up.

## Health Endpoints

By default, the proxy serves these endpoints on the main application port (e.g., `80` or `8080`).

| Endpoint | Purpose | Logic | Returns |
|----------|---------|-------|---------|
| `/liveness` | Checks if the application is alive. | Returns **200 OK** if the process is running. | `200 OK` |
| `/readiness` | Checks if the application can accept traffic. | Returns **200 OK** if at least one backend host is healthy. | `200 OK` / `503 Service Unavailable` |
| `/startup` | Checks if initialization is complete. | Returns **200 OK** if the backend poller has completed its first pass. | `200 OK` / `503 Service Unavailable` |
| `/health` | Generic health check. | Alias for liveness. **Note:** Only available on the main proxy port (not the sidecar). | `200 OK` |

## Response Codes

- **200 OK**: Healthy. Body: `OK`.
- **503 Service Unavailable**: Unhealthy. Body includes details such as `Not Healthy. Active Hosts: 0`.

---

## Reliability Modes

You can deploy monitoring in two modes depending on your performance requirements:

### 1. Standard Mode (Default)
In this mode, health probes are served by the main HTTP listener. This is sufficient for most workloads and is the simplest to configure.

**Pros:** No extra configuration.
**Cons:** If the proxy is extremely busy (e.g., 100% CPU or full ThreadPool), it may be slow to answer probes, potentially causing Kubernetes to restart the pod falsely.

### 2. Sidecar Mode (High Performance)

For high-throughput production environments, you can enable the **Health Probe Sidecar**. This isolates health checks into a separate lightweight process (listening on port `9000`) to prevent false restarts during heavy load.

**How it works**:
1.  **Main Proxy** calculates health and **pushes** status to the sidecar every second (`http://localhost:9000/internal/update-status`).
2.  **Sidecar** serves the probes from memory instantly.
3.  **Safety**: If the sidecar stops receiving updates for >10 seconds, it assumes the main proxy is deadlocked/crashed and fails the probes.

**Enable Sidecar Mode**:
Set the environment variable:
`HealthProbeSidecar=Enabled=true;url=http://localhost:9000`

**Kubernetes Config (Sidecar Mode)**:
Point your probes to port `9000` instead of the main port and configure Liveness, Readiness, and Startup probes.  In this example configuration, the proxy will send updates every second.  The sidecar will fail if it has not heard from the proxy for 10 seconds. 

```yaml
livenessProbe:
  httpGet:
    path: /liveness
    port: 9000
    scheme: HTTP
  failureThreshold: 3
  periodSeconds: 5

readinessProbe:
  httpGet:
    path: /readiness # or /startup 
    port: 9000
    scheme: HTTP
  failureThreshold: 3
  periodSeconds: 5

startupProbe:
  httpGet:
    path: /startup
    port: 9000
    scheme: HTTP
  failureThreshold: 30
  periodSeconds: 5
```
