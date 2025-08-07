# Async Operation Configuration

This document describes how to configure the proxy for asynchronous operation mode. When enabled, the proxy can handle requests asynchronously, providing status updates via Azure Service Bus and storing request/response data in Azure Blob Storage.

## Overview

To enable async operation, the proxy requires:

1. **Async Mode** - Enable the async processing feature
2. **Azure Service Bus** - For sending status notifications to clients
3. **Azure Blob Storage** - For storing request and response data
4. **Request Headers** - Each request must include appropriate headers to enable async processing

All configuration is done via environment variables.

## 1. Enable Async Mode

Set the following environment variable to enable async processing:

```bash
AsyncModeEnabled=true
AsyncTimeout=<milliseconds for request timeout>
AsyncTriggerTimeout=<milliseconds before async is enabled for the request>
AsyncSBStatusWorkers=5
```

The **AsyncTimeout** parameter defaults to 30 minutes and represents the amount of time an async request is allowed to run for.  Similar to **Timeout** in operation, this parameter controls when the proxy server will abandon a request that has been upgraded to Async.

The **AsyncTriggerTimeout** parameter enables when a request becomes async.  The idea is that fast runnung requests will perform without change.  After this time occurs, the request is converted to async and a response is sent to the caller with details on how to access the data when processing is complete.

The **AsyncSBStatusWorkers** parameter controls the number of workers that can feed data to the service bus.  The default value is 5.  If your service is running under high load, it may make sense to increase this number as the number because the service will attempt to drain all of the in-memory events during a shutdown or resize.

## 2. Azure Service Bus Configuration

Azure Service Bus is used to send real-time status updates to client applications as requests are processed. Listening to this queue will tell you when your async message has completed processing. There are two authentication methods available:

### Option A: Connection String Authentication

For development or when using Service Bus access keys:

```bash
AsyncSBConnectionString=<service-bus-connection-string>
```

### Option B: Managed Identity Authentication (Recommended)

For production environments, use managed identity for enhanced security:

```bash
AsyncSBUseMI=true
AsyncSBNamespace=<fully-qualified-namespace>
```

**Requirements:**
- The connection string must have permissions to send messages to the configured topic
- The Service Bus namespace must allow message sending operations
- Each client should have their own topic configured for status updates

**Required Azure RBAC Roles:**

The managed identity (system-assigned or user-assigned) must be granted these roles on the Service Bus namespace:

- **Azure Service Bus Data Sender** - Send messages to queues and topics ( more restrictive )
- **Azure Service Bus Data Owner** - Full access to Service Bus data operations (alternative to Data Sender)  

#### Clients

Clients will need to be able to read from their own topic.  This topic name is specified in the user profile under the **AsyncSBTopicFieldName** parameter.  i.e., The 

### Event Types

The following events are published to the Service Bus topic:

- **InQueue** - The message was enqueued for processing.
- **RetryAfterDelay** - The message will delay for a periord of time before being requeued.
- **ReQueued** - The message has been requeued for processing.
- **Processing** - The message is being processed (sent downstream)
- **Processed** - The message was successfully processed; blob URIs are available
- **Failed** - The message failed to process
- **Expired** - The message has expired


## 3. Azure Blob Storage Configuration

Azure Blob Storage is used to store request headers, request body data, and response data. There are two authentication methods available:

### Option A: Connection String Authentication

For development or when using storage account keys:

```bash
AsyncBlobStorageConnectionString=<storage-account-connection-string>
AsyncBlobContainer=<profile field name that contains the client's container name. >
AsyncClientBlobFieldname=user_container_name

BlobAccessTimeout=<The numebr of seconds that the blobs will be available for before they are automatically deleted.>
```

### Option B: Managed Identity Authentication (Recommended)

For production environments, use managed identity for enhanced security:

```bash
AsyncBlobStorageUseMI=true
AsyncBlobStorageAccountUri=https://<storage-account-name>.blob.core.windows.net
AsyncClientBlobFieldname=<profile field name that contains the client's container name. >
BlobAccessTimeout=<The numebr of seconds that the blobs will be available for before they are automatically deleted.>
```

**Requirements:**
- The connection string must include account keys with full storage permissions
- The storage account must allow blob creation and SAS token generation
- Each client should have their own container and have access assigned to it.
- Each client can have their own TTL for blob lifetime.  

**Required Azure RBAC Roles:**

The managed identity (system-assigned or user-assigned) must be granted these roles on the storage account:

- **Storage Blob Data Contributor** - Read, write, and delete blob data
- **Storage Blob Delegator** - Generate user delegation SAS tokens
- **Storage Account Contributor** - Manage storage account properties

**User Profile Configuration:**

```bash
AsyncSBTopicFieldName=<user-profile-field-name>
```

This field in the user profile determines the blob container folder structure. Users need access to their designated folder.

## 4. Client Request Configuration

### Enable Async Processing Per Request

Each incoming request must include a header to enable async processing:

```bash
AsyncClientAllowedFieldName=async-allowed
```

**Default behavior:** If not specified, the header name defaults to `"async-allowed"`

**Usage:** Clients must send requests with the header set to `"true"`:
```http
async-allowed: true
```

## Complete Configuration Example

### Development Environment (Connection String)
```bash
# Enable async mode
AsyncModeEnabled=true

# Service Bus
AsyncSBConnectionString=Endpoint=sb://myservicebus.servicebus.windows.net/;SharedAccessKeyName=...

# Blob Storage (Connection String)
AsyncBlobStorageConnectionString=DefaultEndpointsProtocol=https;AccountName=mystorage;AccountKey=...

# User Profile
AsyncSBTopicFieldName=user_servicebus_topic_name
AsyncClientBlobFieldname=user_container_name

# Client Header
AsyncClientAllowedFieldName=async-allowed
```

### Production Environment (Managed Identity)
```bash
# Enable async mode
AsyncModeEnabled=true

# Service Bus (Managed Identity)
AsyncSBUseMI=true
AsyncSBNamespace=myservicebus.servicebus.windows.net

# Blob Storage (Managed Identity)
AsyncBlobStorageUseMI=true
AsyncBlobStorageAccountUri=https://mystorage.blob.core.windows.net

# User Profile
AsyncSBTopicFieldName=user_servicebus_topic_name
AsyncClientBlobFieldname=user_container_name

# Client Header
AsyncClientAllowedFieldName=async-allowed
```

## Security Considerations

1. **Use Managed Identity in production** for enhanced security and credential management
2. **Limit storage account access** using RBAC roles instead of connection strings
3. **Configure appropriate blob retention policies** to manage storage costs
4. **Use Azure Key Vault** to store sensitive connection strings if managed identity isn't available

## Troubleshooting

### Common Issues

1. **"Failed to create SAS token"** - Ensure the managed identity has the Storage Blob Delegator role
2. **"BlobContainerClient not initialized"** - Check that InitClientAsync was called after AsyncWorker construction
3. **Service Bus connection failures** - Verify the connection string has send permissions for the topic
4. **Access denied errors** - Confirm RBAC roles are assigned to the correct managed identity
5. **Network access** - Confirm that the networking for the Storage account allows your applications to have access.

### Detailed Error Messages and Solutions

#### Blob Storage Errors

| Error Message | Cause | Solution |
|---------------|-------|----------|
| `Failed to generate SAS token for blob {BlobName} in container {ContainerName}` | SAS token generation failed, often due to missing permissions | Ensure managed identity has Storage Blob Delegator role, or verify connection string has account keys |
| `Cannot generate SAS token. Either enable managed identity (UsesMI=true) or provide a connection string with account keys` | Authentication method not properly configured | Set `AsyncBlobStorageUseMI=true` for managed identity or provide valid connection string |
| `AsyncBlobStorageAccountUri is not set. Cannot create BlobWriter` | Missing storage account URI for managed identity | Set `AsyncBlobStorageAccountUri` environment variable |
| `Invalid blob storage connection string provided` | Invalid or empty connection string | Verify `AsyncBlobStorageConnectionString` is correctly formatted |
| `Failed to create BlobServiceClient with managed identity` | Managed identity authentication failed | Check RBAC permissions and ensure managed identity is enabled |
| `Failed to create BlobServiceClient with connection string` | Connection string authentication failed | Verify connection string format and account keys |
| `UserId cannot be null or empty` | Missing user ID parameter | Ensure user ID is provided in requests |
| `ContainerName cannot be null or empty for userId: {UserId}` | Missing container name | Verify container name configuration |
| `BlobName cannot be null or empty` | Missing blob name parameter | Ensure blob name is generated properly |
| `Error initializing BlobContainerClient for userId {userId}` | Container client initialization failed | Check storage account permissions and container existence |
| `Blob storage is not enabled` | NullBlobWriter being used | Enable async mode with `AsyncModeEnabled=true` |

#### Service Bus Errors

| Error Message | Cause | Solution |
|---------------|-------|----------|
| `Failed to initialize ServiceBusSenderFactory` | Service Bus initialization failed | Verify `AsyncSBConnectionString` is valid and has send permissions |
| `Topic name cannot be null or empty` | Missing topic name parameter | Ensure topic name is configured in user profile field |
| `Failed to enqueue message to the status queue` | Internal queue operation failed | Check memory and system resources |
| `An error occurred while sending a message to the topic` | Service Bus send operation failed | Verify Service Bus connection and topic permissions |
| `Error while flushing service bus. Continuing` | Error during shutdown flush | Non-critical error during cleanup, check Service Bus connectivity |

#### Configuration Errors

| Error Message | Cause | Solution |
|---------------|-------|----------|
| `ArgumentNullException` for dependencies | Missing required service injection | Ensure all required services are registered in DI container |
| Authentication timeout errors | Managed identity token acquisition failed | Check Azure resource configuration and network connectivity |
| Permission denied on storage operations | Insufficient RBAC permissions | Verify all required roles are assigned: Storage Blob Data Contributor, Storage Blob Delegator |

### Diagnostic Steps

1. **Check Authentication**:
   ```bash
   # Verify managed identity is enabled
   curl -H "Metadata: true" "http://169.254.169.254/metadata/identity/oauth2/token?api-version=2018-02-01&resource=https://storage.azure.com/"
   ```

2. **Verify Storage Account Access**:
   - Confirm the managed identity appears in the storage account's Access Control (IAM)
   - Test blob operations using Azure CLI with the same identity

3. **Service Bus Connectivity**:
   - Test the connection string using Service Bus Explorer
   - Verify topic exists and has appropriate permissions

4. **Environment Variables**:
   ```bash
   # Check if all required variables are set
   echo $AsyncModeEnabled
   echo $AsyncBlobStorageUseMI
   echo $AsyncBlobStorageAccountUri
   echo $AsyncSBConnectionString
   ```

5. **Logging**:
   - Enable debug logging to see detailed error information
   - Check application logs for initialization errors
   - Monitor Azure Resource logs for authentication failures

### Performance Considerations

- **SAS Token Caching**: User delegation keys are cached for 1 hour to reduce API calls
- **Service Bus Batching**: Messages are processed in batches for better throughput
- **Container Client Reuse**: Container clients are cached per user to avoid recreation overhead 

