# POC: LLM Chargeback

_Track per-user token consumption across Azure OpenAI, Anthropic, and Gemini using SimpleL7Proxy as a transparent passthrough._

## Overview

> **TL;DR** — Point SimpleL7Proxy at your LLM endpoint, send requests with a `userId` header, then run a KQL query in Application Insights to see per-user token consumption.

When multiple teams share an LLM deployment, you need a way to attribute token costs back to each caller. A PTU (Provisioned Throughput Unit) makes this especially important — it's a fixed-capacity Azure OpenAI deployment billed at a flat rate 24×7, so sharing one across teams is cost-effective only if you can track each team's consumption.

SimpleL7Proxy solves this as a transparent passthrough: it reads the response stream, extracts token counts, and writes them to Application Insights — without buffering or added latency, and with no changes to your clients or backends. A single KQL query then breaks down consumption by user, tier, or backend.

By the end of this walkthrough you will have confirmed:

1. Token counts appear in Application Insights custom dimensions.
2. Requests are attributed to individual callers via the `userId` header.
3. A KQL query can aggregate tokens per user over any time window.

You can generate traffic three ways: AI Foundry, a live Azure OpenAI endpoint, or the LLM Simulator. The simulator is the fastest path — its responses use the same `usage` format as the real Azure OpenAI API.

After the POC, you will have the opportuinity to extend the telemetry to other Azure services such as an EventHub, Stream Analytics and even PowerBI.

---

## How it works

The proxy streams responses and simultaneously extracts token metrics. It supports `Azure OpenAI`, `Anthropic`, and `Google Gemini` out of the box.

Each completed request writes a `customEvent` to Application Insights with token counts and routing metadata in `customDimensions`. The fields logged depend on the provider — expand for the full list.

<details>
<summary>Custom dimensions by provider</summary>

The `processor=` value on the backend host determines which usage fields are extracted:

| Provider | `processor=` value | Usage fields logged |
|----------|--------------------|---------------------|
| Azure OpenAI / OpenAI | `OpenAI` | `Usage.Prompt_Tokens`, `Usage.Completion_Tokens`, `Usage.Total_Tokens` |
| Anthropic | `AllUsage-2` | `Usage.Input_Tokens`, `Usage.Output_Tokens` |
| Google Gemini | `MultiLineAllUsage` | `Usage.PromptTokenCount`, `Usage.CandidatesTokenCount`, `Usage.TotalTokenCount` |

Every request also includes: `S7P_RequestId` (correlation ID), `S7P_Priority` (queue assigned), `BackendHost` (URL that served the request).

</details>

> **Note:** Without a `processor=` value from the backend host, the proxy forwards the stream without parsing token usage.

---

## Prerequisites

- The LLM Simulator deployed as an Azure Function or an OpenAI endpoint — see [`test/LLMSimulator/Readme.md`](../test/LLMSimulator/Readme.md) for setup.
- SimpleL7Proxy running locally or on ACA.
- `processor=` configured to match the model.
- Application Insights connected via `APPINSIGHTS_CONNECTIONSTRING`.



---

## Step 1. Validate endpoint connectivity

Confirm your backend is reachable before configuring the proxy.

**LLM Simulator:**
```bash
curl -i https://<funcapp>.azurewebsites.net/api/health
# → 200 OK
```

**APIM** — run both:
```bash
# Health probe
curl -i https://<apim-name>.azure-api.net/status-0123456789abcdef
# → 200 OK

# End-to-end LLM call (api prefix /openai)
curl -i \
  -H "userId: alice" \
  -H "Content-Type: application/json" \
  -d '{"model":"gpt-4o-mini","messages":[{"role":"user","content":"hello"}],"stream":true}' \
  "https://<apim-name>.azure-api.net/openai/deployments/gpt-4o-mini/chat/completions"
```

## Step 2. Configure a backend in the proxy

Set the `Host1` environment variable pointing to your backend. Choose the option that matches your setup:

<details>
<summary>Direct backend — LLM Simulator or a raw Azure OpenAI endpoint</summary>

Pick one — all three are not required. If using a real backend, ensure that `mode=direct` exists which disables health probing.

```bash
# Azure OpenAI — handles requests to /openai/...
export Host1="host=https://<funcapp>.azurewebsites.net;mode=direct;path=/openai;processor=OpenAI"

# Anthropic — handles requests to /anthropic/...
export Host1="host=https://<funcapp>.azurewebsites.net;mode=direct;path=/anthropic;processor=AllUsage-2"

# Google Gemini — handles requests to /v1beta/...
export Host1="host=https://<funcapp>.azurewebsites.net;mode=direct;path=/v1beta;processor=MultiLineAllUsage"
```

</details>

<details>
<summary>APIM — routing through Azure API Management</summary>

APIM can run multiple endpoints and adds governance, security, and rate limiting. Use `mode=apim` with a probe path so the proxy can health-check the gateway:

```bash
export Host1="host=https://<apim>.azure-api.net;mode=apim;probe=/status-0123456789abcdef"
```

Because APIM can route to multiple backends, the processor is determined per request from a `TOKENPROCESSOR` response header — no `processor=` value is needed in the host config. Set it in the APIM policy's `<outbound>` block:

```xml
<outbound>
    <set-header name="TOKENPROCESSOR" exists-action="override">
        <value>MultiLineAllUsage</value>
    </set-header>
    ...
</outbound>
```

Change the value to match the model family your policy routes to (`OpenAI`, `AllUsage-2`, or `MultiLineAllUsage`). For policies that route to multiple providers, set it conditionally using a policy expression.

</details>

---

## Step 3. Configure Application Insights

Set `APPINSIGHTS_CONNECTIONSTRING` to your Application Insights connection string. See [CONFIGURATION_SETTINGS.md](CONFIGURATION_SETTINGS.md) for all environment variable options covering endpoints, logging, workers, and timeouts.

> **Note:** How you set these variables depends on your deployment: local shell environment variables, ACA environment variables in the container app configuration, or Azure App Configuration if you have that integration enabled.

---

## Step 4. Send a test request

Send one request to confirm the pipeline is working:

```bash
curl -i \
  -H "userId: alice" \
  -H "Content-Type: application/json" \
  -d '{"model":"gpt-4o-mini","messages":[{"role":"user","content":"hello"}],"stream":true}' \
  "http://localhost:8000/openai/deployments/gpt-4o-mini/chat/completions"
```

The simulator returns fixed counts (58 prompt / 1000 completion / 1058 total) — look for `1058` in the KQL results to confirm end-to-end flow. For Anthropic or Gemini, expand below.

<details>
<summary>Anthropic and Gemini commands</summary>

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

</details>


---

## Verifying the first pass

The proxy writes a `customEvent` to Application Insights for every completed request, with token counts in `customDimensions`. The simulator returns fixed counts (58 prompt / 1000 completion / 1058 total) 

The queries below use OpenAI field names. For Anthropic or Gemini, substitute the field names from the [provider table above](#how-it-works).


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

If the query shows 1058 total tokens per request, the full pipeline is working. For a real endpoint, the counts will match the actual usage from your test request.

---

### Send More Data

For a chargeback test across multiple users, send the batch below before running the queries:

```bash
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
After a few minutes, re-run the query above. Expected result for the batch above (simulator returns 1058 tokens per call):

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

---

## Sending data to additional data sinks

Set `EVENT_LOGGERS` to one or more of `appinsights`, `eventhub`, or `file` (comma-separated). See [CONFIGURATION_SETTINGS.md](CONFIGURATION_SETTINGS.md) for all options.

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

- [BEGINNER_DEVELOPMENT.md](BEGINNER_DEVELOPMENT.md) — Running the proxy locally for the first time
- [CONTAINER_DEPLOYMENT.md](CONTAINER_DEPLOYMENT.md) — Deploying to Azure Container Apps
- [CONFIGURATION_SETTINGS.md](CONFIGURATION_SETTINGS.md) — Full reference for all environment variables
- [POC-Priority-configuration.md](POC-Priority-configuration.md) — Routing requests across backends by priority tier
- [POC-Failover-configuration.md](POC-Failover-configuration.md) — Automatic failover and retry behaviour when a backend is slow or unavailable
- [OBSERVABILITY.md](OBSERVABILITY.md) — Token metrics, telemetry channels, and event logger configuration
- [BACKEND_HOSTS.md](BACKEND_HOSTS.md) — `processor=` and other host connection string options
