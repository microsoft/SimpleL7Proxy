# EventHub Monitor — Requirements & Behavior Specification

## A. Document Metadata

| Field | Value |
| --- | --- |
| Title | EventHub Monitor Page — Requirements & Behavior Specification |
| Version | 1.0 |
| Last Updated | 2026-07-13 |
| Owner | SimpleL7Proxy / chat_tester |
| Route | `/eventhub` |
| Purpose | Authoritative baseline of observable behavior, used to refactor the implementation and re-validate that no behavior changed. |
| Status | Descriptive — captures current behavior as of `feature/async`. |

---

## B. Summary

**What this defines:** every value the EventHub Monitor page renders, the exact source event fields it derives from, and the computation rule for each derived value.

**Why it exists:** the current implementation (`EventHubReader`, `EventHubMonitorStore`, `ProxyMetricsCatalog`, `EventHubMonitorPage.razor`, `Pipeline/*`) is slated for cleanup. This document is the contract the cleaned-up code MUST continue to satisfy.

**TL;DR**
- Input is a stream of newline-delimited JSON `S7P-*` events from Azure Event Hub (or a local file).
- Events are aggregated into an in-memory store; requests are retained for **1 hour**.
- The UI pulls an immutable snapshot every **`RefreshSeconds`** (default 5s) and renders five regions: **Runtime stats**, **Backends + Metrics**, **Endpoints**, **Paths/Users**, and **Request status**.

---

## C. Scope & Applicability

**In scope**
- The `/eventhub` page and its server-side data pipeline (reader → store/catalog → snapshot → UI).
- Field-level mapping from `S7P-*` events to every rendered value.

**Out of scope**
- The proxy that emits the events; Event Hub provisioning; authentication configuration.
- Other pages in `chat_tester`.

**Dependencies**
- `Azure.Messaging.EventHubs` consumer client.
- Shared history component `MultiRequestStatusItem` and `EventHubRequestStatus.razor`.
- `InspectorPageShell` for the page frame + history/scope selector.

---

## D. Ingest & Data Sources

### D.1 Transport
- REQUIRED: The reader is a `BackgroundService` (singleton, server-side). One shared store feeds all connected browser circuits.
- The reader connects to Event Hub when `eventhub_enabled = true` **and** configuration is valid; otherwise it stays idle.
- On startup, if `LocalFilePath` is set and the file exists, the file is imported first with **request aging disabled** (records are not purged by the 1-hour window). Live Event Hub mode keeps aging **enabled**.
- Auth order: connection string → on `LocalAuthDisabled` rejection, retry with `DefaultAzureCredential` (managed identity).
- Each record MUST be a JSON object. Records that are not objects, fail to parse, or have no `Type` field MUST be skipped (counted as skipped, never crash the reader).

### D.2 Event types consumed
Records are routed by their `Type` field. All other `Type` values MUST be ignored.

| `Type` | Effect on state |
| --- | --- |
| `S7P-Backend` | Replaces backend health list + fleet info. |
| `S7P-ProxyRequestEnqueued` | Records enqueue success; captures Enqueue phase; adds lifecycle step. |
| `S7P-BackendRequest` | Adds a per-attempt request item (feeds **Endpoints** only). |
| `S7P-ServerError` | Records a server-side rejection; adds lifecycle step. |
| `S7P-CircuitBreakerError` | Records circuit-breaker history (server-level or per-backend). |
| `S7P-ProxyRequest` | Final request outcome — owns request panel + runtime stats. |
| `S7P-ProxyRequestExpired` | Treated as a failed final request. |
| `S7P-ProxyRequestRequeued` | Requeue signal (counted as a retry). |

### D.3 Correlation & identity
- **Request identity (phases):** `S7P-ID`, falling back to `MID`. Shared across enqueue, every attempt, and the final proxy record.
- **Lifecycle correlation:** `GUID`, falling back to `MID`. (`MID` differs per backend attempt; `GUID` is stable per request.)
- INVARIANT: after a final `S7P-ProxyRequest`/`Expired`/`Requeued` is processed, its lifecycle and phase entries for that key MUST be released.

---

## E. Store, Retention & Snapshot

- Requests are held oldest-first in memory. **Retention = 1 hour** when aging is enabled; purge occurs on every add and on every snapshot read.
- Local-file import sets **DisableRequestAging = true** → no purge (full file remains visible).
- `RequestsPerSecond` is computed over a trailing **5-second** window.
- A monotonic `RequestNumber` is assigned by the store per added item.
- The UI never renders directly off mutations; it pulls an immutable `MonitorSnapshot`. `LastDataUtc` marks the last ingest; the feed is "live" when `now - LastDataUtc < 5s`.
- REQUIRED: `S7P-BackendRequest` items are **excluded** from the request panel and from every runtime-stats aggregate. They contribute **only** to the Endpoints region.

---

## F. UI Regions Specification

Layout (desktop, ≥992px): three columns — **Request status** (left/order-1), **Runtime stats + Paths + Users** (center/order-2), **Backends + Endpoints** (right/order-3).

### F.1 Runtime stats — tile "Request"
Primary = `TotalRequests` (non-backend request count), subtitle "last hour". Tile CSS = `success` when `SuccessRate ≥ 95`, else `warning`.

| Metric | Source / rule |
| --- | --- |
| Req / sec | requests received in trailing 5s ÷ 5 |
| Success | `succeeded / decided × 100`, where decided = items with a status code; 2xx = success |
| Failed | count of decided items that are not 2xx |
| Enqueued | count of `S7P-ProxyRequestEnqueued` successes |
| Processing | `max(0, Enqueued − Completed)` |
| Completed | non-backend request count |
| Avg size | mean `RequestContentLength` over items where it is > 0 |

### F.2 Runtime stats — tile "Server"
Primary = `AvgLatencyMs` (mean `Duration` of non-backend requests) + "backend avg". CSS = `info`.

| Metric | Source / rule |
| --- | --- |
| Probe | fleet `ProbeLatencyMs` (mean backend latency) |
| Balancing | `LoadBalanceMode` from `S7P-Backend` (default `latency`) |
| Enqueue | `EnqueueSuccess/EnqueueAttempts (rate%)` |
| Enqueue failed | count of `S7P-ServerError` |
| Enqueue Q/AH | last enqueue `QueueLength`/`ActiveHosts` |
| Rejected | count of `S7P-ServerError` |
| 403 NotAuth | `S7P-ServerError` with status 403 + message "Not Authorized" |
| Queue len | latest server queue length (max) |

### F.3 Runtime stats — tile "Circuit breaker"
Primary = `OPEN` when server CB open, else `CLOSED`; "live state". CSS = `warning` when open else `success`.

- Server CB is open when any explicit server CB signal was seen **or** every observed endpoint's latest state is open (`endpointOpenCount == endpointCount && endpointCount > 0`).
- A 2xx–3xx final request resets the server CB flag to closed.

| Metric | Source / rule |
| --- | --- |
| Endpoint open | `EndpointCircuitBreakerOpenCount / EndpointCount` |
| Scope | constant "server + endpoint" |
| Server CB events | `ServerEventCount` (from `S7P-CircuitBreakerError`) |
| Last code | last CB error code, else `-` |

**Endpoint CB signal** = any of `Message`/`ErrorDetail`/`Error`/`ErrorMessage`/`backendLog`/`Attempt-1-backendLog` containing "No active hosts", "CALL INCOMPLETE", "CircuitBreaker", or "THROTTLED"; **or** a final `S7P-ProxyRequest` with status ≥ 500.
**Server CB signal** = `S7P-CircuitBreakerError`, or any endpoint CB signal.

### F.4 Backends card
Header subtitle: `N host(s) · <LoadBalancingMode>`. One tile per host from the latest `S7P-Backend` event (fields `{i}-Host`, `{i}-Status`, `{i}-Latency`, `{i}-SuccessRate`, `{i}-Calls`, `{i}-Errors`, enumerated `i = 1..`).

| Field | Source / rule |
| --- | --- |
| Name | host of `{i}-Host` URL |
| Status | `{i}-Status` |
| Lat | `{i}-Latency` (rounded, " ms") |
| Succ | `{i}-SuccessRate` (%) |
| ProbeOK | `max(0, Calls − Errors)` |
| ProbeFail | `max(0, Errors)` |
| ReqCalls | count of final `S7P-ProxyRequest` matched to this host key |
| ReqFail | of those, count not 2xx |
| ReqAvg | mean `Duration` of those requests, " ms" |

**Tile CSS:** `down` if `SuccessRate < threshold`; else `healthy` if status contains "active"; else `degraded` if status contains "throttle"/"below"/"fail"; else `neutral`. Threshold from `SuccessRate` field, normalized (≤1 → ×100), default 80.

### F.5 Metrics by group (below Backends)
Rendered from `ProxyMetricsCatalog` active groups, **excluding** `Endpoints`, `Backends`, and `Request` groups (i.e., shows `Models` and `Server`). Each row is `metric name → value`, filtered to the active scope.

### F.6 Endpoints card
Endpoints are derived **only** from the backend target inside `backendLog` (`Using <NAME> URL: <url>`). Request paths are NOT endpoints. Sources: `S7P-BackendRequest` (`backendLog` in response headers) and every `Attempt-{n}-backendLog` inside the final `S7P-ProxyRequest`.

- DEDUPE: aggregate each distinct `backendLog` string once (a `S7P-BackendRequest` and the same attempt echoed inside `S7P-ProxyRequest` MUST NOT double-count). Backend-request items are processed first so their richer headers win.
- Aggregated per target name; ordered by `Calls` desc, then name; **top 10**.

| Field | Source / rule |
| --- | --- |
| Path (title) | target name from `backendLog` |
| Calls | records aggregated to this target |
| Fail | of those, status ≥ 400 |
| Attempts | mean `x-PolicyCycleCounter` (request headers) |
| Proc(s) | mean `Request-Process-Duration` |
| Queue | mean `Proxy-Queue-Duration` (parsed from `backendLog`) ms |
| ProxyProc | mean `Proxy-Process-Duration` (from `backendLog`) ms |
| Thrtl | count of logs containing "THROTTLED" |
| TargetThr | count of logs containing "THROTTLED: <target>" |
| Incomplete | count of logs containing "CALL INCOMPLETE" |
| Exhausted | count of logs containing "RETRIES LEFT: exhausted" |

**Tile CSS:** `down` if failure-rate ≥ 50%; `degraded` if > 0%; else `healthy`.

### F.7 Paths card (Success vs Failed)
Only final `S7P-ProxyRequest`/`S7P-ProxyRequestExpired`. Success = status in [200,400). Path from `Path:` field, else request-line first token, else `/`. Grouped by path, counted, ordered by count desc then path, **top 10** each side.

### F.8 Users card (Success vs Failed)
Same event filter and success rule as Paths. Key = `UserID` (else `(unknown)`). Ordered by count desc then user, **top 10** each side.

### F.9 Request status column
Renders `HistoryItems` = snapshot requests **excluding** `S7P-BackendRequest`, optionally scoped (see F.10), via `EventHubRequestStatus.razor`.

- **Statistics** sub-card: outcome thermometer + per-status-code table (Total, TTFB, Avg) + ALL row. Rows are clickable status filters.
- **Request status** list: one row per request showing `RequestNumber`, a duration thermometer, and the status code. Header shows `completed/total complete`.
- **Hover:** tooltip with S7P-ID + high-level stats.
- **Click:** modal with tabbed per-phase detail (Enqueue tab, one tab per backend attempt, Final tab) plus a `backendLog` drill-down. Backed by `MultiRequestStatusItem.Phases` (`RequestPhaseView`).

### F.10 Scope / history selector
`InspectorPageShell` history entries come from `ProxyMetricsCatalog` scope options: `all`, per `ContainerApp` (`app:<app>`), and per replica (`replica:<app>:<replica>`). Selecting a scope filters the Request-status column by `ContainerApp`/`Replica` and re-resolves the "Metrics by group" values to that scope. Unknown/empty scope → `all`.

---

## G. Refresh & Lifecycle

- On page init: `Connect()` snapshots the store, subscribes to `Store.Changed`, and starts a `PeriodicTimer(RefreshSeconds)` loop.
- `Store.Changed` triggers **one** immediate paint (first batch only); all later updates are throttled to the timer cadence.
- `RefreshSeconds < 1` is coerced to 5.
- On dispose: unsubscribe, cancel timer, reset local snapshot to empty. Live badge shows "Awaiting data" until first data.

---

## H. Configuration (`EventHubMonitor` section, appsettings.json only)

| Key | Default | Meaning |
| --- | --- | --- |
| `eventhub_enabled` | `true` | Connect to live Event Hub. |
| `LocalFilePath` | `""` | NDJSON file imported at startup (disables aging). |
| `ConnectionString` | `""` | Namespace connection string. |
| `EventHubName` | `""` | Hub/topic name (REQUIRED for live mode). |
| `EventHubNamespace` | `""` | Namespace for managed-identity auth (`.servicebus.windows.net` appended if bare). |
| `ConsumerGroup` | `$Default` | Consumer group. |
| `StartPosition` | `latest` | `latest` or `earliest`. |
| `RefreshSeconds` | `5` | UI snapshot cadence. |

Environment variables override: `EVENTHUB_CONNECTIONSTRING`, `EVENTHUB_NAME`, `EVENTHUB_CONSUMER_GROUP`, `EVENTHUB_NAMESPACE`.

---

## I. Invariants (MUST hold after refactor)

1. `S7P-BackendRequest` MUST NOT appear in the request panel or any runtime aggregate; it MUST feed Endpoints only.
2. A `backendLog` string MUST be counted once per Endpoints aggregate (no double-count between attempt and proxy-summary echoes).
3. Final `S7P-ProxyRequest` MUST own lifecycle/phase removal for its key.
4. Request retention MUST be 1 hour with aging enabled; local-file import MUST disable aging.
5. `RequestsPerSecond` MUST use a trailing 5s window.
6. Success MUST be defined as HTTP status in [200,300) for runtime success rate, and [200,400) for Paths/Users classification (note: these two definitions differ intentionally — preserve both).
7. Server CB MUST report open when all observed endpoints are open, and MUST reset on a 2xx–3xx final request.
8. Invalid/unsupported records MUST be skipped without stopping the reader.
9. All list regions (Endpoints, Paths, Users, server TopPaths) MUST cap at top 10.
10. The UI MUST render from an immutable snapshot, not from live mutation.

---

## J. Validation Checklist

Use with a known NDJSON fixture (set `LocalFilePath`, `eventhub_enabled=false`).

- [ ] Page loads at `/eventhub`; live badge shows a timestamp after import.
- [ ] **Request** tile: Completed == number of final proxy requests in fixture; Failed == non-2xx; Enqueued == `S7P-ProxyRequestEnqueued` count.
- [ ] **Server** tile: Rejected/Enqueue-failed == `S7P-ServerError` count; 403 NotAuth matches fixture.
- [ ] **Circuit breaker** tile: OPEN/CLOSED and Endpoint-open ratio match expected states; Last code matches last `S7P-CircuitBreakerError`.
- [ ] **Backends**: one tile per `{i}-Host`; ProbeOK/ProbeFail = Calls−Errors/Errors; ReqCalls/ReqFail/ReqAvg match final requests routed to each host; tile color matches status/threshold rule.
- [ ] **Endpoints**: one tile per distinct `Using <NAME> URL:` target; Calls have no double-count when an attempt appears both as `S7P-BackendRequest` and inside the proxy summary; Thrtl/Incomplete/Exhausted counts match log substrings; capped at 10.
- [ ] **Paths**: success/failed split uses [200,400); top 10; correct path extraction.
- [ ] **Users**: keyed on `UserID`, `(unknown)` fallback; top 10.
- [ ] **Request status**: `S7P-BackendRequest` excluded; per-status table totals sum to ALL; row click filters; item click opens phase tabs (enqueue/attempts/final) with backendLog drill-down.
- [ ] **Scope selector**: `all` + per-app + per-replica entries appear; selecting one filters the request column and re-scopes "Metrics by group".
- [ ] With aging enabled and a >1h old timestamp, that request is purged; with local-file import, it is retained.
- [ ] Feeding a malformed record does not stop ingestion (skipped count increments in logs).

---

## K. Known Simplification Targets (informational, not requirements)

These are current implementation smells the cleanup may address **without** changing behavior above:
- Each record is JSON-parsed multiple times across the four `Pipeline/*` processors + `CleanupProcessor` + `ProxyMetricsCatalog`.
- `server/backend/endpoint/requests` dictionaries built by the pipeline are rebuilt again in `ProxyMetricsCatalog.BuildScopeValues` (duplicated aggregation).
- `GetValue` / `FirstNonEmpty` / `GetCorrelationKey` are duplicated in three files with divergent `GetCorrelationKey` precedence (reader: GUID→MID; base processor: MID→GUID).
- Request data is flattened to `"Key: value"` text then re-parsed by regex in the razor, despite a structured `RequestPhaseView` already existing.
- Endpoint/Path/User aggregation lives in `EventHubMonitorPage.razor` and overlaps store aggregation.
