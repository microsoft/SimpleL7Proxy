# Run SimpleL7Proxy manual tests

SimpleL7Proxy manual tests start local nullservers, run the proxy with a test-specific configuration, and verify behavior through real HTTP requests. Run every command from the repository root in Bash on Linux or WSL.

## Requirements

Running a SimpleL7Proxy manual test requires:

- .NET 10 SDK
- Python 3
- `curl`
- `jq`
- Free ports `3000`, `3001`, `3002`, and `8000`
- Free port `9000` for test 11

Build SimpleL7Proxy once before running a test:

```bash
dotnet build src/SimpleL7Proxy/SimpleL7Proxy.csproj
```

> [!IMPORTANT]
> Stop SimpleL7Proxy before switching tests. Source the next setup script before restarting the proxy; the script clears settings from previous manual tests.

## Manual tests

Choose the test that covers the behavior you want to verify.

| Test | Verifies |
|---|---|
| 1 | Priority-group routing and acceptable priorities |
| 2 | Legacy path routing, prefix stripping, and route order |
| 3 | MultiPass retries, lifetime attempts, and requeue delay |
| 4 | Named routes, route-level modes, and attempt limits |
| 5 | User profiles and request or response header rules |
| 6 | TTL formats, attempt timeout, and response contracts |
| 7 | Priority queue order and queue-capacity rejection |
| 8 | Backend probes, latency routing, failover, and recovery |
| 9 | Inbound keys, App IDs, profiles, and suspended users |
| 10 | Streaming body integrity and token telemetry |
| 11 | .NET health sidecar status and stale-update detection |

## Run tests 1 through 10

Set `testnum` to the same test number in each terminal. The commands below run test 1.

### Terminal 1: Start the nullservers

The launcher starts every nullserver required by the selected test.

```bash
testnum=1
./test/ManualTests/start_nullservers.sh "$testnum"
```

Continue when the launcher reports that the nullservers are ready. Keep the terminal open.

### Terminal 2: Start SimpleL7Proxy

The selected setup script configures SimpleL7Proxy for the test:

```bash
testnum=1
source "test/ManualTests/test${testnum}_setup.sh"
dotnet run --project src/SimpleL7Proxy/SimpleL7Proxy.csproj --no-build --no-launch-profile
```

You must source the setup script. Running the script directly does not configure the proxy process.

### Terminal 3: Run the verifier

In a separate terminal, run the verifier:

```bash
testnum=1
"./test/ManualTests/test${testnum}_setup.sh" verify
```

`PASS:` means every check completed successfully. `FAIL:` identifies the first result that did not match the expected behavior.

## Run test 11

Manual test 11 requires the nullserver, health sidecar, SimpleL7Proxy, and verifier in four terminals.

### Terminal 1: Start the nullserver

```bash
./test/ManualTests/start_nullservers.sh 11
```

### Terminal 2: Start the health sidecar

```bash
HEALTHPROBE_PORT=9000 \
dotnet run --project src/HealthProbe/HealthProbe.csproj --no-launch-profile
```

### Terminal 3: Start SimpleL7Proxy

```bash
source test/ManualTests/test11_setup.sh
dotnet run --project src/SimpleL7Proxy/SimpleL7Proxy.csproj --no-build --no-launch-profile
```

### Terminal 4: Run the verifier

```bash
./test/ManualTests/test11_setup.sh verify
```

To verify stale-update detection, stop only SimpleL7Proxy and leave the nullserver and sidecar running. Then run:

```bash
./test/ManualTests/test11_setup.sh verify-stale
```

## Stop or switch tests

To stop a manual test, press `Ctrl+C` in each terminal started for the test.

To run another test, stop the current processes, source the next setup script in the proxy terminal, and restart SimpleL7Proxy.

## Test details

Each row lists one behavior verified by a manual-test script.

| Domain | Goal | Description | Test script |
|---|---|---|---|
| Routing | Route high-priority traffic | A request with `S7PPriorityKey: high` returns `200` from port `3000`. | [`test1_setup.sh`](test1_setup.sh) |
| Routing | Route medium-priority traffic | A request with `S7PPriorityKey: medium` returns `200` from port `3001`. | [`test1_setup.sh`](test1_setup.sh) |
| Routing | Route low-priority traffic | A request with `S7PPriorityKey: low` returns `200` from port `3002`. | [`test1_setup.sh`](test1_setup.sh) |
| Routing | Reject an ineligible priority | Priority `none` returns `503` with `Attempts: 0`. | [`test1_setup.sh`](test1_setup.sh) |
| Routing | Preserve priority-group attempt order | A failing high-priority request returns `500` after exactly three attempts in order `3000 -> 3001 -> 3002`. | [`test1_setup.sh`](test1_setup.sh) |
| Routing | Select the legacy `/api` route | `/api/success` returns `200` from port `3000`. | [`test2_setup.sh`](test2_setup.sh) |
| Routing | Select the legacy `/api2` route | `/api2/success` returns `200` from port `3001`. | [`test2_setup.sh`](test2_setup.sh) |
| Routing | Preserve `/api` failure order | `/api/500error` returns `500` after two attempts in order `3000 -> 3001`. | [`test2_setup.sh`](test2_setup.sh) |
| Routing | Preserve `/api2` failure order | `/api2/500error` returns `500` after two attempts in order `3001 -> 3000`. | [`test2_setup.sh`](test2_setup.sh) |
| Routing | Enforce legacy path segment boundaries | `/apix/success` does not match `/api` and returns `503` with zero attempts. | [`test2_setup.sh`](test2_setup.sh) |
| Routing | Reject an unmatched legacy path | `/success` returns `503` with zero attempts when no catch-all host exists. | [`test2_setup.sh`](test2_setup.sh) |
| Routing | Strip a legacy path prefix | A unique `/api/...` path returns `200`, and the backend counter increases for the path with `/api` removed. | [`test2_setup.sh`](test2_setup.sh) |
| Routing | Preserve a stripped-route query | Query parameters survive `/api` prefix stripping; the request returns `200` from port `3000` in one attempt. | [`test2_setup.sh`](test2_setup.sh) |
| Routing | Return promptly for an unmatched named route | `/success` returns `503` with `Attempts: 0` instead of stranding a worker. | [`test4_setup.sh`](test4_setup.sh) |
| Routing | Recover after an unmatched route | `/api/success` still returns `200` after the preceding unmatched request. | [`test4_setup.sh`](test4_setup.sh) |
| Routing | Honor `/api` declared host order | `/api/success` selects port `3000`. | [`test4_setup.sh`](test4_setup.sh) |
| Routing | Honor `/api2` declared host order | `/api2/success` selects port `3001`. | [`test4_setup.sh`](test4_setup.sh) |
| Routing | Select the longest named prefix | `/api/special/success` selects the more specific route and port `3002`. | [`test4_setup.sh`](test4_setup.sh) |
| Routing | Strip the longest named prefix | The `/api/special` route forwards `/success`, confirmed by the expected backend body. | [`test4_setup.sh`](test4_setup.sh) |
| Routing | Prevent fallback from an owned route | An ineligible request matching `/api/special` returns `503` with zero attempts instead of falling through to `/api`. | [`test4_setup.sh`](test4_setup.sh) |
| Routing | Preserve a named route prefix | A unique `/keep/...` request returns `200`, and the backend counter increases for the full unstripped path. | [`test4_setup.sh`](test4_setup.sh) |
| Routing | Enforce named-route segment boundaries | `/apix/success` does not match `/api` and returns `503` with zero attempts. | [`test4_setup.sh`](test4_setup.sh) |
| Retry and Requeue | Enforce single-cycle `MaxAttempts` | `/500error` returns `412` with `Attempts: 5`, `Lifetime-Attempts: 5`, and the maximum-attempt message. | [`test3_setup.sh`](test3_setup.sh) |
| Retry and Requeue | Cycle hosts in `MultiPass` | The five failed attempts follow `3000 -> 3001 -> 3000 -> 3001 -> 3000`. | [`test3_setup.sh`](test3_setup.sh) |
| Retry and Requeue | Enforce lifetime attempts across requeues | Repeated Retry-After cycles terminate at `Lifetime-Attempts: 5` instead of resetting the limit after each requeue. | [`test3_setup.sh`](test3_setup.sh) |
| Retry and Requeue | Report final-cycle attempts | The terminal requeue cycle reports `Attempts: 1` while retaining five lifetime attempts. | [`test3_setup.sh`](test3_setup.sh) |
| Retry and Requeue | Accumulate requeue delay | The final response contains a nonzero `Request-Requeue-Delay` totaling at least `400 ms`. | [`test3_setup.sh`](test3_setup.sh) |
| Retry and Requeue | Prevent calls beyond the lifetime limit | Backend request counters increase by exactly five; no sixth backend call occurs. | [`test3_setup.sh`](test3_setup.sh) |
| Retry and Requeue | Apply route `SinglePass` | `/api/500error` returns `500` after exactly three attempts. | [`test4_setup.sh`](test4_setup.sh) |
| Retry and Requeue | Preserve route `SinglePass` order | `/api/500error` attempts `3000 -> 3001 -> 3002`. | [`test4_setup.sh`](test4_setup.sh) |
| Retry and Requeue | Apply route `MultiPass` and `MaxAttempts` | `/api2/500error` returns `412` after four attempts with the route limit message. | [`test4_setup.sh`](test4_setup.sh) |
| Retry and Requeue | Preserve route `MultiPass` order | `/api2/500error` attempts `3001 -> 3000 -> 3001 -> 3000`. | [`test4_setup.sh`](test4_setup.sh) |
| Retry and Requeue | Override route mode with `SinglePass` | `S7P-Iterator: SinglePass` changes `/api2/500error` to a two-attempt `500` response. | [`test4_setup.sh`](test4_setup.sh) |
| Retry and Requeue | Ignore an invalid iterator override | Invalid `S7P-Iterator` falls back to `/api2` route `MultiPass` and returns `412` after four attempts. | [`test4_setup.sh`](test4_setup.sh) |
| Retry and Requeue | Override route mode with `MultiPass` | `S7P-Iterator: MultiPass` changes `/api/500error` to a `412` response after ten attempts. | [`test4_setup.sh`](test4_setup.sh) |
| Retry and Requeue | Prefer route attempt limit over global limit | The `/api` request-header override uses route `maxattempts=10` instead of global `MaxAttempts=7`. | [`test4_setup.sh`](test4_setup.sh) |
| Retry and Requeue | Inherit global route mode | `/inherit/500error` inherits global `SinglePass`, returns `500`, and attempts `3002 -> 3000`. | [`test4_setup.sh`](test4_setup.sh) |
| Profiles and Validation | Reject a missing profile identity | A request without `X-UserProfile` returns `403` and identifies the missing profile. | [`test5_setup.sh`](test5_setup.sh) |
| Profiles and Validation | Reject an unknown profile | `X-UserProfile: unknown-profile` returns `403` and names the unknown profile. | [`test5_setup.sh`](test5_setup.sh) |
| Profiles and Validation | Enforce an explicit required header | Omitting `X-Correlation-ID` returns `417` and names that header. | [`test5_setup.sh`](test5_setup.sh) |
| Profiles and Validation | Auto-require the validation source | Omitting `X-Requested-Model` returns `417` after `ValidateHeaders` adds it to required headers. | [`test5_setup.sh`](test5_setup.sh) |
| Profiles and Validation | Accept an exact profile allowlist match | Profile A accepts `X-Requested-Model: gpt-4o` and returns `200`. | [`test5_setup.sh`](test5_setup.sh) |
| Profiles and Validation | Prevent allowlist spoofing | A client-supplied `X-Allowed-Models` value is removed and replaced by the stored profile value, allowing the legitimate request. | [`test5_setup.sh`](test5_setup.sh) |
| Profiles and Validation | Route an enriched profile request | The accepted profile request reaches port `3000`. | [`test5_setup.sh`](test5_setup.sh) |
| Profiles and Validation | Add a header from a profile rule | The backend reflects `x-Request-Sequence: profile-rule-added`. | [`test5_setup.sh`](test5_setup.sh) |
| Profiles and Validation | Accept a wildcard profile allowlist match | Profile A accepts case-insensitive `GPT-4.1` through `gpt-4*` and returns `200`. | [`test5_setup.sh`](test5_setup.sh) |
| Profiles and Validation | Apply rules after wildcard validation | The wildcard-approved request also receives `x-Request-Sequence: profile-rule-added`. | [`test5_setup.sh`](test5_setup.sh) |
| Profiles and Validation | Keep allowlists profile-specific | Profile B rejects `gpt-4o` with `417` and a validation failure. | [`test5_setup.sh`](test5_setup.sh) |
| Profiles and Validation | Reject a value outside every allowlist | Profile A rejects `gpt-3.5` with `417` and a validation failure. | [`test5_setup.sh`](test5_setup.sh) |
| Profiles and Validation | Reject an unknown secured profile | `unknown-user` returns `403`, names the profile, and makes no backend call. | [`test9_setup.sh`](test9_setup.sh) |
| Profiles and Validation | Reject a suspended user | `suspended-user` must return `403`, mention suspension, and make no backend call. | [`test9_setup.sh`](test9_setup.sh) |
| Headers | Strip a configured request header | Client `x-S7PID` does not reach the backend; the reflected value is `N/A`. | [`test5_setup.sh`](test5_setup.sh) |
| Headers | Preserve an allowed response header | Backend `Random-Header: Random-Value` remains in the client response. | [`test5_setup.sh`](test5_setup.sh) |
| Headers | Strip a configured response header | Backend `x-Random-Header` is absent from the client response. | [`test5_setup.sh`](test5_setup.sh) |
| Headers | Emit queue duration | A successful response includes `Request-Queue-Duration`. | [`test6_setup.sh`](test6_setup.sh) |
| Headers | Emit process duration | A successful response includes `Request-Process-Duration`. | [`test6_setup.sh`](test6_setup.sh) |
| Headers | Emit total latency | A successful response includes `Total-Latency`. | [`test6_setup.sh`](test6_setup.sh) |
| Request Lifecycle | Reject malformed TTL | `S7PTTL: not-a-ttl` returns `400`, names the invalid format, and makes no backend call. | [`test6_setup.sh`](test6_setup.sh) |
| Request Lifecycle | Reject an already-expired TTL | `S7PTTL: 0` returns `412`, reports TTL expiry, and makes no backend call. | [`test6_setup.sh`](test6_setup.sh) |
| Request Lifecycle | Accept a decimal relative TTL | `S7PTTL: 1.5` permits the request and returns `200`. | [`test6_setup.sh`](test6_setup.sh) |
| Request Lifecycle | Accept an absolute Unix TTL | A future `+<UnixSeconds>` value permits the request and returns `200`. | [`test6_setup.sh`](test6_setup.sh) |
| Request Lifecycle | Accept an ISO 8601 TTL | A future UTC timestamp permits the request and returns `200`. | [`test6_setup.sh`](test6_setup.sh) |
| Request Lifecycle | Enforce per-attempt timeout | `S7PTimeout: 100` against a `500 ms` response returns `408` after one attempt. | [`test6_setup.sh`](test6_setup.sh) |
| Response Handling | Return a baseline success | `/success` returns `200` from port `3000` with one current and one lifetime attempt. | [`test6_setup.sh`](test6_setup.sh) |
| Response Handling | Pass through an acceptable `500` | With `500` in `AcceptableStatusCodes`, `/500error` must return the backend `500` body in one attempt from port `3000`. | [`test6_setup.sh`](test6_setup.sh) |
| Queuing | Hold the only general worker | The blocker reaches `/test-hold` and remains active until the verifier releases it. | [`test7_setup.sh`](test7_setup.sh) |
| Queuing | Observe queue depth one | The low-priority request is confirmed queued while the worker is held. | [`test7_setup.sh`](test7_setup.sh) |
| Queuing | Observe queue depth two | The medium-priority request increases the confirmed queue depth to two. | [`test7_setup.sh`](test7_setup.sh) |
| Queuing | Observe queue capacity | The high-priority request increases the confirmed queue depth to `MaxQueueLength=3`. | [`test7_setup.sh`](test7_setup.sh) |
| Queuing | Reject queue overflow | A fourth queued request returns `429` with `Queue is full`. | [`test7_setup.sh`](test7_setup.sh) |
| Queuing | Keep queued traffic off the backend | Before release, the backend arrival list contains only the blocker. | [`test7_setup.sh`](test7_setup.sh) |
| Queuing | Complete released requests | The blocker and all three queued requests return `200` after release. | [`test7_setup.sh`](test7_setup.sh) |
| Queuing | Dispatch by numeric priority | Backend arrival order after release is `blocker -> high -> medium -> low`. | [`test7_setup.sh`](test7_setup.sh) |
| Queuing | Never forward rejected overflow | The overflow request is absent from backend arrivals. | [`test7_setup.sh`](test7_setup.sh) |
| Backend Health | Suppress probes for direct mode | The direct backend reports zero `/health` requests. | [`test6_setup.sh`](test6_setup.sh) |
| Backend Health | Reach initial startup readiness | The proxy `/startup` endpoint reaches `200` with both probeable hosts active. | [`test8_setup.sh`](test8_setup.sh) |
| Backend Health | Prefer the lower probe latency | Latency mode initially selects faster port `3001`. | [`test8_setup.sh`](test8_setup.sh) |
| Backend Health | Remove a failed probe target | With `SuccessRate=100`, a failed port `3001` probe removes it and traffic moves to port `3000`. | [`test8_setup.sh`](test8_setup.sh) |
| Backend Health | Degrade readiness with no active hosts | `/readiness` reaches `503` after both hosts fail probes. | [`test8_setup.sh`](test8_setup.sh) |
| Backend Health | Degrade startup with no active hosts | `/startup` reaches `503` after both hosts fail probes. | [`test8_setup.sh`](test8_setup.sh) |
| Backend Health | Preserve process liveness | `/liveness` remains `200` while backend health is degraded. | [`test8_setup.sh`](test8_setup.sh) |
| Backend Health | Reject admission with no active hosts | A normal request returns `429` and names `No active hosts`. | [`test8_setup.sh`](test8_setup.sh) |
| Backend Health | Recover readiness | `/readiness` returns to `200` after successful probe observations replace failures. | [`test8_setup.sh`](test8_setup.sh) |
| Backend Health | Return a recovered host to routing | Port `3001` becomes selectable again after recovery. | [`test8_setup.sh`](test8_setup.sh) |
| Backend Health | Probe every probeable host | Both nullservers report at least one `/health` request. | [`test8_setup.sh`](test8_setup.sh) |
| Authentication and Security | Reject a missing inbound key | A request without `X-Test-Key` returns `403`, reports missing auth, and makes no backend call. | [`test9_setup.sh`](test9_setup.sh) |
| Authentication and Security | Reject an invalid inbound key | A wrong key returns `403`, reports authorization failure, and makes no backend call. | [`test9_setup.sh`](test9_setup.sh) |
| Authentication and Security | Reject an unlisted App ID | `denied-app` returns `403`, reports invalid App ID, and makes no backend call. | [`test9_setup.sh`](test9_setup.sh) |
| Authentication and Security | Accept the first configured key | `key-one` authorizes `active-user` with `allowed-app` and returns `200`. | [`test9_setup.sh`](test9_setup.sh) |
| Authentication and Security | Accept the second configured key | `key-two` authorizes `active-user` with `allowed-app` and returns `200`. | [`test9_setup.sh`](test9_setup.sh) |
| Streaming and Telemetry | Preserve fixed OpenAI response bytes | The fixed proxy response and direct backend response both return `200` with identical bodies. | [`test10_setup.sh`](test10_setup.sh) |
| Streaming and Telemetry | Extract fixed OpenAI prompt tokens | The fixed-response proxy event records `Usage.Prompt_Tokens: 41`. | [`test10_setup.sh`](test10_setup.sh) |
| Streaming and Telemetry | Extract fixed OpenAI completion tokens | The fixed-response proxy event records `Usage.Completion_Tokens: 512`. | [`test10_setup.sh`](test10_setup.sh) |
| Streaming and Telemetry | Extract fixed OpenAI total tokens | The fixed-response proxy event records `Usage.Total_Tokens: 553`. | [`test10_setup.sh`](test10_setup.sh) |
| Streaming and Telemetry | Preserve chunked OpenAI response bytes | The chunked proxy response and direct backend response both return `200` with identical decoded bodies. | [`test10_setup.sh`](test10_setup.sh) |
| Streaming and Telemetry | Extract chunked OpenAI usage | The chunked-response event records prompt `41`, completion `512`, and total `553`. | [`test10_setup.sh`](test10_setup.sh) |
| Streaming and Telemetry | Preserve multiline response bytes | The multiline proxy response and direct backend response both return `200` with identical bodies. | [`test10_setup.sh`](test10_setup.sh) |
| Streaming and Telemetry | Extract multiline input tokens | The multiline event records `Usage.Input_Tokens: 10`. | [`test10_setup.sh`](test10_setup.sh) |
| Streaming and Telemetry | Extract multiline output tokens | The multiline event records `Usage.Output_Tokens: 28`. | [`test10_setup.sh`](test10_setup.sh) |
| Streaming and Telemetry | Fall back for an unknown processor | `NotAProcessor` returns `200` through `DefaultStream` without changing the response body. | [`test10_setup.sh`](test10_setup.sh) |
| Health Probes | Receive healthy sidecar liveness | Active proxy status updates make sidecar `/liveness` return `200`. | [`test11_setup.sh`](test11_setup.sh) |
| Health Probes | Receive healthy sidecar readiness | Active proxy status updates make sidecar `/readiness` return `200`. | [`test11_setup.sh`](test11_setup.sh) |
| Health Probes | Receive healthy sidecar startup | Active proxy status updates make sidecar `/startup` return `200`. | [`test11_setup.sh`](test11_setup.sh) |
| Health Probes | Propagate degraded readiness | A failed backend probe makes sidecar `/readiness` return `503`. | [`test11_setup.sh`](test11_setup.sh) |
| Health Probes | Propagate degraded startup | A failed backend probe makes sidecar `/startup` return `503`. | [`test11_setup.sh`](test11_setup.sh) |
| Health Probes | Meet readiness failure threshold | Sidecar readiness returns `503` for the configured consecutive sample count, default `3`. | [`test11_setup.sh`](test11_setup.sh) |
| Health Probes | Meet startup failure threshold | Sidecar startup returns `503` for the configured consecutive sample count, default `30`. | [`test11_setup.sh`](test11_setup.sh) |
| Health Probes | Keep liveness healthy during backend failure | Sidecar liveness returns `200` for the configured consecutive sample count, default `3`. | [`test11_setup.sh`](test11_setup.sh) |
| Health Probes | Propagate readiness recovery | Sidecar `/readiness` returns to `200` after backend recovery. | [`test11_setup.sh`](test11_setup.sh) |
| Health Probes | Propagate startup recovery | Sidecar `/startup` returns to `200` after backend recovery. | [`test11_setup.sh`](test11_setup.sh) |
| Health Probes | Sustain recovered probe success | Recovered readiness and liveness remain `200` for their configured consecutive sample counts. | [`test11_setup.sh`](test11_setup.sh) |
| Health Probes | Detect stale liveness updates | In `verify-stale`, sidecar `/liveness` reaches `503` after proxy status updates stop. | [`test11_setup.sh`](test11_setup.sh) |
| Health Probes | Sustain stale liveness failures | Stale liveness remains `503` for the configured consecutive failure count. | [`test11_setup.sh`](test11_setup.sh) |
| Health Probes | Sustain stale readiness failures | Stale readiness remains `503` for the configured consecutive failure count. | [`test11_setup.sh`](test11_setup.sh) |
| Health Probes | Sustain stale startup failures | Stale startup remains `503` for the configured consecutive failure count. | [`test11_setup.sh`](test11_setup.sh) |
