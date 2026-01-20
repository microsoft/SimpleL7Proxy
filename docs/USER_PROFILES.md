# User Profiles

User profiles provide a powerful way to customize request handling on a per-user basis. The proxy can validate incoming requests against user configurations and apply specific settings like priority levels, headers, and async processing permissions.

## Overview

User profiles are stored in a JSON file that the proxy reads periodically (every hour by default). This file can be fetched from a URL or loaded from a local file location, depending on your configuration. The profiles enable you to:

- **Validate incoming requests** against allowed users
- **Set user-specific priority levels** for request processing
- **Configure async processing permissions** per user
- **Apply custom headers** based on user identity
- **Control access** to specific features

## Configuration

Configure user profiles using these environment variables. For detailed variable definitions, see [Environment Variables](ENVIRONMENT_VARIABLES.md).

| Variable | Description | Default |
|----------|-------------|---------|
| **UseProfiles** | Enable user profile functionality | false |
| **UserConfigUrl** | URL or file path to fetch user configuration | file:config.json |
| **SuspendedUserConfigUrl** | URL or file path to fetch list of explicitly suspended users | file:config.json |
| **UserIDFieldName** | Header name used to look up user information | userId |
| **UserProfileHeader** | Header containing serialized user profile information for downstream services | X-UserProfile |
| **UserPriorityThreshold** | Threshold (0.0-1.0) for user priority calculations. If a user's active requests exceed this ratio of the total queue, their requests are deprioritized. | 0.1 |

## User Suspension

There are two ways to suspend or block a user:

1.  **SuspendedUserConfigUrl**: Configure a separate file listing suspended User IDs. If a user is found in this list, they are rejected immediately (HTTP 403).
2.  **Profile Status**: (Implementation specific) If logic is strictly checking the profile content, simply removing the profile or setting `S7PPriorityKey` to a low-priority value can limit access. *Note: Strict suspension usually relies on the `SuspendedUserConfigUrl`.*

## User Profile Structure

The user profile configuration source (URL or file) must return a **JSON Array** of user objects.

```json
[
  {
    "userId": "unique-user-identifier",
    "S7PPriorityKey": "priority-key-value",
    "Header1": "Custom header value",
    "async-config": "enabled=true, containername=my-container, topic=my-topic, timeout=3600"
  }
]
```

### Fields Description

| Field | Requirement | Description |
|---|---|---|
| **userId** | **Required** | Unique identifier for the user. Must match the value extracted from the header configured in `UserIDFieldName`. |
| **S7PPriorityKey** | Optional | A key corresponding to a priority level defined in `PriorityKeys`. If present, assigns this priority to the user's requests. |
| **async-config** | Optional | A comma-separated string `key=value` enabling async processing. Requires: `enabled`, `containername`, and `topic`. Optional: `timeout` (seconds for SAS token). |
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

1. **Extract User ID**: Look for the user identifier in the configured header (`UserIDFieldName`)
2. **Profile Lookup**: Search the loaded profiles for a matching `userId`
3. **Apply Profile**: If found, apply the user's configuration to the request
4. **Default Handling**: If no profile exists, use default proxy settings

### Validation Scenarios

#### Valid User with Profile
```bash
curl -H "userId: premium-user-123" \
     -H "Content-Type: application/json" \
     http://localhost:8000/api/data
```
- Profile found → Apply premium user settings
- Request processed with high priority
- Async processing enabled if requested

#### User Without Profile
```bash
curl -H "userId: unknown-user" \
     http://localhost:8000/api/data
```
- No profile found → Use default settings
- Request processed with default priority
- Standard processing rules apply

#### Missing User ID Header
```bash
curl http://localhost:8000/api/data
```
- No user identification → Anonymous processing
- Default proxy behavior
- Limited feature access

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

A client requests an async operation by adding the `AsyncMode` header (or configured equivalent):

```bash
curl -H "userId: premium-user-123" \
     -H "AsyncMode: true" \
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

## Security Considerations

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
- Use time-limited SAS tokens for blob access
- Validate Service Bus topic permissions

## Troubleshooting

### Common Issues

**Profiles not loading:**
- Check the `UserConfigUrl` path/URL
- Verify file permissions
- Confirm JSON syntax is valid

**User not found:**
- Verify the `UserIDFieldName` header is present
- Check that `userId` in profile matches request header
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
curl -H "S7PDEBUG: true" -H "userId: test-user" http://localhost:8000/api/test
```

## Profile Management

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
