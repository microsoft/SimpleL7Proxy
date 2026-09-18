# User Profiles

User profiles let you customize request handling on a per-user basis. The proxy can validate incoming requests against user configurations and apply specific settings like priority levels, headers, and async processing permissions.

> **TL;DR**
> - **Match the caller's profile-header value to an ID in the profile JSON.**
> - Use profiles for caller-specific priority, async permissions, and injected headers.
> - A supplied unknown or suspended profile ID is rejected with 403 when profiles are enabled.

## Overview

User profiles are stored in a JSON file that the proxy reads periodically (every hour by default). This file can be fetched from a URL or loaded from a local file location, depending on your configuration. The profiles enable you to:

- **Validate incoming requests** against allowed users
- **Set user-specific priority levels** for request processing
- **Configure async processing permissions** per user
- **Apply custom headers** based on user identity
- **Control access** to specific features

## Configuration

Configure user profiles using these environment variables. For detailed variable definitions, see [Environment Variables](environment-variables.md).

| Variable | Description | Default |
|----------|-------------|---------|
| **UseProfiles** | Enable user profile functionality | false |
| **UserConfigUrl** | URL or file path to fetch user configuration | `""` (not set) |
| **SuspendedUserConfigUrl** | URL or file path to fetch list of explicitly suspended users | `""` (not set) |
| **UserIDFieldName** | JSON field name in the user profile config file used as the unique user identifier | userId |
| **UserProfileHeader** | Incoming header containing the ID used to look up a profile, not serialized JSON | X-UserProfile |
| **UniqueUserHeaders** | Header values combined for queue accounting; separate from profile lookup | X-UserID |
| **UserConfigRequired** | Reject a missing profile header when profiles are enabled | false |
| **UserPriorityThreshold** | Threshold (0.0-1.0) for user priority calculations. If a user's active requests exceed this ratio of the total queue, their requests are deprioritized. | 0.1 |

## User Suspension

**Use `SuspendedUserConfigUrl` to block a caller independently of their profile or priority.** A user whose ID appears in the loaded suspension list receives **403 Forbidden** before backend processing, regardless of their profile content.

```text
Caller ID is in the loaded suspension list -> 403 Forbidden
Caller ID is absent from the list -> continue normal validation
Caller has a lower priority -> scheduling changes, not suspension
```

> [!TIP]
> **Caller still accepted?** Check that the suspension source loaded successfully and contains the exact caller identity used for profile lookup. Lowering `S7PPriorityKey` is not a substitute for suspension.

## User Profile Structure

**`UserConfigUrl` supplies a JSON array; `UserIDFieldName` chooses the ID field in each object.** Use this when callers need different priorities, async permissions, or injected headers. The incoming header named by `UserProfileHeader` supplies the ID to match; `UniqueUserHeaders` is separate queue-accounting configuration.

```text
UserIDFieldName=userId; UserProfileHeader=X-UserProfile
Request header: X-UserProfile: alice
Profile record: { "userId": "alice", "Department": "Engineering" }
```

| Step | Example | Result |
|---|---|---|
| Read source | JSON record has `userId=alice` | Cache the record under `alice` |
| Match caller | Request has `X-UserProfile: alice` | Select that record |
| Enrich request | Record has `Department=Engineering` | Inject `Department: Engineering` |

> [!TIP]
> **Unexpected 403?** Compare the incoming `UserProfileHeader` value with the JSON ID value. Renaming `UserIDFieldName` changes the source JSON field, not the incoming header name.

```json
[
  {
    "userId": "unique-user-identifier",
    "S7PPriorityKey": "priority-key-value",
    "Header1": "Custom header value",
    "async-config": "enabled=true, containername=my-container, topic=my-topic, timeout=3600, generatesas=false"
  }
]
```

### Fields Description

| Field | Requirement | Description |
|---|---|---|
| **userId** | **Required** | Default JSON ID field, configurable through `UserIDFieldName`. Its value must match the incoming header named by `UserProfileHeader`. |
| **S7PPriorityKey** | Optional | A key corresponding to a priority level defined in `PriorityKeys`. If present, assigns this priority to the user's requests. |
| **async-config** | Optional | A comma-separated string `key=value` enabling async processing. Requires: `enabled`, `containername`, and `topic`. Optional: `timeout` and `generatesas`. `generatesas` is retained but current blob responses contain base URIs without generated SAS tokens. |
| **[CustomHeader]** | Optional | Any other key-value pair will be injected as a specific HTTP header into the proxied request. |

## Example Configuration File

Here is a syntactically correct example of a configuration file:

```json
[
    {
        "userId": "premium-user-123",
        "S7PPriorityKey": "12345",
        "Department": "Engineering",
        "Region": "US-East",
        "async-config": "enabled=true, containername=premium-data, topic=premium-status, timeout=3600"
    },
    {
        "userId": "standard-user-456",
        "S7PPriorityKey": "234",
        "Department": "Marketing",
        "Region": "EU-West"
    },
    {
        "userId": "basic-user-789",
        "Department": "Support",
        "Region": "US-West",
        "async-config": "enabled=false"
    }
]
```

## Request Validation Process

When a request arrives, the proxy follows this validation process:

1. **Extract Profile ID**: Read the incoming header named by `UserProfileHeader` when `UseProfiles=true`.
2. **Profile Lookup**: Reject suspended or unknown supplied IDs with 403; otherwise select the cached record.
3. **Apply Profile**: If found, apply the user's configuration to the request
4. **Missing Header**: Reject with 403 when `UserConfigRequired=true`; otherwise continue without profile enrichment.

### Validation Scenarios

#### Valid User with Profile
```bash
curl -H "X-UserProfile: premium-user-123" \
     -H "Content-Type: application/json" \
     http://localhost:8000/api/data
```
- Profile found → Apply premium user settings
- Request processed with high priority
- Async processing enabled if requested

#### User Without Profile
```bash
curl -H "X-UserProfile: unknown-user" \
     http://localhost:8000/api/data
```
- With `UseProfiles=true`, no matching profile returns **403 Forbidden**.
- The request does not reach a backend.

#### Missing User ID Header
```bash
curl http://localhost:8000/api/data
```
- With `UseProfiles=true` and `UserConfigRequired=true`, the missing header returns **403 Forbidden**.
- Otherwise the request continues without profile enrichment; other validation still applies.

## Async Processing Configuration

To enable async processing for a user, their profile must contain the `async-config` field. This tells the proxy where to store the request state for that specific user.

### Example Profile Entry
```json
"async-config": "enabled=true, containername=my-data, topic=my-notifications"
```

### Components
*   **enabled**: `true` to allow async for this user.
*   **containername**: The Azure Blob Storage container name where request payloads will be stored.
*   **topic**: The Azure Service Bus topic name where completion notifications will be sent.

### Async Request Example

A client requests an async operation by adding the `S7PAsyncMode` header (or the value of `AsyncClientRequestHeader` if overridden):

```bash
curl -H "X-UserProfile: premium-user-123" \
     -H "S7PAsyncMode: true" \
     -H "Content-Type: application/json" \
     -d '{"query": "process this async"}' \
     http://localhost:8000/api/long-running-task
```

Response:
```json
{
    "status": "accepted",
    "requestId": "S7P-12345-67890",
    "blobUrl": "https://storage.blob.core.windows.net/premium-data/results/12345",
    "notificationTopic": "premium-status"
}
```

<details>
<summary>Security Considerations</summary>

### Profile File Security
- Store profile files securely with appropriate access controls
- Use HTTPS when fetching profiles from URLs
- Consider encrypting sensitive profile data

### User Validation
- Validate user IDs against your authentication system
- Implement rate limiting per user
- Monitor for suspicious user activity

### Async Processing Security
- Ensure blob containers have proper access controls
- Protect returned base blob URIs with private networking and Azure RBAC
- Validate Service Bus topic permissions

</details>

<details>
<summary>Troubleshooting</summary>

### Common Issues

**Profiles not loading:**
- Check the `UserConfigUrl` path/URL
- Verify file permissions
- Confirm JSON syntax is valid

**User not found:**
- Verify the header named by `UserProfileHeader` is present
- Check that the JSON field named by `UserIDFieldName` matches that header's value
- Confirm profiles file has been reloaded (check timestamp)

**Async not working:**
- Verify `async-config: true` in present with all three values: enabled, containername and topic.  Verify access.
- Check Azure Storage and Service Bus connections
- Confirm `AsyncModeEnabled=true` at service level

### Debugging

Enable debug logging to trace profile loading and user lookup:

```bash
export LogAllRequestHeaders=true
export LogProbes=true
```

Add debug header to requests:
```bash
curl -H "S7PDEBUG: true" -H "X-UserProfile: test-user" http://localhost:8000/api/test
```

</details>

<details>
<summary>Profile Management</summary>

### Updating Profiles
- Profiles are reloaded every hour automatically
- Update the source file/URL to modify user configurations
- Changes take effect on the next reload cycle

### Monitoring Profile Usage
- Monitor Application Insights for user-specific metrics
- Track priority queue usage by user
- Review async processing patterns

### Best Practices
- Keep profile files under version control
- Test profile changes in development first
- Monitor resource usage per user
- Implement user quotas to prevent abuse

</details>