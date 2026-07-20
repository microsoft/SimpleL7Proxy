# ChatTester

ChatTester is a local Blazor Server application for exercising SimpleL7Proxy, or another HTTP backend, with chat, vision, request-behavior, and live Event Hub analysis tools.

## Requirements and startup

Requirement: .NET 10 SDK.

```bash
cd src/ChatTester
cp appsettings.json appsettings.Development.json
```

Customize `appsettings.Development.json` for the proxy URL, authentication defaults, local storage, and optional Event Hub connection. Keep credentials and environment-specific values in this development file; do not commit secrets.

```bash
dotnet restore
dotnet run
```

Open one of the following URLs:

- http://localhost:5259
- https://localhost:7117

The launch profiles set `ASPNETCORE_ENVIRONMENT=Development`, so `appsettings.Development.json`, `chat-models.Development.json`, and `vision-models.Development.json` override their corresponding base files automatically.

## Main pages

- Investigator (`/investigator`, alias `/vision`): build and send chat or image-aware requests from model and API templates, inspect structured results, and run request batches.
- Chat (`/chat`): run multi-turn, streaming conversations with per-turn metrics.
- Abort test (`/abort-test`): cancel requests quickly to test disconnect handling.
- URL tester (`/url-tester`): run repeated calls against URL lists with configurable authentication headers.
- Stress test (`/stress-test`): create parallel request load and inspect aggregate and per-request timing.
- History (`/history`): review saved requests and choose disk, Blob Storage, or Cosmos DB persistence.
- [EventHub Monitor](/eventhub): view proxy requests, backends, endpoints, paths, users, circuit-breaker state, and runtime metrics from Event Hub events.
- [Insights](/insights): analyze the same Event Hub feed in an insights-oriented view.
- User preferences (`/user-preferences`): manage browser-local UI preferences.

## Shared request controls

Most test pages provide these controls:

- Authorization: configure the header name, API key or bearer mode, and token source.
- User: add user identity and an optional priority-key header.
- Custom headers: add static or templated headers; `{id}` is supported.
- Exchange and result views: inspect request headers, response headers, request body, response body, and timings.

## Configuration

The `chat-tester` section controls request defaults and persistence. Values in `appsettings.Development.json` override the base settings.

Common settings:

- `ServerBaseUrl`: proxy or backend base URL.
- `AuthorizationHeaderName` and `AuthorizationHeaderPrefix`: default authorization-header behavior.
- `UserHeaderName` and `PriorityKeyHeader`: user identity and priority headers.
- `DefaultHeaders`: headers added to requests, in `Name: Value` format.
- `History` and `Conversations`: persistence mode and disk, Blob Storage, or Cosmos DB settings.

Model definitions are loaded from `chat-models.json` and `vision-models.json`. Their `Development` variants can override APIs, models, templates, and model defaults for local work.

## Event Hub setup for EventHub Monitor and Insights

The **EventHub Monitor** and **Insights** tabs share one server-side `EventHubReader` and one in-memory event store. Both tabs remain empty unless the application can read the configured Event Hub, apart from any configured local event file.

Configure the `EventHubMonitor` section in `appsettings.Development.json`:

```json
"EventHubMonitor": {
	"eventhub_enabled": true,
	"ConnectionString": "",
	"EventHubName": "<event-hub-name>",
	"EventHubNamespace": "<event-hub-namespace>",
	"ConsumerGroup": "$Default",
	"StartPosition": "latest",
	"RefreshSeconds": 5
}
```

Use one authentication method:

- **Connection string:** set `ConnectionString` and `EventHubName`. The connection string must allow listen/receive access.
- **Microsoft Entra ID:** leave `ConnectionString` empty and set `EventHubNamespace` and `EventHubName`. Assign the identity used by ChatTester the **Azure Event Hubs Data Receiver** role on the Event Hub or its namespace. For local development, authenticate that identity with `az login`; when deployed, assign the role to the app's managed identity.

Environment variables override the equivalent settings when present: `EVENTHUB_CONNECTIONSTRING`, `EVENTHUB_NAME`, `EVENTHUB_CONSUMER_GROUP`, and `EVENTHUB_NAMESPACE`.

`LocalFilePath` is optional. When it points to an existing newline-delimited JSON event file, ChatTester imports the file at startup. Set `eventhub_enabled` to `false` to use only the local file. `StartPosition` accepts `latest` or `earliest`; `RefreshSeconds` controls the tab refresh cadence in seconds.

> [!NOTE]
> `CheckpointStorage` is present in configuration but is not used by the current Event Hub reader. It reads every Event Hub partition directly and does not persist checkpoints.

## Notes

- History and conversations use the configured storage mode; disk storage is the default.
- Request defaults can be overridden from the UI for an individual run.
