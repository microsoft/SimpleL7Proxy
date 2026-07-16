# Advanced Configuration

| Attribute | Value |
|-----------|-------|
| **Version** | 1.1 |
| **Last Updated** | 2026-05-21 |
| **Owner** | SimpleL7Proxy maintainers |
| **Review Cycle** | Quarterly |

## Summary

This document specifies the configuration for priority mapping, header validation, and per-user throttling. These three capabilities are inactive by default and MUST be explicitly configured. All settings in this document are **Warm** — changes propagate within ~30 s via Azure App Configuration without a restart.

> **TL;DR**
> - **Priority mapping:** set `PriorityKeyHeader`, `PriorityKeys`, `PriorityValues`, and `PriorityWorkers` as a matching set to route requests to dedicated worker threads.
> - **Header validation:** set `ValidateHeaders` as `SourceHeader:AllowlistHeader` pairs to enforce per-user allowlists injected from profiles.
> - **User throttling:** set `UserPriorityThreshold` (0.0–1.0) to deprioritize users who exceed that fraction of the total queue.

> [!NOTE]
> All three features in this document are **Warm** settings. See [AZURE_APP_CONFIGURATION.md](AZURE_APP_CONFIGURATION.md) for how to apply changes without a restart. For the full settings reference, see [CONFIGURATION_SETTINGS.md](CONFIGURATION_SETTINGS.md).

---

## Scope & Applicability

**In scope:** Priority mapping, header validation, and per-user throttling configuration.
**Out of scope:** Backend host setup (see [BACKEND_HOSTS.md](BACKEND_HOSTS.md)); load balancing (see [LOAD_BALANCING.md](LOAD_BALANCING.md)); user profile structure (see [USER_PROFILES.md](USER_PROFILES.md)).
**Dependencies:** [CONFIGURATION_SETTINGS.md](CONFIGURATION_SETTINGS.md), [AZURE_APP_CONFIGURATION.md](AZURE_APP_CONFIGURATION.md).

---

## Table of Contents
- [Priority Management](#priority-management)
- [Header Validation](#header-validation)
- [User Governance](#user-governance)
- [Validation \& Compliance](#validation--compliance)
- [Version History](#version-history)

## Priority mapping

The proxy maps a request header value to an internal priority level, then assigns dedicated worker threads to each level.

### How it works

1.  **Incoming Trigger**: The proxy looks for the header named by `PriorityKeyHeader` (default: `S7PPriorityKey`) in the request.
2.  **Mapping**: The value of that header is matched against `PriorityKeys` and mapped to a corresponding value in `PriorityValues`.
3.  **Resource Allocation**: The internal priority level is matched against `PriorityWorkers` to determine how many dedicated threads handle that priority.

### Configuration Variables

| Variable | Description |
|----------|-------------|
| `PriorityKeyHeader` | The HTTP header name to inspect (e.g., `S7PPriorityKey` or `X-Priority-ID`). |
| `PriorityKeys` | A comma-separated list of expected values in the header. |
| `PriorityValues` | A comma-separated list of internal priority integers (lower number = higher priority typically, but depends on implementation. Default is usually lower = higher). |
| `PriorityWorkers` | A mapping string defining worker threads per priority level. |

### Example

> **Rule: The count of entries in `PriorityKeys` MUST equal the count in `PriorityValues`. `PriorityWorkers` MUST reference only priority levels that appear in `PriorityValues`. A mismatch causes the proxy to reject the configuration at startup.**

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
    *   Priority 1 (Plat) gets 5 reserved workers.
    *   Priority 2 (Gold) gets 3 reserved workers.
    *   Priority 3 (Standard) gets remaining/shared.

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

You can enforce that a header's value appears in an allowlist stored in another header. This is often combined with **User Profiles**, where the allowlist header is injected from the profile.

### Format

A comma-separated list of `SourceHeader:AllowedValuesHeader` pairs.

*   **SourceHeader**: The header whose value is being validated (the "lookup").
*   **AllowedValuesHeader**: The header containing a comma-separated list of allowed values.

The proxy checks that the value of `SourceHeader` matches at least one entry in `AllowedValuesHeader`. Both headers must be present on the request (they are automatically added to `RequiredHeaders` at startup).

### Matching Rules

*   **Exact match** (case-insensitive): The lookup value must equal one of the allowed values.
*   **Wildcard prefix match**: If an allowed value ends with `*`, the lookup value only needs to *start with* the prefix. For example, `/echo*` matches `/echo`, `/echo/resource`, `/echo/resource?param1=sample1`, etc.

### Example: Path-Based Access Control

> **Rule: Both `SourceHeader` and `AllowlistHeader` MUST be present on every request. Setting `ValidateHeaders` automatically adds both to `RequiredHeaders` and adds `AllowlistHeader` to `DisallowedHeaders` — this side effect is mandatory and cannot be disabled.**

The proxy automatically copies the request path into the `S7Path` header before validation. Combined with an `AllowedPaths` header from the user profile, you can restrict which URL paths a user is permitted to call.

**Environment variable:**
```bash
ValidateHeaders="S7Path:AllowedPaths"
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

These settings limit how much of the queue a single user can occupy.

### `UserPriorityThreshold`

> **Rule: `UserPriorityThreshold` is a fraction (0.0–1.0), not a percentage. A value of `0.1` means 10% of the total active request count. Setting it to `0` disables per-user throttling entirely. Setting it to `1.0` means a user MUST own the entire active request pool before deprioritization activates.**

*   **Type**: Float (0.0 to 1.0).
*   **Default**: `0.1` (10% of total active requests)

**How it works**:
The proxy tracks the number of active requests per user. If a user's active requests exceed this fraction of the total active request count, their subsequent requests are downgraded to a lower priority until their share drops back below the threshold.

**Example**:
With `UserPriorityThreshold=0.2` (20%):
If there are 100 active requests in the system and User A has 21, User A's new requests are deprioritized until their count drops below 20.

> [!TIP]
> **Troubleshooting:** If a specific user's requests are consistently slow, confirm they are not triggering the threshold. Set `LogAllRequestHeaders=true` and inspect proxy logs for the assigned priority.

---

## Validation & Compliance

| Check | Method | Expected Result |
|-------|--------|-----------------|
| Priority mapping active | Send request with `PriorityKeyHeader` value matching an entry in `PriorityKeys` | Request served at mapped priority; priority visible in proxy logs |
| Header validation active | Send request with `SourceHeader` value absent from `AllowlistHeader` | `417 Expectation Failed` with `X-S7P-Error: Validation check failed for header: {SourceHeader}` |
| User throttling active | Send more than `UserPriorityThreshold` × total active requests from one user | Subsequent requests from that user receive `DefaultPriority` instead of the mapped priority |
| `PriorityKeys`/`PriorityValues` aligned | Proxy startup log | No configuration error; proxy starts successfully |

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 1.1 | 2026-05-21 | Added metadata, TL;DR, Summary, Scope & Applicability; added Rule: callouts before each example; added [!TIP] troubleshooting hints; added Validation & Compliance and Version History sections | SimpleL7Proxy maintainers |
| 1.0 | — | Initial version | SimpleL7Proxy maintainers |
