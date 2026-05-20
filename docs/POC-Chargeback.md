# POC: Token-Level Chargeback

## Overview

This POC shows how SimpleL7Proxy captures token usage from streaming Azure OpenAI responses and emits it as structured telemetry — without buffering the response or adding meaningful latency. Once the data is in Application Insights, a single KQL query can break down consumption by user, priority tier, or backend, giving you the raw numbers needed for internal chargeback or cost reporting.

The goal is to verify that:

1. Token counts (`prompt_tokens`, `completion_tokens`, `total_tokens`) are extracted from the SSE stream and appear in Application Insights custom dimensions.
2. The `userId` header flows through to telemetry, so consumption can be attributed to an individual caller.
3. A KQL query can aggregate total tokens per user over a time window.

The LLM Simulator covers all three cases. Its sample files return real `usage` blocks in the same format Azure OpenAI uses, so the proxy's stream processor extracts and logs the same fields it would against a real endpoint.

---

## How it works

The proxy includes stream processors that read the SSE or JSON response stream on-the-fly and extract token usage without buffering the full response. The processor to use depends on the provider (LLM model):

| Provider | `processor=` value | Usage fields logged |
|----------|--------------------|---------------------|
| Azure OpenAI / OpenAI | `OpenAI` | `Usage.Prompt_Tokens`, `Usage.Completion_Tokens`, `Usage.Total_Tokens` |
| Anthropic | `AllUsage-2` | `Usage.Input_Tokens`, `Usage.Output_Tokens` |
| Google Gemini | `MultiLineAllUsage` | `Usage.PromptTokenCount`, `Usage.CandidatesTokenCount`, `Usage.TotalTokenCount` |

All processors attach their extracted values to the `ProxyEvent` for that request and write them to every configured telemetry sink — Application Insights, Event Hubs, and the local file logger all receive the same token fields. This POC focuses on Application Insights. For Event Hubs see the tuning section below.

<details>
<summary>Custom dimensions emitted per request</summary>

| Custom Dimension | Content |
|------------------|---------|
| `Usage.Prompt_Tokens` | Tokens consumed by the input prompt |
| `Usage.Completion_Tokens` | Tokens generated in the response |
| `Usage.Total_Tokens` | Sum of prompt + completion |
| `S7P_RequestId` | Unique request correlation ID |
| `S7P_Priority` | Priority queue the request was assigned to |
| `BackendHost` | Backend URL that served the request |

</details>

The `userId` header value is forwarded to the backend and appears in request telemetry, enabling per-user attribution.

> **Note:** Token extraction requires the appropriate `processor=` value on the backend host configuration (see table above). Without it, the proxy forwards the stream transparently but does not parse usage.

---

## Prerequisites

- SimpleL7Proxy running locally or on ACA, pointed at the LLM Simulator.
- `processor=OpenAI` set on the backend host (see [Backend Configuration](#backend-configuration) below).
- Application Insights connected via `APPINSIGHTS_CONNECTIONSTRING`.
- The LLM Simulator deployed as an Azure Function. See [`test/LLMSimulator/Readme.md`](../test/LLMSimulator/Readme.md) — the fastest path is the portal ZIP deploy. Verify it's up:
  ```bash
  curl https://<funcapp>.azurewebsites.net/api/health
  # → 200 OK
  ```

---

## Backend Configuration

<details>
<summary>Direct backend</summary>

Set one `Host` environment variable per provider. The `path=` prefix tells the proxy which incoming URL paths belong to that host, `processor=` selects the right token extractor, and `mode=direct` disables health probing (appropriate for Azure Functions which scale to zero). Only configure the hosts you actually need — all three are not required.

```bash
# Azure OpenAI — handles requests to /openai/...
export Host1="host=https://<funcapp>.azurewebsites.net;mode=direct;path=/openai;processor=OpenAI"

# Anthropic — handles requests to /anthropic/...
export Host2="host=https://<funcapp>.azurewebsites.net;mode=direct;path=/anthropic;processor=AllUsage-2"

# Google Gemini — handles requests to /v1beta/...
export Host3="host=https://<funcapp>.azurewebsites.net;mode=direct;path=/v1beta;processor=MultiLineAllUsage"
```

</details>

<details>
<summary>APIM</summary>

With APIM the processor is not set in the host config. Instead, APIM returns it as the `TOKENPROCESSOR` response header. The proxy reads that header from each `200 OK` response and selects the processor dynamically — useful when a single APIM gateway fronts multiple models and the policy knows which backend was actually called.

The included policy already does this in its `<outbound>` block:

```xml
<outbound>
    <set-header name="TOKENPROCESSOR" exists-action="override">
        <value>MultiLineAllUsage</value>
    </set-header>
    ...
</outbound>
```

Change the value to match the model family the policy is routing to (`OpenAI`, `AllUsage-2`, or `MultiLineAllUsage`). If the policy routes to multiple providers, use a policy expression to set it conditionally based on whichever backend was selected.

The host config does not need a `processor=` value — the header overrides it at runtime. Use `mode=apim` with a probe path so the proxy health-checks the gateway:

```bash
export Host1="host=https://<apim>.azure-api.net;mode=apim;probe=/status-0123456789abcdef"
```

</details>

```bash
export APPINSIGHTS_CONNECTIONSTRING="InstrumentationKey=..."
export Workers=5
dotnet run --project src/SimpleL7Proxy
```

---

## Sending Test Requests

The simulator returns deterministic responses with realistic `usage` blocks — the same JSON structure the real providers return. Because the token counts are fixed, you can verify telemetry exactly: if the KQL query shows 1058 total tokens for an OpenAI call, the stream processor is working correctly end-to-end. Send at least a few requests across two different `userId` values so the chargeback query has something meaningful to aggregate.

<details>
<summary>curl commands</summary>

**Azure OpenAI (`processor=OpenAI`) — 58 prompt / 1000 completion / 1058 total:**
```bash
curl -i \
  -H "userId: alice" \
  -H "Content-Type: application/json" \
  -d '{"model":"gpt-4o-mini","messages":[{"role":"user","content":"hello"}],"stream":true}' \
  "http://localhost:8000/openai/deployments/gpt-4o-mini/chat/completions"
```

**Anthropic (`processor=AllUsage-2`) — 10 input / 35 output:**
```bash
curl -i \
  -H "userId: alice" \
  -H "Content-Type: application/json" \
  -d '{"model":"claude-sonnet-3-5","messages":[{"role":"user","content":"hello"}]}' \
  "http://localhost:8000/anthropic/v1/messages"
```

**Gemini (`processor=MultiLineAllUsage`) — 6 prompt / 19 candidates / 1465 total (includes thinking tokens):**
```bash
curl -i \
  -H "userId: alice" \
  -H "Content-Type: application/json" \
  -d '{"contents":[{"role":"user","parts":[{"text":"hello"}]}]}' \
  "http://localhost:8000/v1beta/models/gemini-2.5-pro:generateContent"
```

**Batch — two users, multiple requests (OpenAI):**
```bash
for i in {1..5}; do
  curl -s -o /dev/null \
    -H "userId: alice" \
    -H "Content-Type: application/json" \
    -d '{"model":"gpt-4o-mini","messages":[{"role":"user","content":"hello"}],"stream":true}' \
    "http://localhost:8000/openai/deployments/gpt-4o-mini/chat/completions" &
done

for i in {1..3}; do
  curl -s -o /dev/null \
    -H "userId: bob" \
    -H "Content-Type: application/json" \
    -d '{"model":"gpt-4o-mini","messages":[{"role":"user","content":"hello"}],"stream":true}' \
    "http://localhost:8000/openai/deployments/gpt-4o-mini/chat/completions" &
done

wait
echo "Done"
```

</details>

After a few seconds, the events will appear in Application Insights.

---

## Verifying the Data

Now that the proxy is running and sample requests have been sent, it's time to verify that token data is flowing through correctly. The proxy writes a `customEvent` to Application Insights for every completed request, with token counts in `customDimensions`. Because the simulator returns fixed token counts, the numbers are deterministic — if the results match the expected values below, the full pipeline (stream parsing → telemetry emission → ingestion) is confirmed working.

The field names vary by provider: Azure OpenAI uses `Usage.Prompt_Tokens` / `Usage.Completion_Tokens` / `Usage.Total_Tokens`; Anthropic uses `Usage.Input_Tokens` / `Usage.Output_Tokens`; Gemini uses `Usage.PromptTokenCount` / `Usage.CandidatesTokenCount` / `Usage.TotalTokenCount`. The queries and log checks below use the OpenAI fields — adapt the field names if you're testing a different provider.

<details>
<summary>Application Insights</summary>

Open the Log Analytics workspace linked to your Application Insights resource and run:

```kusto
customEvents
| where timestamp > ago(1h)
| where customDimensions contains "Usage.Total_Tokens"
| project
    timestamp,
    UserId       = tostring(customDimensions["userId"]),
    Priority     = tostring(customDimensions["S7P_Priority"]),
    Backend      = tostring(customDimensions["BackendHost"]),
    PromptTokens = toint(customDimensions["Usage.Prompt_Tokens"]),
    CompTokens   = toint(customDimensions["Usage.Completion_Tokens"]),
    TotalTokens  = toint(customDimensions["Usage.Total_Tokens"])
| summarize
    Requests     = count(),
    TotalTokens  = sum(TotalTokens),
    PromptTokens = sum(PromptTokens),
    CompTokens   = sum(CompTokens)
    by UserId, Priority
| order by TotalTokens desc
```

Expected result for the batch above (simulator returns 1058 tokens per call):

| UserId | Priority | Requests | TotalTokens | PromptTokens | CompTokens |
|--------|----------|----------|-------------|--------------|------------|
| alice  | 1        | 5        | 5290        | 290          | 5000       |
| bob    | 1        | 3        | 3174        | 174          | 3000       |

To break down by backend — useful when multiple deployments serve different tiers:

```kusto
customEvents
| where timestamp > ago(1h)
| where customDimensions contains "Usage.Total_Tokens"
| summarize
    TotalTokens = sum(toint(customDimensions["Usage.Total_Tokens"])),
    Requests    = count()
    by UserId = tostring(customDimensions["userId"]),
       Backend = tostring(customDimensions["BackendHost"])
| order by TotalTokens desc
```

</details>

<details>
<summary>Event Hubs</summary>

Set `EVENT_LOGGERS=eventhub` and `EVENTHUB_CONNECTIONSTRING` to enable the Event Hubs sink. The proxy emits the same JSON envelope it writes to the file log, so every token field is present in the event body.

**Capture to a storage account:** Enable the Event Hubs Capture feature on the hub to land events as Avro files in Azure Blob Storage automatically. This gives you a durable, queryable archive without building a consumer.

**Query with ADX (Azure Data Explorer):** Connect ADX to the hub or to the captured Blob Storage container using an external table or continuous ingestion. Once the data is in ADX you can run the equivalent chargeback query:

```kusto
ProxyEvents
| where UsageTotalTokens > 0
| summarize
    Requests    = count(),
    TotalTokens = sum(UsageTotalTokens),
    PromptTokens = sum(UsagePromptTokens),
    CompTokens  = sum(UsageCompletionTokens)
    by UserId, Priority
| order by TotalTokens desc
```

Other tools that work directly with Event Hubs or Blob-captured data include Fabric Real-Time Intelligence, Stream Analytics, and Azure Synapse.

</details>

<details>
<summary>Local File</summary>

If `EVENT_LOGGERS=file` (the default), token data appears in `eventslog.json` immediately — no ingestion delay. Useful for a quick sanity check before querying Application Insights.

If the proxy is deployed to Azure Container Apps, the file is written inside the container. Use the ACA console to inspect it: in the Azure portal, open the container app → **Containers** → **Console**, then run the `jq` command below. For a more durable setup, ACA supports mounting an Azure Files share as a volume — configure a storage mount in the container app and set the proxy's working directory to that path so `eventslog.json` persists across container restarts.

```bash
cat eventslog.json | jq 'select(."Usage.Total_Tokens" != null) | {"user": .userId, "total": ."Usage.Total_Tokens", "backend": .BackendHost}'
```

Expected output per OpenAI request:
```json
{
  "user": "alice",
  "total": "1058",
  "backend": "https://<funcapp>.azurewebsites.net"
}
```

</details>

<details>
<summary>None</summary>

If `EVENT_LOGGERS` is not set or is set to an empty value, telemetry is turned off and nothing is captured. Requests are still proxied normally, but no token data, request events, or usage metrics are written anywhere. Set at least one logger before running this POC.

</details>

---

## Tuning and Further Exploration

Once the basic data is confirmed, a few variations are worth trying:

<details>
<summary>Stream Analytics + Power BI dashboard</summary>

With `EVENT_LOGGERS=eventhub`, every request event lands in the hub in real time. Connect an Azure Stream Analytics job to the hub and project the token fields into an output — a Power BI streaming dataset works well here. You can build a live dashboard showing token consumption by user, priority tier, and backend, updating as requests arrive. This is the closest thing to a real-time chargeback view without any custom code.

For a batch approach, use the Event Hubs Capture output (Avro files in Blob Storage) as a Power BI dataflow source or import it into a Fabric lakehouse for scheduled reporting.

</details>

<details>
<summary>Add a second backend by tier</summary>

Use `acceptablePriorities` to route priority-1 to a "premium" backend and priority-3 to a "standard" one. The `BackendHost` dimension in telemetry then lets you split cost by tier automatically in any of the queries above.

</details>

<details>
<summary>Increase concurrency</summary>

Raise `Workers` and send a larger burst. Watch `eventslog.json` — every line should have a `Usage.Total_Tokens` entry. Missing entries indicate the stream was closed before the final usage chunk arrived (rare with the simulator; common if a real backend is configured without `processor=OpenAI`).

</details>

---

## Related Documentation

- [POC-Priority-configuration.md](POC-Priority-configuration.md) — Routing requests across backends by priority tier
- [POC-Failover-configuration.md](POC-Failover-configuration.md) — Automatic failover and retry behaviour when a backend is slow or unavailable
- [OBSERVABILITY.md](OBSERVABILITY.md) — Token metrics, telemetry channels, and event logger configuration
- [BACKEND_HOSTS.md](BACKEND_HOSTS.md) — `processor=` and other host connection string options
