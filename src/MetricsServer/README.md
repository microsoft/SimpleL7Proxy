# MetricsServer

In-memory rollup metrics server for SimpleL7Proxy services. Other services POST rollup counters; operators and services query rolled up status per user and per model.

**TL;DR**
- Run it: `dotnet run --project src/MetricsServer` (listens on port 9100).
- Send rollups: `POST /metrics/rollup` with one record, a JSON array, or `{"records":[...]}`.
- Query status: `GET /metrics/status?user=alice&model=gpt-4o&window=300`.

> [!IMPORTANT]
> Data is kept in memory only. Nothing is persisted, and all counters are lost on restart.

## Units used in this doc

Durations are seconds unless the name ends in `Ms` (milliseconds). Timestamps are Unix seconds (UTC).

## Configuration

| Environment variable | Default | Units | Description |
| --- | --- | --- | --- |
| `METRICSSERVER_PORT` | `9100` | port | TCP port Kestrel listens on. |
| `METRICSSERVER_BUCKET_SECONDS` | `60` | seconds | Width of one rollup bucket. |
| `METRICSSERVER_BUCKET_COUNT` | `60` | buckets | Buckets retained per user/model series. |
| `METRICSSERVER_MAX_SERIES` | `100000` | series | Cap on distinct user/model pairs held in memory. |
| `METRICSSERVER_MAX_BODY_BYTES` | `4194304` | bytes | Maximum accepted request body size. |
| `APPINSIGHTS_CONNECTIONSTRING` | empty | string | Application Insights connection string. Telemetry is off when empty. `APPLICATIONINSIGHTS_CONNECTION_STRING` is accepted as a fallback. |
| `METRICSSERVER_TELEMETRY_INTERVAL_SECONDS` | `60` | seconds | How often server counters are published to Application Insights. |

Retention window = `METRICSSERVER_BUCKET_SECONDS` × `METRICSSERVER_BUCKET_COUNT` (default 1 hour). Values that are missing, unparsable, or out of range fall back to the default. All settings are read once at startup.

## Endpoints

| Method | Path | Purpose |
| --- | --- | --- |
| `POST` | `/metrics/rollup` | Merge rollup records into the store. |
| `GET` | `/metrics/status` | Aggregated status for a user, a model, or both. |
| `GET` | `/metrics/series` | Bucketed time series for the same filters. |
| `GET` | `/metrics/users` | Known users, optionally filtered by `model`. |
| `GET` | `/metrics/models` | Known models, optionally filtered by `user`. |
| `GET` | `/metrics/stats` | Server counters and memory footprint. |
| `GET`/`HEAD` | `/health`, `/liveness`, `/readiness` | Container probes. |

Query parameters for `/metrics/status` and `/metrics/series`: `user`, `model`, and `window` (seconds). Omit `user` or `model` to aggregate across all values. `window` defaults to, and is capped at, the retention window, and is rounded up to whole buckets.

## Sending rollups

**Rollup counters are additive; the server never stores individual requests.**

```bash
curl -X POST http://localhost:9100/metrics/rollup \
  -H 'Content-Type: application/json' \
  -d '{"user":"alice","model":"gpt-4o","requests":10,"successes":9,"failures":1,"latencyMsTotal":4200,"latencyMsMax":900,"promptTokens":1200,"completionTokens":800}'
```

Accepted payload shapes: a single object, a JSON array of objects, or `{"records":[ ... ]}`.

Record fields: `user`, `model`, `requests`, `successes`, `failures`, `latencyMsTotal`, `latencyMsMax`, `promptTokens`, `completionTokens`, `timestamp`.

- `user` and `model` are trimmed, matched case-insensitively, and recorded as `unknown` when empty.
- `requests` defaults to `successes + failures` when omitted or zero.
- `timestamp` defaults to now. Records older than the retention window, or more than one bucket in the future, are rejected.

The response body is `{"accepted":N,"rejected":M}`. Status codes:

- `202 Accepted` — at least one record was merged.
- `503 Service Unavailable` — nothing merged because the series limit was reached; retrying later can succeed.
- `400 Bad Request` — nothing merged because every record was unusable or outside the retention window, or the body was empty or not valid JSON.
- `413 Payload Too Large` — the body exceeded `METRICSSERVER_MAX_BODY_BYTES`.

## Querying status

```bash
curl 'http://localhost:9100/metrics/status?user=alice&model=gpt-4o&window=300'
```

```json
{
  "user": "alice", "model": "gpt-4o",
  "windowSeconds": 300, "windowStart": 1758484800, "windowEnd": 1758485100,
  "seriesCount": 1, "requests": 10, "successes": 9, "failures": 1,
  "successRate": 0.9, "averageLatencyMs": 420, "maxLatencyMs": 900,
  "promptTokens": 1200, "completionTokens": 800,
  "lastUpdate": 1758485090, "status": "healthy"
}
```

`status` is derived from the failure ratio in the window: `unknown` with no requests, `healthy` at or below 5% failures, `degraded` at or below 25%, `unhealthy` above that.

> [!TIP]
> Query `?user=alice` alone for a per-user rollup across every model, or `?model=gpt-4o` alone for a per-model rollup across every user.

## Memory model

Each user/model pair owns a fixed ring of buckets, so memory per series is constant and total memory is bounded by `METRICSSERVER_MAX_SERIES`. Counters are updated with interlocked operations, so concurrent ingest calls do not block each other. A background loop removes series that have received nothing for a full retention window.

## Application Insights

**Telemetry is enabled only when a connection string is configured.** Set `APPINSIGHTS_CONNECTIONSTRING` (or `APPLICATIONINSIGHTS_CONNECTION_STRING`) to register the Application Insights SDK (`Microsoft.ApplicationInsights.WorkerService`, .NET 10). When it is set the service:

- Sends all `ILogger` output to Application Insights in addition to the console.
- Stamps every item with cloud role `metricsserver`, the machine name as role instance, and the version from `Constants.cs`.
- Publishes `MetricsServer.SeriesCount`, `MetricsServer.UserCount`, `MetricsServer.ModelCount`, `MetricsServer.RecordsIngested`, and `MetricsServer.RecordsDropped` custom metrics on the telemetry interval.

Adaptive sampling is disabled so counter values are not distorted.

```bash
export APPINSIGHTS_CONNECTIONSTRING="InstrumentationKey=...;IngestionEndpoint=https://...;"
dotnet run --project src/MetricsServer
```

## Running in a container

```bash
cd src
docker build -f MetricsServer/Dockerfile -t metricsserver:v1.0.0 .
docker run -p 9100:9100 metricsserver:v1.0.0
```

`build.sh` builds the image, tags it with the version from `Constants.cs`, and pushes it to ACR. Set `ACR` (and optionally `ACRGROUP`) first, or let the script source `deployment/proxy-with-sidecar/deploy.parameters.sh`:

```bash
export ACR=myregistry
./build.sh
```

## Troubleshooting

| Symptom | Cause | Check |
| --- | --- | --- |
| `503` from `/metrics/rollup` | Series limit reached | `GET /metrics/stats` for `seriesCount` vs `maxSeries` and `recordsDropped` |
| `400` from `/metrics/rollup` with `rejected > 0` | Timestamps outside retention, or null/unusable records | Compare record `timestamp` values against `retentionSeconds` in `/metrics/stats` |
| `status` stays `unknown` | No requests matched the window | Widen `window`, or confirm the `user`/`model` spelling with `/metrics/users` and `/metrics/models` |
| Counters reset unexpectedly | Process restarted | Data is in memory only; check container restarts |
| `400 Invalid JSON payload` | Malformed body | Validate the JSON and `Content-Type: application/json` |
