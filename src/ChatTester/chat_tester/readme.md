# chat-tester

chat-tester is a local Blazor Server app for exercising SimpleL7Proxy (or any HTTP backend) with interactive chat, vision, and traffic-behavior tests.

It is useful for:

- Sending realistic model requests (chat and vision) through the proxy.
- Inspecting raw request and response payloads, headers, and timings.
- Running repeatable behavior tests (abort, URL/auth probing, stress).

## Requirements and startup

Requirement: .NET 10 SDK.

```bash
cd test/chat_tester
dotnet restore
dotnet run
```

Then open one of:

- http://localhost:5259
- https://localhost:7117

## Main pages

- Home (/): launch point for Investigator.
- Investigator (/investigator, alias /vision): build and send image-aware or chat-style requests using model/API templates, inspect structured result tabs, and run multi-request batches.
- Chat (/chat): multi-turn conversation runner with streaming output and per-turn metrics.
- Abort test (/abort-test): sends requests and aborts quickly to test disconnect handling.
- URL tester (/url-tester): runs repeated calls against URL lists with configurable auth headers.
- Stress test (/stress-test): high-parallel request load test with aggregate and per-request timing.
- History (/history): review saved request history and switch storage mode (disk, blob storage, or Cosmos DB).

## Shared request controls

Most test pages share these controls:

- Authorization panel: configure header name, API key or bearer mode, and token source.
- User panel: inject user identity and optional priority key headers.
- Custom headers panel: add static or templated headers (supports {id} token).
- Raw exchange/result panels: inspect request headers, response headers, request body, and response body.

## Configuration

Defaults are loaded from the chat-tester section in appsettings.json and can be overridden in UI at runtime.

Common keys:

- ServerBaseUrl: base URL of the proxy/backend under test.
- DefaultMethod: default HTTP method for URL-oriented tests.
- ChatEndpointPath: default endpoint for chat/investigator calls.
- ChatRequestBody: default request body seed.
- RequestCount: default request count for repeated tests.
- AbortDelayMilliseconds: delay before cancel in abort tests.
- Payload: default body for URL/abort-style requests.
- TargetUrls: default URL list for abort tests.
- AuthorizationHeaderName, AuthorizationHeaderPrefix: auth header defaults.
- AuthTargetUrls: default URL list for URL tester.
- UserHeaderName, PriorityKeyHeader, UserNames: user and priority header settings.
- DefaultHeaders: headers applied to all requests (Name: Value format).
- History: history persistence settings.
- Conversations: chat conversation persistence settings.

## Model catalogs

chat-models.json:

- apis: API schema definitions and endpoint templates.
- models: model metadata and API compatibility.
- modeldefaults: default field sets by model/API.

vision-models.json:

- vision-models section for vision APIs, compatible models, request templates, and defaults.

Optional environment-specific overrides are loaded automatically when present:

- chat-models.Development.json
- vision-models.Development.json

## Notes

- The app stores history and conversations based on selected storage mode and settings.
- Local defaults are useful for repeated test loops, but each run can override values from the UI.
