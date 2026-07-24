# Getting 400 Bad Request (Invalid TTL)

Purpose: Diagnose and fix `400 Bad Request` responses caused by invalid TTL header values.

> **TL;DR**
> 1. `400 InvalidTTL` happens when the TTL header cannot be parsed.
> 2. Use one supported format only: relative seconds (`300`), absolute unix seconds (`+1735689600`), or a parseable datetime.
> 3. If clients cannot guarantee valid TTL formatting, remove the header and rely on `DefaultTTLSecs`.

---

## Runtime behavior

When the request enters the proxy queue, the proxy computes expiration in `CalculateExpiration(...)` from the configured TTL header (default `S7PTTL`).

Parsing order:

1. If header starts with `+` and the rest is an integer, it is treated as absolute unix epoch seconds.
2. Else if it parses as a float, it is treated as relative seconds from enqueue time.
3. Else if it parses as datetime, it is converted to UTC and used directly.
4. Else the proxy throws `InvalidTTL` and returns `400`.

If the header is missing or empty, proxy uses `DefaultTTLSecs` (or fallback default timeout path) and does not return `400`.

---

## Diagnosis checklist

- Confirm response is proxy-originated:
  - Status code is `400`.
  - Message includes `Invalid TTL format` or `InvalidTTL`.
- Confirm header name:
  - Check configured `TTLHeader` (default `S7PTTL`).
  - Verify client is setting that exact header name.
- Inspect actual header value sent by client:
  - Reject values with unit suffixes like `300s` unless your datetime parser format explicitly supports it.
  - Reject free text values such as `five-minutes`.
- Validate value against supported formats:
  - Relative seconds: `300` or `2.5`
  - Absolute epoch seconds: `+1735689600`
  - Date/time: `2026-04-29T10:30:00Z`
- If value origin is downstream middleware or APIM policy, inspect transformation step for type/format drift.

---

## Canonical example (reproduce -> inspect -> fix -> verify)

```bash
# 1) Reproduce a failure
curl -i https://<proxy-host>/<path> -H "S7PTTL: 300s"
# Expect: HTTP/1.1 400 Bad Request

# 2) Inspect by sending a known-valid TTL format
curl -i https://<proxy-host>/<path> -H "S7PTTL: 300"

# 3) Apply one targeted fix (client/APIM policy)
# Change TTL value generation from "300s" to "300"

# 4) Verify
curl -i https://<proxy-host>/<path> -H "S7PTTL: 300"
# Expect: request is accepted and processed (not 400 InvalidTTL)
```

---

## Common operator pitfalls

- Sending `S7PTTL` with suffixes (`ms`, `sec`, `s`) from policy templates.
- Setting a custom `TTLHeader` in config but clients still sending `S7PTTL`.
- Treating `+300` as relative; in proxy logic `+...` is absolute unix seconds.
- Timezone ambiguity in non-UTC datetime strings.

---

## Related

- [../RESPONSE_CODES.md](../reference/headers-and-status-codes.md) — `400 InvalidTTL` behavior
- [requests-412.md](requests-412.md) — TTL expired after successful parsing
- [../TIMEOUTS.md](../reference/timeouts.md) — TTL and timeout interactions
- [../REQUEST_VALIDATION.md](../how-to/configure-security.md) — other request validation failures
