# Troubleshoot Health Probes and Restarts

Map each probe status and body to the complete set of supported health conditions before changing orchestration thresholds.

## TL;DR

1. Confirm whether the probe targets the main proxy port or the optional sidecar port; they do not expose identical endpoints or liveness behavior.
2. Treat `Active Hosts: 0` and `Failed Hosts: True` as aggregate states with multiple possible causes.
3. Use `/health` and `/healthdetail` on the main port plus proxy and sidecar logs to identify the failing component.

## Separate Main and Sidecar Endpoints

**Send probes to one serving process consistently and interpret that process’s behavior.**

| Endpoint | Main proxy port | Sidecar port (`HEALTHPROBE_PORT`, default `9000`) |
|----------|-----------------|---------------------------------------------------|
| `/liveness` | `200 OK` while the main listener can answer | GET returns `200 OK` while status updates are current; returns `503` when updates are stale |
| `/readiness` | `200` or `503` from the current health aggregate | Mirrors the last pushed readiness state; GET returns `503` when updates are stale |
| `/startup` | `200` or `503` from the current health aggregate | Mirrors the last pushed startup state; GET returns `503` when updates are stale |
| `/health` | `200` diagnostic report, including readiness and startup state | Not exposed (`404`) |
| `/healthdetail` | `200` expanded diagnostics | Not exposed (`404`) |
| `/internal/update-status` | Not a public main-process probe | Sidecar update endpoint used by the main process |

The main application port is configured by `Port` and is `8000` in the standard examples. The sidecar listens on `HEALTHPROBE_PORT`, default `9000`, as a separate process.

> [!NOTE]
> Sidecar GET responses consider the last update stale after 20 seconds. The current sidecar HEAD handlers do not apply the same stale-update check, so use GET probes when stale-main-process detection is required.

## Map Status and Body to Causes

**A body names an aggregate state, not a unique root cause.**

| Status and body | Endpoint source | Supported causes to check |
|-----------------|-----------------|---------------------------|
| `200 OK`, `OK` | Main or sidecar `/liveness`; ready `/readiness` or `/startup` | Handler is responding; for readiness/startup, all current aggregate checks passed |
| `503`, `Not Healthy.  Active Hosts: 0` | Main `/readiness` or `/startup` | Startup readiness gates are incomplete; event backlog exceeds `EVENTHUB_MAX_UNDRAINED_EVENTS`; async blob queue exceeds `AsyncBlobMaxQueue`; or active-host count is zero |
| `503`, `Not Healthy.  Failed Hosts: True` | Main `/readiness` or `/startup` | Circuit status check reports failed, or the event client reports unhealthy |
| `503`, `Not Healthy.  Active Hosts: 0` | Sidecar `/readiness` or `/startup` | The last pushed state was a zero-host aggregate, with the same possible main-process causes above |
| `503`, `Not Healthy.  Failed Hosts: True` | Sidecar GET `/liveness` | At least one update was received, but the most recent update is more than 20 seconds old |
| `503`, `Not Healthy.  Failed Hosts: True` | Sidecar GET `/readiness` or `/startup` | The last pushed state was failed, or a previously received update is more than 20 seconds old |
| `200` diagnostic body showing `/readiness : 503 ...` | Main `/health` or `/healthdetail` | Diagnostic endpoint is working while readiness or startup is unhealthy; inspect the reported participants, queues, and hosts |
| Timeout, connection refusal, or reset with no HTTP body | Main or sidecar | Wrong port, process unavailable, listener saturation, network policy, container restart, or probe timeout |

`ReadinessZeroHosts` is reused when `_systemReady` is false, the event backlog is excessive, the blob queue is unhealthy, or active-host count is zero. `ReadinessFailedHosts` is reused for a failed circuit status or an unhealthy event client. The same mapping is used for startup status.

## Inspect Main-Process Health

**Use main-port diagnostics to separate initialization, event, blob, circuit, and host conditions.**

```bash
curl -i http://<proxy-host>:<main-port>/readiness
curl -i http://<proxy-host>:<main-port>/health
curl -i http://<proxy-host>:<main-port>/healthdetail
```

Check:

- Startup readiness logs for `Backends`, `BackendTokens`, `Workers`, `UserProfiles`, and `EventClient`; async mode adds template, blob-writer, and Service Bus participants.
- `Undrained` versus `EVENTHUB_MAX_UNDRAINED_EVENTS`.
- `Blob Queue` versus `AsyncBlobMaxQueue`.
- Event-client health and recent drain activity.
- Active and configured backend hosts, probe status, authentication, DNS, and circuit logs.

Startup failure is not limited to waiting for the first backend poll. Any incomplete readiness participant or failed aggregate check can keep `/startup` at `503`.

## Inspect Sidecar Health

**Verify both the sidecar listener and status-update channel from the main process.**

```bash
curl -i http://localhost:9000/liveness
curl -i http://localhost:9000/readiness
curl -i http://localhost:9000/startup
```

Confirm the main proxy logs `External health probe sidecar enabled`, the configured URL targets the sidecar, and update requests to `/internal/update-status` succeed. Sidecar logs report missing or invalid update parameters; main-process logs report failed update attempts. Before the first update arrives, sidecar liveness returns `200`, while readiness and startup use their initial zero-host state.

The sidecar runs a separate tuned Kestrel service and serves cached status from memory. This reduces contention with the main listener, but it does not eliminate .NET runtime, Kestrel, socket, scheduling, or ThreadPool dependencies.

```bash
HealthProbeSidecar=Enabled=true;url=http://localhost:9000
```

## Configure Orchestrator Probes

**Set startup and failure budgets from observed initialization and response latency, not only `PollInterval`.**

```yaml
livenessProbe:
  httpGet: { path: /liveness, port: 9000 }
  failureThreshold: 3
  periodSeconds: 5
readinessProbe:
  httpGet: { path: /readiness, port: 9000 }
  failureThreshold: 3
  periodSeconds: 5
startupProbe:
  httpGet: { path: /startup, port: 9000 }
  failureThreshold: 30
  periodSeconds: 5
```

Ensure the startup budget covers backend initialization, token acquisition, workers, profiles, event clients, and enabled async participants. For sidecar probes, also allow the sidecar process to start and receive its first status update.

## Related

- [Health Endpoint Reference](../reference/health-endpoints.md)
- [Deploy the Health Probe Sidecar](../how-to/deploy-sidecar.md)
- [Circuit Breaker Troubleshooting](circuit-breaker.md)
- [Backend Host Troubleshooting](backend-hosts.md)
