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

Configure user profiles using these environment variables:

| Variable | Description | Default |
|----------|-------------|---------|
| **UseProfiles** | Enable user profile functionality | false |
| **UserConfigUrl** | URL or file path to fetch user configuration | file:config.json |
| **UserIDFieldName** | Header name used to look up user information | userId |
| **UserProfileHeader** | Header containing user profile information | X-UserProfile |
| **UserPriorityThreshold** | Threshold for user priority calculations (prevents greedy users) | 0.1 |

## User Profile Structure

Each user profile is a JSON object with the following structure:

```json
{
    "userId": "unique-user-identifier",
    "S7PPriorityKey": "priority-key-value",
    "Header1": "Custom header value",
    "Header2": "Another custom value",
    "async-config": enabled=true, blobcontainer=<user-specific-blob-container>, topic=<user-specific-servicebus-topic>
}
```

### Required Fields

- **userId**: Unique identifier for the user (must match the incoming request header)

### Optional Fields

- **S7PPriorityKey**: Maps to priority levels defined in `PriorityKeys` and `PriorityValues`
- **Custom Headers**: Any additional key-value pairs become headers applied to requests
- **async-blobname**: Azure Blob Storage container name for async processing
- **async-topic**: Azure Service Bus topic for async notifications
- **async-config**: a 3 tuple:  enabled=<true|false>, blobcontainer=<client blob container name>, topic=<client topic>

## Example Configuration File

Here's a complete example of a user profiles configuration file:

```json
[
    {
        "userId": "premium-user-123",
        "S7PPriorityKey": "12345",
        "Department": "Engineering",
        "Region": "US-East",
        "async-config": "enabled=true, blobcontainer=premium-data, topic=premium-status"
        "async-blobaccess-timeout": 3600
    },
    {
        "userId": "standard-user-456",
        "S7PPriorityKey": "234",
        "Department": "Marketing",
        "Region": "EU-West", 
        "async-blobname": "standard-data",
        "async-topic": "standard-status",
        "async-config": "enabled=true, blobcontainer=standard-data, topic=standard-status"
        "async-blobaccess-timeout": 1800
    },
    {
        "userId": "basic-user-789",
        "Department": "Support",
        "Region": "US-West",
        "async-config": "enabled=false"
    },
    {
        "userId": "suspended-user-999",
        "Status": "Suspended",
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

For users with async capabilities, additional fields control the behavior:

| Field | Description | Example |
|-------|-------------|---------|
| **async-config** | async processing parameters: enabled, containername, topic | enabled=true/false, containername=<name>, topic=<name> |
| **async-blobaccess-timeout** | Blob access timeout in seconds | 3600 |

### Async Request Example

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
