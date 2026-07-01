# backendLog Activity Log Reference

Reference for every activity log entry emitted by [APIM-Policy/v2.3.0/Priority-with-retry.xml](../APIM-Policy/v2.3.0/Priority-with-retry.xml).

Each entry is a JSON object with two fields: `Elapsed` (string, `context.Elapsed.TotalSeconds` formatted `F3`) and `message`. Entries are serialized into the `backendLog` response header as `{Elapsed}s {message}` joined by ` | `, optionally prefixed by `DbgStr`, and truncated to 4000 characters.

Each goal below maps to one `set-variable` call in the policy (matching the `// Goal` comment in the XML). A goal may emit different messages depending on the branch taken.

| Message format | Trigger |
|----------------|---------|
| **Request Start** | |
| `Begin` | Always, once per request when `activityLog` is initialized. |
| **Error Scenario** | |
| `Invalid backend index: {backendIndex}` | `backendIndex` out of range **and** `backendCallCounter > 0`. Returns scenario 4 (unknown). |
| `THROTTLED: ({label} - MM:SS, ...)` or `THROTTLED: (none)` | `backendIndex` out of range **and** `backendCallCounter == 0`. Lists throttling priority backends. Returns scenario 4. |
| `Throttling [{backendIndex}] by 10s, likely timeout Elapsed: {deltaSeconds:F1}s Timeout: {requestTimeout}s` | Call did not complete and elapsed ≥ 90% of backend timeout. Sets `isThrottling=true`, `retryAfter=now+10s`. Returns scenario 2 (timeout). |
| `Throttling [{backendIndex}] by 10s due to concurrency limit` | Call did not complete and `wasLimited==true`. Sets `isThrottling=true`, `retryAfter=now+10s`. Returns scenario 3 (concurrency limit). |
| `Error status {lastStatusCode} after {deltaSeconds:F1}s` | Call did not complete, not a timeout, not concurrency-limited. Returns scenario 4 (unknown). |
| **Cycle Status** | |
| `RETRIES LEFT: {exhausted\|N} CYCLE: {PolicyCycleCounter} Unthrottled Backends: {count}` | Always, once per retry cycle. `exhausted` when `RetryCount == 0`. PTU excluded from count when `contextWindowExceeded`. |
| **Backend Selected** | |
| `Using {label} URL: {url} LIMIT: {shouldLimit}` | `backendIndex > -1` and `RetryCount > 0`. `LIMIT` is `off`/`low`/`medium`/`high`. |
| **Response Classified** | |
| `StatusCode: {statusCode\|N/A} - {Temp Error\|Perm Error\|Success}` | After `forward-request`. Temp = 400/408/429/≥500; Perm = other 4xx; else Success. |
| **No Backend** | |
| `NO BACKENDS: none configured for priority {RequestPriority}` | `PriBackendIndxs` is empty. |
| `NO BACKENDS: retries exhausted` | `RetryCount <= 0`. |
| `NO BACKENDS: all throttled ({label} - MM:SS, ...)` or `... (none)` | All priority backends throttling (PTU skipped if `contextWindowExceeded`). |
| **Backend Throttled** | |
| `THROTTLED: {label} Retry-After: MM:SS` | Temp error, or incomplete with 400/408/429/≥500. Reads `retry-after` header (+2s) or `defaultRetryAfter`; sets `isThrottling=true` and max `retryAfter`. |
| **Retry Decision** | |
| `CALL SUCCESSFUL` | `callCompleted` and response status 200. |
| `REQUEUE: true BACKENDS: [{i}]={label} Throttling={bool} RetryAfter={sec:F1}s ...` | `ShouldRequeue == true`. Repeated per priority backend. |
| `CALL INCOMPLETE, Unthrottled Backends: {count}` | Neither success nor requeue. |
| **Error Handler** | |
| `ON ERROR HANDLER INVOKED-  callCompleted: {bool} isPermError: {bool} isTempError: {bool} StatusCode: {n}` | Any unhandled exception routes to `on-error`. Sets `responseObject` status/reason. |

## Notes

- **`MM:SS`** — remaining retry-after via `string.Format("{0:00}:{1:00}", sec/60, sec%60)`, floored at 0.
- **Throttle lists** — comma-separated `label - MM:SS`; literal `none` when empty.
- **`{label}`** — backend label (e.g. `PTU`, `PAYGO`); PTU entries are skipped in unthrottled/throttle counts when `contextWindowExceeded` is set.
- **Scenarios 0 (success) and 1 (completed error)** in `ErrorScenario` add no entry.
- **`lastPolicyError`** — exceptions in `backendIndex`/`shouldLimit` write to `lastPolicyError` (surfaced via `X-Policy-LastError`), not to `activityLog`.
