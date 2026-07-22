# Day-2 Operations

Guidance for running SimpleL7Proxy in production *after* the initial deployment.
This page complements the [Deployment Guide](README.md) and aligns with
[Azure Well-Architected Framework](https://learn.microsoft.com/azure/well-architected/)
operational excellence and reliability pillars.

---

## Updating backends (no redeploy)

Backend host configuration is sourced from **Azure App Configuration**, so
backend changes do **not** require rebuilding the image or redeploying ACA.

- Update the relevant keys in App Configuration (e.g. `Hosts1`, priorities, weights).
- The proxy picks up changes on its configured refresh interval — no restart needed.
- Validate by tailing ACA logs and confirming traffic now lands on the new host(s).

See: [BACKEND_HOSTS.md](../docs/reference/backend-hosts.md), [AZURE_APP_CONFIGURATION.md](../docs/how-to/configure-app-configuration.md)

---

## Rolling out new proxy versions

Image tags are immutable and derived from `src/SimpleL7Proxy/Constants.cs`.
A version bump = a new tag = a new ACA revision.

1. Bump the version in `Constants.cs` and merge.
2. **Rebuild** the image:
   ```bash
   cd ContainerImage
   ./deploy.sh
   ```
3. **Redeploy** ACA (it picks up the new tag from the build output):
   ```bash
   cd ../ACA
   ./deploy.sh
   ```
4. ACA creates a new revision. Use **revision traffic splitting** to canary
   (e.g. 10% new / 90% old) before shifting to 100%.
5. Roll back by shifting traffic back to the previous revision — no rebuild required.

---

## Scaling considerations

SimpleL7Proxy runs on Azure Container Apps with KEDA-based autoscaling.

**Replica counts**

- `minReplicas` — keep ≥ 1 to avoid cold starts on a hot path; raise for HA.
- `maxReplicas` — cap based on downstream backend capacity, not just proxy CPU.

**Scale triggers**

- **CPU** — good default for steady, compute-bound traffic.
- **HTTP concurrency** — better for bursty workloads or long-lived requests
  (async / streaming). Tune `concurrentRequests` to match per-replica
  backend connection budget.
- Align `concurrentRequests` with the proxy's own `Workers` and
  `MaxQueueLength` settings — repository deployment templates ship
  different concurrency targets, so treat them as starting points, not
  universal defaults.

**Sizing tips**

- Start with 0.5 vCPU / 1 GiB and load-test with [TestClient](../TestClient/).
- Watch p95/p99 latency, not just RPS — proxy overhead shows up at the tail.
- For async / streaming workloads, prefer concurrency-based scaling.

---

## Failure modes

| Symptom | Likely cause | Where to look |
|---|---|---|
| 5xx spike, all backends | Backend outage / DNS | ACA logs, backend health, Private DNS |
| 5xx spike, one backend | Single host down | HealthProbe results, App Config priorities |
| Requests time out | Backend slow / pool exhausted | Concurrency metrics, timeouts in App Config |
| Proxy not starting | Identity / App Config access | ACA system logs, Managed Identity role assignments |
| Stale config | App Config refresh interval | Refresh sentinel / TTL settings |

**HealthProbe down** — the proxy continues with last-known health state and
serves traffic per existing priorities. Restore the probe and health
re-converges automatically.

**Backend unreachable** — circuit breaker opens for that host; traffic shifts
to the next-priority backend. See [CIRCUIT_BREAKER.md](../docs/reference/circuit-breaker.md)
and [HEALTH_CHECKING.md](../docs/reference/health-endpoints.md).

---

## Where logs live

- **ACA console logs** — quickest view, per-revision:
  ```bash
  az containerapp logs show -n <app> -g <rg> --follow
  ```
- **Log Analytics** — long-term, queryable via KQL. The ACA environment is
  wired to a Log Analytics workspace; query `ContainerAppConsoleLogs_CL` and
  `ContainerAppSystemLogs_CL`.
- **Application Insights** — distributed traces, request metrics, dependencies.
  See [OBSERVABILITY.md](../docs/concepts/observability.md).
- **(Optional) Blob async logs** — full request/response bodies for async
  flows. See [StorageBlobConfig.md](../docs/reference/storage-blob-config.md) and
  [AsyncOperation.md](../docs/concepts/async-processing.md).

---

## Related

- [Deployment Guide](README.md)
- [OBSERVABILITY.md](../docs/concepts/observability.md)
- [HEALTH_CHECKING.md](../docs/reference/health-endpoints.md)
- [CIRCUIT_BREAKER.md](../docs/reference/circuit-breaker.md)
- [TIMEOUTS.md](../docs/reference/timeouts.md)
- [TroubleshootTOC.md](../docs/troubleshooting/README.md)
