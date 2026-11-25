# Async Expected but 202 Never Issued

Purpose: Diagnose cases where clients expect async behavior but the proxy returns a normal synchronous response instead of `202 Accepted`.

> **TL;DR**
> 1. Confirm the request includes the configured async opt-in header (`AsyncClientRequestHeader`, default `S7PAsyncMode`).
> 2. Confirm async is enabled, a blob writer is available, the selected profile has valid enabled async settings, and the request header parses as `true`.
> 3. Compare backend completion time to `AsyncTriggerTimeout`; response timing and blob queue pressure also affect whether the request upgrades.

---

## Runtime behavior

The proxy does not force all async-tagged requests to return `202`. It first starts processing synchronously, then may upgrade an eligible request after the trigger window if blob queue pressure allows.

Runtime decision path:

1. Request arrives.
2. Proxy validates async gates:
   - `AsyncModeEnabled=true` (system gate)
   - A blob writer is available.
   - Request opt-in header (default `S7PAsyncMode`) parses as `true`.
   - Selected user profile has valid enabled async settings.
3. If any eligibility condition fails, request stays synchronous.
4. If gates pass, proxy starts sync processing and waits up to `AsyncTriggerTimeout`.
5. If backend finishes before `AsyncTriggerTimeout`, request returns sync (200/4xx/5xx as applicable).
6. If processing exceeds `AsyncTriggerTimeout` and blob queue pressure allows the upgrade, proxy returns `202` and continues in async pipeline.

---

## Diagnosis checklist

- Verify request header name and value:
  - Header name is `AsyncClientRequestHeader` (default `S7PAsyncMode`).
  - Header must be present and parse as `true` on the failing request.
- Verify proxy-level async switch:
  - `AsyncModeEnabled=true`.
  - If using App Config, key is `Cold:Async:Enabled` and requires restart after change.
- Verify user profile async config:
  - The profile's configured async field is valid and `enabled=true`.
  - `containername` and `topic` are present.
- Verify the blob writer is configured and available; check blob queue pressure if all other conditions pass.
- Verify trigger behavior:
  - `AsyncTriggerTimeout` is set as expected.
  - Backend latency is actually above this threshold for the request path being tested.
- Verify no config drift:
  - `AsyncClientRequestHeader` on server matches client header name exactly.
  - In App Config, `AZURE_APPCONFIG_LABEL` matches the label containing your async keys.

---

## Canonical example (reproduce -> inspect -> fix -> verify)

```bash
# 1) Reproduce (client expects async)
curl -i https://<proxy-host>/<path> -H "S7PAsyncMode: true"

# 2) Inspect key runtime settings (example env inspection)
echo $AsyncModeEnabled
echo $AsyncClientRequestHeader
echo $AsyncTriggerTimeout

# 3) Apply one targeted fix (example: lower trigger timeout for eligible slow requests)
export AsyncTriggerTimeout=1000

# If using App Config instead of env vars:
# Warm:Async:TriggerTimeout = 1000
# Then bump Warm:Sentinel (same label as active environment)

# 4) Verify
curl -i https://<proxy-host>/<path> -H "S7PAsyncMode: true"
# Expect: HTTP/1.1 202 Accepted when the request exceeds the new trigger and queue conditions allow upgrade
```

---

## Common operator pitfalls

- `AsyncModeEnabled` changed in App Config but proxy not restarted (Cold key).
- Client sends `AsyncMode: true` while server expects `S7PAsyncMode` (header mismatch).
- Profile async config exists but `enabled=false`.
- Test backend is too fast; request naturally completes before trigger timeout.
- Blob queue pressure prevents an otherwise eligible request from upgrading.

---

## Related

- [async-requests.md](async-requests.md) — broader async troubleshooting
- [../AsyncOperation.md](../concepts/async-processing.md) — async config and flow reference
- [../TIMEOUTS.md](../reference/timeouts.md) — timeout interactions
- [app-configuration.md](app-configuration.md) — App Config label/key troubleshooting
