# chat-tester

A local Blazor Server app for exercising SimpleL7Proxy (or any HTTP backend) with
chat-style requests and a set of failure / stress scenarios. Use it to send
requests through the proxy, inspect the streamed response and headers, and trigger
the kinds of abusive or malformed traffic the proxy is expected to handle
gracefully.

## What it does

The app exposes five interactive tests from a single home page:

- **Analyze query** — Send one chat-style request, pick a model template (OpenAI,
  Anthropic, Gemini, Llama, Mistral, DeepSeek, xAI, Cohere), attach optional
  authorization, and inspect the live streamed response alongside the raw payload,
  request headers, and response headers.
- **Chat** — Hold a multi-turn conversation with the model. Each request
  automatically includes all prior turns so the model has full context. Useful for
  verifying that the proxy preserves conversation state across streaming calls.
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
| `ChatEndpointPath` | Analyze query, Chat, Burst test | Endpoint path for chat requests (e.g. `/openai/v1/chat/completions`). |
| `ChatRequestBody` | Analyze query, Chat, Burst test | Default JSON request body for chat calls. |
| `RequestCount` | Rapid disconnect, Auth probe | Default number of requests per run. |
| `AbortDelayMilliseconds` | Rapid disconnect | Delay before each request is aborted. |
| `Payload` | Rapid disconnect, Auth probe | Default POST payload. |
| `TargetUrls` | Rapid disconnect | List of target paths to hit (one entry per array item). |
| `AuthorizationHeaderName` | Auth probe | Default auth header name (e.g. `S7P-KEY`, `Authorization`). |
| `AuthorizationHeaderPrefix` | Auth probe | Default header value prefix (e.g. `Bearer`). |
| `AuthTargetUrls` | Auth probe | List of protected endpoints to probe with credentials. |
| `UserHeaderName` | All tests | HTTP header used to identify the caller (e.g. `x-user-id`). |
| `PriorityKeyHeader` | All tests | Header the proxy reads for its priority key (e.g. `S7PPriorityKey`). Sent when a user has a priority level set. |
| `UserNames` | All tests | List of named users available in the User panel. Each entry is a name or `name, priority` pair (e.g. `"alice, high"`). |
| `DefaultHeaders` | All tests | Custom headers sent with every request. Each entry is a `Name: Value` string. Use `{id}` in a value to insert the sequential request number. |

Values left blank fall back to the in-code defaults, and anything set here can still
be overridden in the UI per run.

#### Model catalog

The model picker is driven by three optional sections in `appsettings.json`:

- **`apis`** — Named API schemas, each with an `id`, `displayName`, and `endpoint`
  path. The endpoint may include a `{model}` placeholder (e.g. Gemini's
  `/v1beta/models/{model}:generateContent`).
- **`models`** — Model definitions, each with an `id`, `provider`, `displayName`,
  and an `apis` array listing which API schemas the model supports.
- **`modeldefaults`** — Default field values applied when building a request body.
  Each entry has an `appliesTo` list (model ids, or `"*"` for all) and an
  `appliesToAPI` list, plus a `fields` array of single-key objects.

Omitting these sections falls back to the built-in catalog in
`Components/Shared/ModelCatalog.cs`.

## Using each test

All tests share three optional shared panels.

**Authorization** — Check **Send with authorization** and expand it to set:

- **Header name** — e.g. `Authorization`, `x-api-key`, `S7P-KEY`.
- **Auth mode** — `API key` (raw value) or `OAuth / bearer token`.
- For OAuth: a header value prefix (e.g. `Bearer`) and a token that is either typed
  in / uploaded as a file (**Manual**) or fetched from a URL (**Fetch**) by calling
  a token endpoint and reading a response property (defaults to `access_token`).

**User** — Expand to inject a user identity header and an optional priority key
header on every request. Four modes are available:

- **No user** — no identity headers are added.
- **Selected user** — pick one user from the configured list; that user's name and
  priority are sent on every request.
- **Random user** — a random user is chosen from the list for each request.
- **Rotating user** — users from the list are cycled in order across requests.

The user list, header names, and priority key header name come from `appsettings.json`
(`UserNames`, `UserHeaderName`, `PriorityKeyHeader`) and can be edited in the UI.

**Custom headers** — Expand to add arbitrary request headers sent with every call.
Use `{id}` in a value to insert the sequential request number.

### Request inspector (`/inspect`)

1. Set **Server base URL** and **Endpoint path** (default
   `/openai/v1/chat/completions`).
2. Pick a model template; the request body panel pre-fills a matching payload for
   that provider's schema. Edit the body as needed.
3. Optionally enable authorization, user injection, and custom headers.
4. Click **Send single chat call**.
5. Watch the streamed output in **Chat response**. Expand **Raw response** to switch
   between **Request headers**, **Response headers**, **Request body**, **Response
   body**, and **Backend Log** tabs. The Backend Log tab is populated when the proxy
   returns debug information in a response header; it is disabled when no log is
   present. Use **Full screen** for a larger view of either panel.

### Chat (`/chat`)

1. Set **Server base URL**, **Endpoint path**, and select a model template in the
   **Model** tab.
2. Optionally enable authorization, user injection, and custom headers.
3. Type a message in the **Message** box at the bottom of the chat panel and click
   **Send** (or press Enter).
4. The assistant's reply streams in as a bubble in the conversation. Each turn
   includes a metrics panel (status, content-type, TTFB, duration) and a disclosure
   for request/response headers and the raw response body.
5. Continue sending messages; prior turns are included in every subsequent request.
6. Click **Clear** in the card header to reset the conversation.

### Rapid disconnect (`/abort-test`)

1. Set **Server base URL**, **Method** (GET/POST), **Request count**, and
   **Abort delay (ms)** — how long to wait before aborting each request.
2. Edit the **Target URLs** list (one path per line) and the **Payload** for POST.
3. Optionally enable authorization, then click **Start burst**.
4. Review the run summary table: each row shows the endpoint, outcome, and elapsed
   milliseconds.

### URL tester (`/url-tester`)

1. Set **Server base URL** and **Method**.
2. Configure the authorization panel with the header name and token to test.
3. Edit the **Target URLs** list (one path per line), the **Payload**, and the
   **Request count**.
4. Click **Run auth probe**.
5. Review the results table: each row shows the endpoint, HTTP status code, and
   outcome.

### Burst test (`/stress-test`)

1. Set **Server base URL**, **Endpoint path**, and **Content-Type**.
2. Set **Parallel count** (1–2000) — the number of requests sent simultaneously.
3. Optionally enable authorization and edit the request body.
4. Click **Run stress test**.
5. Review the summary tiles (success/total, total bytes, average TTFB, average
   total) and the per-request table (status, bytes, TTFB, total time). Failed
   requests are highlighted in red.

## Adding model templates

The preferred way to add models is via the `apis`, `models`, and `modeldefaults`
sections in `appsettings.json` (see [Model catalog](#model-catalog) above). The
configuration is loaded at startup and merged with the built-in catalog in
[Components/Shared/ModelCatalog.cs](Components/Shared/ModelCatalog.cs).

To add a model that requires a completely new request body shape that
`appsettings.json` cannot express, edit `ModelCatalog.cs` directly and add a case
to `BuildBody`.
