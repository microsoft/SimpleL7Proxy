# Observability & AI Telemetry

SimpleL7Proxy is designed to provide deep visibility into AI workloads, solving the "Black Box" problem of streaming LLM responses. It captures standard HTTP metrics alongside high-fidelity AI specific telemetry.

## Telemetry Channels
Data is emitted to the following configured sinks:
1.  **Azure Application Insights**: (Recommended for Production) Set `APPINSIGHTS_CONNECTIONSTRING`. Handles structured telemetry (requests, dependencies, exceptions) directly via `TelemetryClient`.
2.  **Azure Event Hubs**: High-volume streaming ingestion. Include `eventhub` in `EVENT_LOGGERS` and set `EVENTHUB_CONNECTIONSTRING` (or `EVENTHUB_NAMESPACE` for managed identity).
3.  **Local Log File**: JSON event log for debugging/testing. Include `file` in `EVENT_LOGGERS` and optionally set `LOGFILE_NAME`.
4.  **Console/Stdout**: For container logging and local debugging.

---

## Event Logging Architecture

Every proxied request produces a `ProxyEvent` — a key/value dictionary containing request metadata, timing, token usage, and other dimensions. These events are serialized to JSON and dispatched to one or more **event logger backends** via the `CompositeEventClient` fan-out.

### How It Works

```
ProxyEvent.SendEvent()
    └── CompositeEventClient.SendData(json)
            ├── LogFileEventClient.SendData(json)   ← if "file" enabled
            ├── EventHubClient.SendData(json)        ← if "eventhub" enabled
            └── CustomLogger.SendData(json)          ← if custom type enabled
```

1. `ProxyEvent` collects per-request data (status, duration, tokens, headers, etc.).
2. Common fields (version, revision, container name) are injected from **Event Headers** (`ICommonEventData`).
3. The event is serialized and passed to `CompositeEventClient.SendData()`.
4. `CompositeEventClient` iterates a `FrozenDictionary` snapshot of registered backends — zero-lock, zero-allocation on the hot path.
5. Each backend buffers events in a `ConcurrentQueue` and flushes them asynchronously on a background loop.

### Configuration Reference

| Environment Variable | Default | Description |
| :--- | :--- | :--- |
| `EVENT_LOGGERS` | `file` | Comma-separated list of backends to enable: `file`, `eventhub`, or a fully-qualified type name. |
| `LOGFILE_NAME` | `eventslog.json` | Output path for the local file logger. |
| `LOGTOFILE` | `false` | **(Legacy)** When `EVENT_LOGGERS` is not set, `true` → `file`, `false` → `eventhub`. |
| `EVENTHUB_CONNECTIONSTRING` | — | Connection string for Azure Event Hubs. |
| `EVENTHUB_NAMESPACE` | — | Event Hub namespace for managed-identity auth (alternative to connection string). |
| `EVENTHUB_NAME` | — | The specific Event Hub name to write to. |
| `EVENT_HEADERS` | `SimpleL7Proxy.Events.CommonEventHeaders` | Fully-qualified type name of the `ICommonEventData` implementation that supplies default event fields. |

### Built-in Backends

**Local Log File** (`file`)
- Writes JSON lines to disk via `LogFileEventClient`.
- Buffers events in a `ConcurrentQueue` and flushes batches of up to 99 events every 500 ms using a reusable `StringBuilder`.
- Suitable for local debugging and testing.

**Azure Event Hubs** (`eventhub`)
- Streams events to Azure Event Hubs via `EventHubClient`.
- Supports connection-string auth or managed-identity auth (`EVENTHUB_NAMESPACE`).
- Batches events using the Event Hubs SDK `EventDataBatch` for throughput.
- If the Event Hub connection fails at startup, the backend is silently disabled and other backends continue.

Both backends are **sibling sinks** managed by `CompositeEventClient` — they can run simultaneously. Set `EVENT_LOGGERS=file,eventhub` to enable both. Each backend self-registers on successful startup; if one fails (e.g., EventHub timeout), the others continue unaffected.

---

## Custom Event Loggers

Besides the built-in `file` and `eventhub` backends, you can create your own event logger by implementing `IEventClient` and `IHostedService` in the `SimpleL7Proxy` assembly.

### Steps

1. Create a class that implements both `IEventClient` and `IHostedService`.
2. Accept `CompositeEventClient` (and any other DI services) in the constructor.
3. In `StartAsync`, perform setup and call `_composite.Add(this)` to register with the fan-out.
4. In `SendData`, process or forward the JSON event string.
5. Reference the class by its **fully-qualified type name** in `EVENT_LOGGERS`.

### Example

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SimpleL7Proxy.Events;

public class ConsoleEventLogger : IEventClient, IHostedService
{
    private readonly CompositeEventClient _composite;
    private readonly ILogger<ConsoleEventLogger> _logger;

    public ConsoleEventLogger(CompositeEventClient composite, ILogger<ConsoleEventLogger> logger)
    {
        _composite = composite;
        _logger = logger;
    }

    public int Count => 0;
    public string ClientType => "Console";
    public Task StopTimerAsync() => Task.CompletedTask;

    public void SendData(string? value)
    {
        if (!string.IsNullOrEmpty(value))
            _logger.LogInformation("[EVENT] {Value}", value);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _composite.Add(this);
        _logger.LogInformation("[SERVICE] ✓ ConsoleEventLogger started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => StopTimerAsync();
}
```

**Usage:**
```
EVENT_LOGGERS=file,SimpleL7Proxy.Events.ConsoleEventLogger
```

The proxy uses `ActivatorUtilities.CreateInstance` to construct custom loggers, so all constructor dependencies are resolved from DI automatically. If instantiation fails, the error is logged and the remaining loggers continue.

> **Security:** Only types within the `SimpleL7Proxy` assembly are resolved. External assemblies cannot be loaded via `EVENT_LOGGERS`.

---

## Custom Event Headers

Every event includes a set of **default fields** (version, revision, container name) that are injected by an `ICommonEventData` implementation. You can customize these fields by providing your own class.

### Default Behaviour

The built-in `CommonEventHeaders` class produces:

| Field | Source |
| :--- | :--- |
| `Ver` | `Constants.VERSION` (build-time) |
| `Revision` | `BackendOptions.Revision` |
| `ContainerApp` | `BackendOptions.ContainerApp` |

### Creating a Custom Implementation

1. Create a class that implements `ICommonEventData`.
2. Accept `IOptions<BackendOptions>` in the constructor.
3. Return a `FrozenDictionary<string, string>` from `DefaultEventData()`.
4. Set `EVENT_HEADERS` to the fully-qualified type name.

```csharp
using System.Collections.Frozen;
using Microsoft.Extensions.Options;
using SimpleL7Proxy.Config;

namespace SimpleL7Proxy.Events;

public class MyCustomEventHeaders : ICommonEventData
{
    private readonly FrozenDictionary<string, string> _data;

    public MyCustomEventHeaders(IOptions<BackendOptions> options)
    {
        var bo = options.Value;
        _data = new Dictionary<string, string>
        {
            ["Ver"] = Constants.VERSION,
            ["Revision"] = bo.Revision,
            ["ContainerApp"] = bo.ContainerApp,
            ["Region"] = Environment.GetEnvironmentVariable("AZURE_REGION") ?? "unknown",
            ["Tenant"] = Environment.GetEnvironmentVariable("TENANT_ID") ?? "default"
        }.ToFrozenDictionary();
    }

    public FrozenDictionary<string, string> DefaultEventData() => _data;
}
```

**Usage:**
```
EVENT_HEADERS=SimpleL7Proxy.Events.MyCustomEventHeaders
```

### Fallback Behaviour

If the configured `EVENT_HEADERS` type cannot be found, does not implement `ICommonEventData`, or throws during construction, the proxy logs a warning and falls back to `CommonEventHeaders` automatically. The proxy will always start successfully regardless of event header misconfiguration.

## AI Token Metrics (Streaming)
Standard gateways cannot count tokens in streaming responses (Server-Sent Events/SSE) because the "usage" field is often only sent in the final chunk, or requires aggregating chunks.

SimpleL7Proxy uses a specialized **Stream Processor** to parse response bodies on-the-fly without buffering the full response (which would add latency).

### Extracted Metrics
For requests routed to a host with `processor=OpenAI` configured, the following metrics are automatically extracted and logged:

| Metric Field | Description |
| :--- | :--- |
| **`Usage.Prompt_Tokens`** | Number of tokens in the input prompt. |
| **`Usage.Completion_Tokens`** | Number of tokens generated in the response. |
| **`Usage.Total_Tokens`** | Total billable tokens for the request. |

### Log Schema
These metrics appear in the **Custom Dimensions** of the `Request` or `Event` telemetry in Application Insights.

**Kusto Query Example (App Insights):**
```kusto
requests
| where customDimensions contains "Usage.Total_Tokens"
| project 
    timestamp, 
    Duration = duration, 
    TotalTokens = toint(customDimensions["Usage.Total_Tokens"]),
    PromptTokens = toint(customDimensions["Usage.Prompt_Tokens"]),
    Model = customDimensions["Model"]
| summarize avg(TotalTokens) by bin(timestamp, 1h)
```

## Standard Request Telemetry
Every request includes standard fields useful for operational monitoring:

*   **`S7P_RequestId`**: Unique correlation ID.
*   **`BackendHost`**: The specific backend URL that handled the request.
*   **`S7P_Priority`**: The priority queue assigned to the request.
*   **`CircuitBreakerStatus`**: Whether the host was healthy.
*   **`Retries`**: Number of Retry attempts performed.

## Tuning Logging
*   **`LogAllRequestHeaders` / `LogAllResponseHeaders`**: Enable full header capture for debugging (be careful with PII/Secrets).
*   **`LogAllRequestHeadersExcept`**: Blacklist sensitive headers (e.g., `Authorization`, `api-key`) to prevent leaking credentials.
