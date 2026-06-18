# chat-tester

A local Blazor Server app for exercising SimpleL7Proxy (or any HTTP backend) with
chat-style requests and a set of failure / stress scenarios. Use it to send
requests through the proxy, inspect the streamed response and headers, and trigger
the kinds of abusive or malformed traffic the proxy is expected to handle
gracefully.

## What it does

The app exposes currently four interactive tests from a single home page:

- **Chat test** — Send one chat-style request, pick a model template (OpenAI,
  Anthropic, Gemini, Llama, Mistral, DeepSeek, xAI, Cohere), attach optional
  authorization, and inspect the live streamed response alongside the raw payload,
  request headers, and response headers.
- **Rapid disconnect** — Fire a burst of requests against an editable list of
  target paths and abort each one before the server replies. Demonstrates how the
  proxy handles clients that disconnect mid-request.
- **Authorization probe** — Send repeated requests to a list of endpoints with a
  configurable auth header name and token source (API key or OAuth bearer). Useful
  for confirming which paths are protected and how the proxy responds to missing or
  invalid credentials.
- **Burst test** — Run many parallel requests at once and measure bytes returned,
  total time, and time to first byte (TTFB) for each, with success/total and
  average TTFB summary stats.

## Setup

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
cd test/chat_tester
dotnet restore
dotnet run
```

Then open the app in a browser:

- HTTP: <http://localhost:5259>
- HTTPS: <https://localhost:7117>

Defaults for the failure scenarios (server base URL, endpoint path, request body,
target URLs, auth header names, etc.) come from the `chat-tester` section in
[appsettings.json](appsettings.json). Edit that section to change the defaults that
pre-populate the forms, or just override the values in the UI per run.

### Configuring appsettings.json

`appsettings.json` contains the default values used by the test application.

| Field | Used by | What to set |
| --- | --- | --- |
| `ServerBaseUrl` | All tests | Base URL of the proxy or backend under test (e.g. `http://localhost:8000`). |
| `DefaultMethod` | Rapid disconnect, Auth probe | Default HTTP method (`GET` or `POST`). |
| `ChatEndpointPath` | Chat test, Burst test | Endpoint path for chat requests (e.g. `/openai/v1/chat/completions`). |
| `ChatRequestBody` | Chat test, Burst test | Default JSON request body for chat calls. |
| `RequestCount` | Rapid disconnect, Auth probe | Default number of requests per run. |
| `AbortDelayMilliseconds` | Rapid disconnect | Delay before each request is aborted. |
| `Payload` | Rapid disconnect, Auth probe | Default POST payload. |
| `TargetUrls` | Rapid disconnect | List of target paths to hit (one entry per array item). |
| `AuthorizationHeaderName` | Auth probe | Default auth header name (e.g. `S7P-KEY`, `Authorization`). |
| `AuthorizationHeaderPrefix` | Auth probe | Default header value prefix (e.g. `Bearer`). |
| `AuthTargetUrls` | Auth probe | List of protected endpoints to probe with credentials. |

Values left blank fall back to the in-code defaults, and anything set here can still
be overridden in the UI per run.

## Using each test

All tests share an **Authorization** panel. Check **Send with authorization** and
expand it to set:

- **Header name** — e.g. `Authorization`, `x-api-key`, `S7P-KEY`.
- **Auth mode** — `API key` (raw value) or `OAuth / bearer token`.
- For OAuth: a header value prefix (e.g. `Bearer`) and a token that is either typed
  in / uploaded as a file (**Manual**) or fetched from a URL (**Fetch**) by calling
  a token endpoint and reading a response property (defaults to `access_token`).

### Chat test (`/chat-test`)

1. Set **Server base URL** and **Endpoint path** (default
   `/openai/v1/chat/completions`).
2. Pick a model template; the request body panel pre-fills a matching payload for
   that provider's schema. Edit the body as needed.
3. Optionally enable authorization.
4. Click **Send single chat call**.
5. Watch the streamed output in **Chat response**. Expand **Raw response** to switch
   between request headers, response headers, request body, and response body tabs.
   Use **Full screen** for a larger view of either panel.

### Rapid disconnect (`/rapid-disconnect`)

1. Set **Server base URL**, **Method** (GET/POST), **Request count**, and
   **Abort delay (ms)** — how long to wait before aborting each request.
2. Edit the **Target URLs** list (one path per line) and the **Payload** for POST.
3. Optionally enable authorization, then click **Start burst**.
4. Review the run summary table: each row shows the endpoint, outcome, and elapsed
   milliseconds.

### Authorization probe (`/authorization-probe`)

1. Set **Server base URL** and **Method**.
2. Configure the authorization panel with the header name and token to test.
3. Edit the **Target URLs** list (one path per line), the **Payload**, and the
   **Request count**.
4. Click **Run auth probe**.
5. Review the results table: each row shows the endpoint, HTTP status code, and
   outcome.

### Burst test (`/burst-test`)

1. Set **Server base URL**, **Endpoint path**, and **Content-Type**.
2. Set **Parallel count** (1–2000) — the number of requests sent simultaneously.
3. Optionally enable authorization and edit the request body.
4. Click **Run stress test**.
5. Review the summary tiles (success/total, total bytes, average TTFB, average
   total) and the per-request table (status, bytes, TTFB, total time). Failed
   requests are highlighted in red.

## Adding model templates

Model templates live in
[Components/Shared/ModelCatalog.cs](Components/Shared/ModelCatalog.cs). To support a
new model, add a row to `Templates`. To support a new request body shape, add a case
to `BuildBody`.
