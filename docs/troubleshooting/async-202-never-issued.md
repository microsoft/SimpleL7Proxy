# Async Expected but 202 Never Issued

Purpose: Diagnose cases where clients expect async behavior but the proxy returns a normal synchronous response instead of `202 Accepted`.

> **TL;DR**
> 1. Confirm the request includes the configured async opt-in header (`AsyncClientRequestHeader`, default `S7PAsyncMode`).
> 2. Confirm async is enabled at all three gates: proxy (`AsyncModeEnabled=true`), user profile (`enabled=true` in async config), and request header.
> 3. Compare backend completion time to `AsyncTriggerTimeout`: if the backend responds faster than the trigger timeout, sync response is expected behavior.

---

## Runtime behavior

The proxy does not force all async-tagged requests to return `202`. It first starts processing synchronously, then upgrades to async only if processing crosses the trigger window.

Runtime decision path:

1. Request arrives.
2. Proxy validates async gates:
   - `AsyncModeEnabled=true` (system gate)
   - Request has opt-in header (default `S7PAsyncMode`)
   - User async profile exists and is enabled
3. If any gate fails, request stays synchronous.
4. If gates pass, proxy starts sync processing and waits up to `AsyncTriggerTimeout`.
5. If backend finishes before `AsyncTriggerTimeout`, request returns sync (200/4xx/5xx as applicable).
6. If processing exceeds `AsyncTriggerTimeout`, proxy returns `202` and continues in async pipeline.

---

## Diagnosis checklist

- Verify request header name and value:
  - Header name is `AsyncClientRequestHeader` (default `S7PAsyncMode`).
  - Header must be present on the failing request.
- Verify proxy-level async switch:
  - `AsyncModeEnabled=true`.
  - If using App Config, key is `Cold:Async:Enabled` and requires restart after change.
- Verify user profile async config:
  - `enabled=true`.
  - `containername` and `topic` are present.
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

# 3) Apply one targeted fix (example: lower trigger timeout to force async on slow path)
export AsyncTriggerTimeout=1000

# If using App Config instead of env vars:
# Warm:Async:TriggerTimeout = 1000
# Then bump Warm:Sentinel (same label as active environment)

# 4) Verify
curl -i https://<proxy-host>/<path> -H "S7PAsyncMode: true"
# Expect: HTTP/1.1 202 Accepted (for requests exceeding new trigger timeout)
```

---

## Common operator pitfalls

- `AsyncModeEnabled` changed in App Config but proxy not restarted (Cold key).
- Client sends `AsyncMode: true` while server expects `S7PAsyncMode` (header mismatch).
- Profile async config exists but `enabled=false`.
- Test backend is too fast; request naturally completes before trigger timeout.

---

## Related

- [async-requests.md](async-requests.md) — broader async troubleshooting
- [../AsyncOperation.md](../AsyncOperation.md) — async config and flow reference
- [../TIMEOUTS.md](../TIMEOUTS.md) — timeout interactions
- [app-configuration.md](app-configuration.md) — App Config label/key troubleshooting
