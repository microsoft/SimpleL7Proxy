# Observability & AI Telemetry

SimpleL7Proxy is designed to provide deep visibility into AI workloads, solving the "Black Box" problem of streaming LLM responses. It captures standard HTTP metrics alongside high-fidelity AI specific telemetry.

## Telemetry Channels
Data is emitted to the following configured sinks:
1.  **Azure Application Insights**: (Recommended for Production) Set `APPINSIGHTS_CONNECTIONSTRING`. Handles structured telemetry (requests, dependencies, exceptions) directly via `TelemetryClient`.
2.  **Azure Event Hubs**: High-volume streaming ingestion. Include `eventhub` in `EVENT_LOGGERS` and set `EVENTHUB_CONNECTIONSTRING` (or `EVENTHUB_NAMESPACE` for managed identity).
3.  **Local Log File**: JSON event log for debugging/testing. Include `file` in `EVENT_LOGGERS` and optionally set `LOGFILE_NAME`.
4.  **Console/Stdout**: For container logging and local debugging.

Event Hubs and Local Log File are **sibling backends** managed by the `CompositeEventClient` — they can run simultaneously. Set `EVENT_LOGGERS=file,eventhub` to enable both. Each backend self-registers on successful startup; if one fails (e.g., EventHub timeout), the others continue unaffected.

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
