# Advanced Configuration

| Attribute | Value |
|-----------|-------|
| **Version** | 1.1 |
| **Last Updated** | 2026-05-21 |
| **Owner** | SimpleL7Proxy maintainers |
| **Review Cycle** | Quarterly |

## Summary

This document covers priority mapping, header validation, and per-user queue priority. Priority mapping uses configured defaults; header validation is inactive when no rules are set. Worker assignments and the active per-user threshold are captured at startup and require a restart to change.

> **TL;DR**
> - **Priority mapping:** map `PriorityKeyHeader` values through `PriorityKeys` and `PriorityValues`; `PriorityWorkers` sets worker preferences at startup.
> - **Header validation:** use JSON `ValidateHeaders` rules for inline or profile-backed allowlists; the legacy `SourceHeader=AllowlistHeader` form remains supported.
> - **Per-user queue priority:** `UserPriorityThreshold` controls a secondary queue preference based on each user's tracked share of active requests.

> [!NOTE]
> `PriorityWorkers` assignments and the active `UserPriorityThreshold` are not updated by a Warm refresh; restart the proxy for those changes to take effect. Other Warm settings take effect through the configured App Configuration refresh. See [App Configuration](../how-to/configure-app-configuration.md) and [Configuration](configuration.md).

---

## Scope & Applicability

**In scope:** Priority mapping, header validation, and per-user throttling configuration.
**Out of scope:** Backend host setup (see [BACKEND_HOSTS.md](backend-hosts.md)); load balancing (see [LOAD_BALANCING.md](load-balancing.md)); user profile structure (see [USER_PROFILES.md](user-profiles.md)).
**Dependencies:** [CONFIGURATION_SETTINGS.md](configuration.md), [AZURE_APP_CONFIGURATION.md](../how-to/configure-app-configuration.md).

---

## Table of Contents
- [Priority mapping](#priority-mapping)
- [Header Validation](#header-validation)
- [User throttling](#user-throttling)
- [Validation \& Compliance](#validation--compliance)
- [Version History](#version-history)

## Priority mapping

The proxy maps a request header value to an internal priority level, then assigns preferred priorities to workers at startup.

### How it works

1.  **Incoming Trigger**: The proxy looks for the header named by `PriorityKeyHeader` (default: `S7PPriorityKey`) in the request.
2.  **Mapping**: The value of that header is matched against `PriorityKeys` and mapped to a corresponding value in `PriorityValues`.
3.  **Worker preference**: `PriorityWorkers` assigns preferred priorities when workers start. A worker can process other priorities when its preferred priority is unavailable.

### Configuration Variables

| Variable | Description |
|----------|-------------|
| `PriorityKeyHeader` | The HTTP header name to inspect (e.g., `S7PPriorityKey` or `X-Priority-ID`). |
| `PriorityKeys` | A comma-separated list of expected values in the header. |
| `PriorityValues` | A comma-separated list of internal priority integers. Lower primary priorities are dispatched first. |
| `PriorityWorkers` | `PriorityLevel:WorkerCount` pairs that set worker preferences at startup. |

### Example

> **Keep `PriorityKeys` and `PriorityValues` the same length for the intended mapping. If their counts differ, the parser replaces `PriorityValues` with the default priority repeated for each key.**

1.  **Define the Header**:
    ```bash
    PriorityKeyHeader="X-Service-Tier"
    ```

2.  **Map Keys to Priorities**:
    *   "plat" -> Priority 1 (Highest)
    *   "gold" -> Priority 2 (Medium)
    *   Standard requests get `DefaultPriority` (default is 2, let's say we set standard to 3).

    ```bash
    PriorityKeys="plat,gold"
    PriorityValues="1,2"
    DefaultPriority=3
    ```

3.  **Allocate Workers**:
    *   5 workers prefer Priority 1 (Plat).
    *   3 workers prefer Priority 2 (Gold).
    *   These are startup preferences, not reservations; workers can process other priorities when their preference is unavailable.

    Format: `PriorityLevel:WorkerCount` tuples separated by commas.

    ```bash
    PriorityWorkers="1:5,2:3"
    ```

**Full Configuration:**
```bash
PriorityKeyHeader=X-Service-Tier
PriorityKeys=plat,gold
PriorityValues=1,2
PriorityWorkers=1:5,2:3
DefaultPriority=3
```

> [!TIP]
> **Troubleshooting:** If requests do not receive the expected priority, confirm the header value in the request exactly matches an entry in `PriorityKeys` (comparison is case-sensitive). Requests with no matching key receive `DefaultPriority`.

---

## Header validation

You can validate a source header against inline values, a profile-backed allowlist header, or both. Matching either source is sufficient.

### Format

Use JSON `HeaderValidationRule` entries. Each rule has a `headerName`, a `matchType` (`exact` or `prefixWildcard`), `allowedValues`, and an optional `allowedValuesHeader`.

The legacy `SourceHeader=AllowlistHeader` mapping remains supported and creates a mapped prefix-wildcard rule. The proxy compares header values case-insensitively; a trailing `*` in an allowed value matches a prefix.

The source header is automatically added to `RequiredHeaders`. When a rule uses a mapped allowlist header, that header is also added to `RequiredHeaders` and `DisallowedHeaders`.

### Example: Path-Based Access Control

> **Rule: A mapped allowlist header is required and stripped before forwarding. Inline-only rules do not add a disallowed header.**

The proxy automatically copies the request path into the `S7Path` header before validation. Combined with an `AllowedPaths` header from the user profile, you can restrict which URL paths a user is permitted to call.

**Environment variable:**
```bash
ValidateHeaders='[{"headerName":"S7Path","matchType":"prefixWildcard","allowedValues":["/api/*"],"allowedValuesHeader":"AllowedPaths"}]'
```

**User profile** (e.g., in Cosmos DB):
```json
{
  "userId": "client-123",
  "headers": {
    "AllowedPaths": "/api/delay,/api/values,/echo*"
  }
}
```

**Behavior:**
| Request Path | AllowedPaths | Result |
|---|---|---|
| `/api/delay` | `/api/delay,/api/values,/echo*` | ✅ Exact match |
| `/echo/resource?param1=x` | `/api/delay,/api/values,/echo*` | ✅ Prefix match on `/echo*` |
| `/api/other` | `/api/delay,/api/values,/echo*` | ❌ Rejected (417 Expectation Failed) |

If validation fails, the request is rejected with HTTP **417 Expectation Failed** and the message `Validation check failed for header: <SourceHeader>`.

> [!TIP]
> **Troubleshooting:** If legitimate requests return `417`, confirm the `AllowlistHeader` (e.g., `AllowedPaths`) is being injected by the user profile service and reaches the proxy with the correct comma-separated values. Set `LogAllRequestHeaders=true` temporarily to inspect what the proxy receives.

---

## User throttling

This setting influences secondary queue ordering using each user's tracked share of active requests; it does not cap or reject requests.

### `UserPriorityThreshold`

> **Rule: `UserPriorityThreshold` is a fraction (0.0–1.0) of tracked active requests. It affects secondary queue priority, not the primary mapped priority, and does not reject requests. The active threshold requires a restart to change.**

*   **Type**: Float (0.0 to 1.0).
*   **Default**: `0.1` (10% of total active requests)

**How it works**:
The proxy tracks each user's share of active requests. Users below the threshold receive a secondary queue-priority preference; crossing the threshold removes that preference. The request's primary priority tier remains unchanged.

**Example**:
With `UserPriorityThreshold=0.2` (20%):
If there are 100 active requests in the system and User A has 21, User A's new requests do not receive the below-threshold secondary preference. When their tracked share falls below 20%, new requests receive that preference again.

> [!TIP]
> **Troubleshooting:** If a specific user's requests are consistently slow, confirm they are not triggering the threshold. Set `LogAllRequestHeaders=true` and inspect proxy logs for the assigned priority.

---

## Validation & Compliance

| Check | Method | Expected Result |
|-------|--------|-----------------|
| Priority mapping active | Send request with `PriorityKeyHeader` value matching an entry in `PriorityKeys` | Request served at mapped priority; priority visible in proxy logs |
| Header validation active | Send request with `SourceHeader` value absent from `AllowlistHeader` | `417 Expectation Failed` with `X-S7P-Error: Validation check failed for header: {SourceHeader}` |
| User queue preference | Compare a user's tracked active-request share with `UserPriorityThreshold` | The below-threshold secondary preference applies only while the share is below the threshold; primary priority remains unchanged |
| Priority key/value counts differ | Load configuration | `PriorityValues` is filled with the default priority, repeated once per `PriorityKey` |

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 1.1 | 2026-05-21 | Added metadata, TL;DR, Summary, Scope & Applicability; added Rule: callouts before each example; added [!TIP] troubleshooting hints; added Validation & Compliance and Version History sections | SimpleL7Proxy maintainers |
| 1.0 | — | Initial version | SimpleL7Proxy maintainers |
