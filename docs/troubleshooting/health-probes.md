# Health Probe Failures / Pod Restarts

> **TL;DR**
> Probe failures under heavy load usually mean ThreadPool starvation. Enable the **Health Probe Sidecar** to isolate probes from application traffic. Under light load, probe failures almost always mean no backends are healthy.

---

## Probe endpoints reference

| Endpoint | Port | Returns 200 when… |
|----------|------|-------------------|
| `/liveness` | main / 9000 | Process is running |
| `/readiness` | main / 9000 | At least one backend is healthy |
| `/startup` | main / 9000 | Backend poller has completed its first pass |
| `/health` | main only | Always 200 (alias for liveness) |

---

## Symptom: readiness returns 503

The probe body will tell you why:

| Body | Cause |
|------|-------|
| `Not Healthy. Active Hosts: 0` | No backends passed health checks |
| `Not Healthy. Failed Hosts: True` | At least one circuit breaker is open |

**Fix for "Active Hosts: 0":**
- Verify `Host1`…`Host9` are correctly configured with valid URLs and probe paths.
- Test the backend probe path directly: `curl <backend-url>/<probe-path>`
- Check `PollInterval` and `PollTimeout` — if `PollTimeout` is shorter than the backend's response time, every probe times out.

| Setting | Env Var | App Config key |
|---------|---------|----------------|
| Poll interval (ms) | `PollInterval=<ms>` | `Cold:Server:PollInterval` |
| Probe timeout (ms) | `PollTimeout=<ms>` | `Cold:Server:PollTimeout` |

> [!TIP]
> Set `SuccessRate` lower (e.g. `50`) to keep a partially-recovering host in the active pool. Default is `80` (%).

**Fix for "Failed Hosts: True":**
→ See [circuit-breaker.md](circuit-breaker.md).

---

## Symptom: probes slow or timing out under high load

Under heavy load (~1000 concurrent requests), async Kestrel handlers compete with proxy workers for ThreadPool threads. Probes can queue for 1–2 seconds, causing Kubernetes to mark the pod unhealthy and restart it.

**Fix — enable the Health Probe Sidecar:**

The sidecar is a lightweight Kestrel process on port 9000 that serves probes from memory. The main proxy pushes its health state to it every second. Probes are served synchronously, avoiding any ThreadPool dependency.

```bash
# Enable sidecar
HealthProbeSidecar=Enabled=true;url=http://localhost:9000
```

Then point your Kubernetes probes to port 9000:

```yaml
livenessProbe:
  httpGet:
    path: /liveness
    port: 9000
  failureThreshold: 3
  periodSeconds: 5

readinessProbe:
  httpGet:
    path: /readiness
    port: 9000
  failureThreshold: 3
  periodSeconds: 5

startupProbe:
  httpGet:
    path: /startup
    port: 9000
  failureThreshold: 30
  periodSeconds: 5
```

> [!NOTE]
> If the sidecar does not receive a status update from the main proxy for more than 10 seconds, it automatically fails all probes — protecting against a silently deadlocked main process.

---

## Symptom: startup probe fails before backends are ready

The startup probe returns 503 until the backend poller completes its first pass. If `failureThreshold × periodSeconds` is shorter than `PollInterval`, the pod restarts before it can become ready.

**Fix:** Increase `failureThreshold` on the startup probe so it waits at least as long as one full poll cycle.

```yaml
startupProbe:
  failureThreshold: 30   # 30 × 5s = 150s budget
  periodSeconds: 5
```

---

## Related

- [HEALTH_CHECKING.md](../HEALTH_CHECKING.md) — full health probe reference
- [SIDECAR_DEPLOYMENT.md](../SIDECAR_DEPLOYMENT.md) — sidecar deployment configuration
- [circuit-breaker.md](circuit-breaker.md) — circuit breaker troubleshooting
- [backend-hosts.md](backend-hosts.md) — backend host troubleshooting
