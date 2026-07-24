# How does the proxy handle observability and telemetry?

Telemetry channels (App Insights, `ProxyEvent`/`EVENT_LOGGERS`), event fan-out, event contents, streaming token counting, and custom sinks.

[← Back to FAQ index](README.md)

---

### Where does the proxy send telemetry?

Two mechanisms run side by side. Standard ASP.NET telemetry (requests, dependencies, exceptions) goes straight to Azure Application Insights via `TelemetryClient` when `APPINSIGHTS_CONNECTIONSTRING` is set. Separately, every proxied request also produces a `ProxyEvent` that fans out through `EVENT_LOGGERS` to a local JSON log file, Azure Event Hubs, and/or a custom logger — any combination of these can run at the same time.

### How does the proxy fan events out to multiple sinks?

`CompositeEventClient` holds a zero-lock, zero-allocation snapshot of the registered backends and dispatches each serialized `ProxyEvent` to all of them. Each backend buffers events in its own queue and flushes them asynchronously in the background, so a slow sink doesn't block the request path.

### What does each event contain?

Standard fields include a correlation ID (`S7P_RequestId`), the backend that handled the request (`BackendHost`), its priority (`S7P_Priority`), circuit-breaker status, and retry count, plus request timing and, for streaming AI responses, token usage.

### How are tokens counted for streaming responses?

Standard gateways struggle to count tokens in Server-Sent Events streams because the usage data often only appears in the final chunk. For hosts configured with `processor=OpenAI`, the proxy's stream processor parses the response on the fly — without buffering it — and extracts `Usage.Prompt_Tokens`, `Usage.Completion_Tokens`, and `Usage.Total_Tokens`.

### Can I plug in my own telemetry backend?

Yes — the proxy defines an `IEventClient`/`IHostedService` extensibility point and fans events out to every registered backend through `CompositeEventClient`, so a custom sink runs alongside the built-in ones rather than replacing them.

### What happens if a telemetry backend is misconfigured or fails?

The proxy still starts. A backend that fails to initialize — an unreachable Event Hub, for example — is silently disabled while the others keep running, and an invalid `EVENT_HEADERS` type falls back to the built-in default with a warning.

See [Observability](../concepts/observability.md) for telemetry channel configuration, adding a custom event sink, and the full event schema.
