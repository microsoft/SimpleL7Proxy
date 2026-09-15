# Test HTTP and LLM endpoints

## Request testing prerequisites

- **.NET 10 SDK** and a checkout of this repository to run CompanionApp. Follow the [shared setup](readme.md#set-up-companionapp).
- **A running proxy, API Management gateway, or backend endpoint** reachable from the machine running CompanionApp.
- **Any credentials required by that endpoint**, plus the API path and method. For LLM requests, also confirm the model or deployment name.
- **For optional live telemetry:** a proxy publishing to an accessible Event Hub, an existing consumer group, and receive permission. Use App Configuration-independent [Event Hub authentication](#event-hub-setup-for-eventhub-monitor-and-insights): Azure Event Hubs Data Receiver for CompanionApp's identity, or a connection string with receive access.

## Connect to an endpoint

**Match the endpoint, request path, model, and authentication to the backend you are testing.** Request testing does not require deployment permissions, an App Configuration store, or Event Hub telemetry.

Edit these JSON property paths in [appsettings.Development.json](appsettings.Development.json), then restart CompanionApp:

| Setting | What to edit |
| --- | --- |
| `CompanionApp.ServerBaseUrl` | Replace the sample Container App URL with your proxy or backend base URL, such as `http://localhost:8000`. `ServerBaseUrl-2` is not read. |
| `CompanionApp.AuthorizationHeaderName` | Match the inbound authentication header, such as `S7P-KEY`, `api-key`, or `Authorization`. Set the credential and authentication mode in the request form. |
| `CompanionApp.AuthorizationHeaderPrefix` | Match the token scheme required by that endpoint; the supplied value is `Bearer`. This is not an API key or access token. |
| `CompanionApp.UserHeaderName`, `CompanionApp.PriorityKeyHeader` | Match the identity and priority header names configured on your proxy. The supplied names are `X-UserID` and `S7PPriorityKey`. |
| `CompanionApp.DefaultHeaders` | Add request headers as `Name: Value` strings, or enter request-specific values in the form. |

For a local proxy listening on port 8000, merge this value into your local settings:

```json
{
	"CompanionApp": {
		"ServerBaseUrl": "http://localhost:8000"
	}
}
```

This does not start a proxy or disable its authentication. Existing browser preferences can override configured request defaults; check **User Preferences** (`/user-preferences`) and the request form if an old value still appears.

## Choose a test

| Task | Page |
| --- | --- |
| Build HTTP, chat, or image-aware requests; inspect results; run request batches | **Investigator** (`/investigator`, alias `/vision`) |
| Run multi-turn, streaming conversations with per-turn metrics | **Chat** (`/chat`) |
| Cancel requests quickly to test disconnect handling | **Abort test** (`/abort-test`) |
| Run repeated calls against URL lists | **URL tester** (`/url-tester`) |
| Create parallel request load and inspect timings | **Stress test** (`/stress-test`) |

Model definitions are loaded from [chat-models.json](chat-models.json) and [vision-models.json](vision-models.json). Their Development variants override APIs, models, templates, and defaults for local work. Check that the selected model or deployment name and endpoint path exist on your backend; editing the proxy base URL does not change those values.

## Send and inspect requests

1. Open the test page and select the API path, method, model, and request body required by the endpoint.
2. In **Authorization**, configure the header name, API key or bearer mode, and token source.
3. Set the user identity and optional priority header. Add custom headers when needed; `{id}` is supported in templated headers.
4. Submit the request and inspect the request headers, response headers, request body, response body, and timings in the exchange and result views.

Verify the result:

- [ ] The request uses the intended endpoint, path, and model or deployment name.
- [ ] The response status and body match the expected backend behavior.
- [ ] For proxy tests, follow [Verify SimpleL7Proxy](../../docs/getting-started/verify.md) to check readiness and proxy response signals.

## Review saved requests

Open **History** (`/history`) to review saved requests. The [shared setup](readme.md#create-local-settings) uses local disk storage:

| Setting | What to edit |
| --- | --- |
| `CompanionApp.History.Mode`, `CompanionApp.Conversations.Mode` | Keep `Disk` for local storage without Azure storage dependencies. Other supported modes are `BlobStorage` and `CosmosDb`. |
| `CompanionApp.History.DiskPath`, `CompanionApp.Conversations.DiskPath` | Use writable paths. The supplied values are `data/history` and `data/conversations`. |

## Event Hub setup for EventHub Monitor and Insights

**Telemetry is optional for request testing. To inspect proxy activity, the proxy must publish events and CompanionApp must read the same Event Hub.** Enabling the reader does not configure proxy logging.

Use **EventHub Monitor** (`/eventhub`) for requests, backends, endpoints, paths, users, circuit-breaker state, and runtime metrics. **Insights** (`/insights`) analyzes the same feed. Both pages share one server-side `EventHubReader` and one in-memory event store.

Merge this nested section into [appsettings.Development.json](appsettings.Development.json), keeping your other `CompanionApp` settings and replacing the hub and namespace with your actual values. The consumer group must already exist; `$Default` is the built-in group.

```json
{
	"CompanionApp": {
		"EventHubMonitor": {
			"eventhub_enabled": true,
			"LocalFilePath": "",
			"ConnectionString": "",
			"EventHubName": "your-event-hub",
			"EventHubNamespace": "your-namespace.servicebus.windows.net",
			"ConsumerGroup": "$Default",
			"StartPosition": "latest",
			"RefreshSeconds": 5
		}
	}
}
```

Use one authentication method:

- **Connection string:** set `ConnectionString` and `EventHubName`. The connection string must allow listen/receive access.
- **Microsoft Entra ID:** leave `ConnectionString` empty and set `EventHubNamespace` and `EventHubName`. Assign the identity used by CompanionApp the **Azure Event Hubs Data Receiver** role on the Event Hub or its namespace. Use the [local Azure sign-in](readme.md#sign-in-for-azure-workflows) for development; when deployed, assign the role to the app's managed identity.

Standard environment-variable overrides for this section use `CompanionApp__EventHubMonitor__...`, such as `CompanionApp__EventHubMonitor__eventhub_enabled`. The dedicated overrides still take precedence when present: `EVENTHUB_CONNECTIONSTRING`, `EVENTHUB_NAME`, `EVENTHUB_CONSUMER_GROUP`, and `EVENTHUB_NAMESPACE`.

Use an absolute `LocalFilePath` for a newline-delimited JSON event file. Relative paths are resolved from the running application's output directory, not the repository root. Set `eventhub_enabled` to `false` for file-only analysis, or clear `LocalFilePath` for live-only reading. A configured file is processed independently of `eventhub_enabled`.

Restart CompanionApp after changing these settings. With `StartPosition=latest`, send a new request after the reader starts; `earliest` also reads retained events. `RefreshSeconds` is the UI refresh interval in seconds, not the proxy's App Configuration refresh interval.

Verify live telemetry:

- [ ] The CompanionApp log contains **Event Hub reader started**.
- [ ] A request ID from traffic you sent appears in the Event Hub views.

> [!NOTE]
> Startup includes sample metrics. Populated charts alone do not confirm live telemetry.

> [!NOTE]
> `CheckpointStorage` is present in configuration but is not used by the current Event Hub reader. It reads every Event Hub partition directly and does not persist checkpoints.

## Troubleshoot requests and telemetry

| Symptom | Check |
| --- | --- |
| Requests return `401` or `403` | Match the proxy's required header, authentication mode, credential, and user identity in the request form. Setting an App Configuration endpoint does not configure request authentication. |
| Event Hub views do not show a new request | Check proxy event logging, hub/namespace, consumer group, receive permission, and `eventhub_enabled`. Check `EVENTHUB_*` overrides and send traffic after a `latest` reader starts. |
| Startup reports a missing event import file | Clear `LocalFilePath` or use an absolute path to an existing event file. Disabling live reading does not disable file import. |