# Async Operation Configuration

**The proxy can process requests asynchronously, returning a 202 immediately and delivering results via Azure Blob Storage with status updates over Azure Service Bus.**

**TL;DR:**
- Set `AsyncModeEnabled=true`, configure `AsyncBlobStorageConfig` and `AsyncSBConfig`
- Each client needs a user profile with async enabled, a blob container, and a Service Bus topic
- Requests opt in via the `AsyncClientRequestHeader` header

For the complete list of Async-related environment variables and their default values, see [ENVIRONMENT_VARIABLES.md](ENVIRONMENT_VARIABLES.md#async-processing-variables).

## Configuration Reference

| Setting | Mode | Default | Description |
|---|---|---|---|
| `AsyncModeEnabled` | Cold | `false` | Master switch for async processing |
| `AsyncClientRequestHeader` | Warm | `S7PAsyncMode` | Request header clients send to opt in |
| `AsyncClientConfigFieldName` | Warm | `async-config` | User profile field containing client async config |
| `AsyncTimeout` | Warm | `1800000` (30 min) | Max async request lifetime (ms) |
| `AsyncTriggerTimeout` | Warm | `10000` (10 s) | Time before a request upgrades to async (ms) |
| `AsyncTTLSecs` | Warm | `86400` (24 h) | Blob SAS token lifetime (seconds) |
| `AsyncBlobStorageConfig` | Cold | `uri=https://mystorageaccount.blob.core.windows.net,mi=true` | Composite blob storage connection |
| `AsyncBlobWorkerCount` | Cold | `2` | Number of background blob write workers |
| `AsyncSBConfig` | Cold | `cs=...,ns=...,q=requeststatus,mi=false` | Composite Service Bus connection |
| `StorageDbContainerName` | Cold | `Requests` | Default blob container name |
| `AsyncClassNames` | Cold | _(empty — uses built-in defaults)_ | Override interface→class DI mappings |

> [!NOTE]
> **Warm** settings are hot-reloaded via App Configuration (~30 s). **Cold** settings require a restart.

## Enabling Async Mode

Async must be enabled in three places: the **proxy** (system-wide), the **user profile** (per-client), and the **request** (per-call header).

```bash
AsyncModeEnabled=true
AsyncClientRequestHeader=S7PAsyncMode
AsyncTriggerTimeout=10000
AsyncTimeout=1800000
AsyncClientConfigFieldName=async-config
```

- **AsyncTriggerTimeout**: Fast requests complete normally. After this timeout, the request upgrades to async — a 202 response is returned immediately with details on how to retrieve the result.
- **AsyncTimeout**: Maximum time an async request is allowed to run before the proxy abandons it.

## Azure Service Bus Configuration

The proxy sends real-time status updates to client Service Bus topics as requests are processed. Each client reads only from their designated topic.

### Composite Config Format

All Service Bus settings are supplied via a single composite string:

```bash
AsyncSBConfig=cs=<connection-string>,ns=<namespace>,q=<queue>,mi=<true|false>
```

| Field | Description |
|---|---|
| `cs` | Connection string (used when `mi=false`) |
| `ns` | Fully-qualified Service Bus namespace (used when `mi=true`) |
| `q` | Queue/topic name for status messages |
| `mi` | `true` to use Managed Identity, `false` for connection string |

> [!TIP]
> **Use Managed Identity in production** — set `mi=true` and provide `ns`.

**Required Azure RBAC Roles** (for Managed Identity):
- **Azure Service Bus Data Sender** — send messages to queues and topics
- **Azure Service Bus Data Owner** — alternative with full data access

### Client-Side

Clients need RBAC permission and a subscription to their topic. The topic name is specified in the user profile under the `AsyncClientConfigFieldName` field.

#### Status Events

| Event | Description |
|---|---|
| `InQueue` | Request enqueued for processing |
| `RetryAfterDelay` | Request will delay before requeue |
| `ReQueued` | Request has been requeued |
| `Processing` | Request is being sent downstream |
| `Processed` | Complete — blob URIs available |
| `Failed` | Request failed |
| `Expired` | Request expired |

#### Sample Client Code

```csharp
using Azure.Messaging.ServiceBus;
using Azure.Identity;

var serviceBusNamespace = Environment.GetEnvironmentVariable("SERVICEBUS_NAMESPACE");
var serviceBusTopicName = Environment.GetEnvironmentVariable("SERVICEBUS_TOPICNAME");
var serviceBusSubscriptionName = Environment.GetEnvironmentVariable("SERVICEBUS_SUBSCRIPTIONNAME");

var credential = new DefaultAzureCredential();
var client = new ServiceBusClient(serviceBusNamespace, credential);

var processor = client.CreateProcessor(serviceBusTopicName, serviceBusSubscriptionName);

processor.ProcessMessageAsync += MessageHandler;
processor.ProcessErrorAsync += ErrorHandler;

await processor.StartProcessingAsync();

async Task MessageHandler(ProcessMessageEventArgs args)
{
    var message = args.Message;
    var jobStatus = message.Body.ToString();
    Console.WriteLine($"{jobStatus}");
    await args.CompleteMessageAsync(message);
}

async Task ErrorHandler(ProcessErrorEventArgs args)
{
    Console.WriteLine($"Error processing message: {args.Exception.Message}");
}
```

## Azure Blob Storage Configuration

Azure Blob Storage stores request headers, request body data, and response data. All settings are supplied via a single composite string:

```bash
AsyncBlobStorageConfig=uri=<storage-account-uri>,mi=<true|false>
```

| Field | Description |
|---|---|
| `uri` | Blob storage account URI (e.g. `https://mystorage.blob.core.windows.net`) |
| `mi` | `true` to use Managed Identity, `false` for connection string |

When `mi=false`, provide a connection string in the `uri` field instead.

> [!WARNING]
> Each client should have their own blob container with RBAC access. The `AsyncTTLSecs` setting controls how long SAS tokens remain valid.

**Required Azure RBAC Roles** (for Managed Identity):
- **Storage Blob Data Contributor** — read, write, and delete blob data
- **Storage Blob Delegator** — generate user delegation SAS tokens
- **Storage Account Contributor** — manage storage account properties

### Blob Write Queue

The proxy uses a background write queue for blob operations. Configure the worker count with:

```bash
AsyncBlobWorkerCount=2
```

## User Profile Configuration

Each user's profile must include an async config field (named by `AsyncClientConfigFieldName`):

```
<AsyncClientConfigFieldName>=enabled=<true|false>,containername=<name>,topic=<name>,timeout=<seconds>
```

| Sub-field | Description |
|---|---|
| `enabled` | Whether this client can use async processing |
| `containername` | Blob container for storing this client's results |
| `topic` | Service Bus topic for status updates |
| `timeout` | SAS token lifetime in seconds |

Each client's service principal needs RBAC access to both the blob container and the Service Bus topic.

## Client Request Configuration

Clients opt in per request by sending the configured header:

```http
curl https://proxy.domain.com/do_something -H "S7PAsyncMode: true"
```

## Async Class Name Overrides

The proxy registers async service implementations via DI using the `AsyncClassNames` config. The format is a comma-separated list of `Interface:ClassName` pairs. When empty (default), built-in defaults are used:

```
IServiceBusFactory:ServiceBusFactory, IServiceBusRequestService:ServiceBusRequestService,
IBackupAPIService:BackupAPIService, IBlobWriterFactory:BlobWriterFactory
```

At startup, each entry is validated:
- The interface and class must exist in the assembly
- The class must implement the interface
- The class must be in the same namespace as the interface
- All required interfaces from the built-in defaults must be present

> [!NOTE]
> Invalid entries are skipped with a warning. Missing required interfaces are logged as warnings.

## Complete Configuration Examples

### Development (Connection String)
```bash
AsyncModeEnabled=true
AsyncSBConfig=cs=Endpoint=sb://myservicebus.servicebus.windows.net/;SharedAccessKeyName=...,ns=,q=requeststatus,mi=false
AsyncBlobStorageConfig=uri=DefaultEndpointsProtocol=https;AccountName=mystorage;AccountKey=...,mi=false
AsyncClientConfigFieldName=async-config
AsyncClientRequestHeader=S7PAsyncMode
```

### Production (Managed Identity)
```bash
AsyncModeEnabled=true
AsyncSBConfig=cs=,ns=myservicebus.servicebus.windows.net,q=requeststatus,mi=true
AsyncBlobStorageConfig=uri=https://mystorage.blob.core.windows.net,mi=true
AsyncClientConfigFieldName=async-config
AsyncClientRequestHeader=S7PAsyncMode
```

## Security Considerations

1. **Use Managed Identity in production** for credential management
2. **Limit storage access** using RBAC instead of connection strings
3. **Configure blob retention** via `AsyncTTLSecs` to manage storage costs
4. **Use Azure Key Vault** for connection strings if Managed Identity is unavailable

## Troubleshooting

| Symptom | Cause |
|---|---|
| "Failed to create SAS token" | Managed identity missing Storage Blob Delegator role |
| "BlobContainerClient not initialized" | `InitClientAsync` not called after AsyncWorker construction |
| Service Bus connection failures | Connection string lacks send permissions for the topic |
| Access denied errors | RBAC roles not assigned to the correct managed identity |
| `[ASYNC] Invalid AsyncClasses entry` | Class not found, doesn't implement interface, or wrong namespace |
| `[ASYNC] Required interface missing` | `AsyncClassNames` override is missing a required interface |
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
| `Error while flushing Service Bus. Continuing` | Error during shutdown flush | Non-critical error during cleanup, check Service Bus connectivity |

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

