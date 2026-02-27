# Advanced Configuration Guide

This document provides detailed explanations and examples for the more complex configuration scenarios in SimpleL7Proxy.

## Table of Contents
- [Priority Management](#priority-management)
- [Header Validation](#header-validation)
- [User Governance](#user-governance)

## Priority Management

SimpleL7Proxy allows you to map incoming request headers to internal priority levels and assign dedicated resources to those priorities.

### Understanding the Components

1.  **Incoming Trigger**: The proxy looks for a specific header (default: `S7PPriorityKey`) in the request.
2.  **Mapping**: The value of that header is matched against `PriorityKeys` and mapped to a corresponding value in `PriorityValues`.
3.  **Resource Allocation**: The internal priority level is matched against `PriorityWorkers` to determine how many dedicated threads handle that priority.

### Configuration Variables

| Variable | Usage |
| bound | --- |
| `PriorityKeyHeader` | The HTTP header name to inspect (e.g., `S7PPriorityKey` or `X-Priority-ID`). |
| `PriorityKeys` | A comma-separated list of expected values in the header. |
| `PriorityValues` | A comma-separated list of internal priority integers (lower number = higher priority typically, but depends on implementation. Default is usually lower = higher). |
| `PriorityWorkers` | A mapping string defining worker threads per priority level. |

### Example Scenario

You have three tiers of service: **Platinum** (Key: `plat`), **Gold** (Key: `gold`), and **Standard** (no key).

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

---

## Header Validation

You can enforce that a request header's value appears in a comma-separated allow-list stored in another header using `ValidateHeaders`. This is typically combined with **User Profiles**, where the allow-list header is injected from the profile.

### Format

A comma-separated list of `SourceHeader:AllowedValuesHeader` pairs.

*   **SourceHeader**: The header whose value is being validated (the "lookup").
*   **AllowedValuesHeader**: The header containing a comma-separated list of allowed values.

The proxy checks that the value of `SourceHeader` matches at least one entry in `AllowedValuesHeader`. Both headers must be present on the request (they are automatically added to `RequiredHeaders` at startup).

### Matching Rules

*   **Exact match** (case-insensitive): The lookup value must equal one of the allowed values.
*   **Wildcard prefix match**: If an allowed value ends with `*`, the lookup value only needs to *start with* the prefix. For example, `/echo*` matches `/echo`, `/echo/resource`, `/echo/resource?param1=sample1`, etc.

### Example: Path-Based Access Control

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

---

## User Governance

These settings control how the proxy manages resource usage per user to prevent noisy neighbor issues.

### User Priority Threshold (`UserPriorityThreshold`)

This setting prevents a single user from dominating the high-priority queues.

*   **Type**: Float (0.0 to 1.0) representing a percentage.
*   **Default**: `0.1` (10%)

**How it works**:
The proxy tracks the number of active requests per user. If a user's active requests exceed this threshold percentage of the total queue size (or total active requests), their subsequent requests are temporarily downgraded to a lower priority.

**Example**:
With `UserPriorityThreshold=0.2` (20%):
If there are 100 requests in the system, and User A has 21 active requests, User A's new requests will be deprioritized until their active count drops below 20.

