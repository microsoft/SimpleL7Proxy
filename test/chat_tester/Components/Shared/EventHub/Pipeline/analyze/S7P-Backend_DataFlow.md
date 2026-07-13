# S7P-Backend Event Data Flow & Display

## The Event Record
```json
{
  "Timestamp":"2026-06-24T18:33:07.0472207Z",
  "1-Status":"✓ Active",
  "1-Errors":"0",
  "ActiveHostsCount":"1",
  "SuccessRate":"0.8",
  "LoadBalanceMode":"latency",
  "1-Average":"0",
  "1-Host":"APIM backend | Auth=key | https://nvmtr2apim.azure-api.net | Path: / | Probe: /status-0123456789abcdef",
  "1-Latency":"474.794",
  "1-Calls":"0",
  "1-SuccessRate":"100",
  "Ver":"2.2.13",
  "Replica":"",
  "ContainerApp":"ContainerAppName",
  "Type":"S7P-Backend",
  "MID":"",
  "Status":"0",
  "Method":"GET"
}
```

---

## Where Is This Data Generated?

**Source File:** [src/SimpleL7Proxy/Backend/EndpointMonitorService.cs](../../src/SimpleL7Proxy/Backend/EndpointMonitorService.cs#L437)

**Method:** `DisplayHostStatus()` (runs every 60 seconds)

**Purpose:** Backend health monitoring service polls each backend host and reports:
- Status (✓ Active or ✗ Below threshold)
- Latency measurements
- Success rates
- Call counts
- Error counts

The event is built dynamically for each backend with a numbered suffix:
- `1-Status`, `1-Latency`, `1-Host` = Backend #1
- `2-Status`, `2-Latency`, `2-Host` = Backend #2 (if multiple backends)
- etc.

---

## How Data Flows Through The System

### 1. **Generation** 
   - [EndpointMonitorService.cs](../../src/SimpleL7Proxy/Backend/EndpointMonitorService.cs#L437-L490)
   - `DisplayHostStatus()` builds the event with fields like:
     - `1-Host`: Full backend description (from `BaseHostHealth.ToString()`)
     - `1-Status`: "✓ Active" or "✗ Below threshold"
     - `1-Latency`: Average latency in ms
     - `1-SuccessRate`: Success percentage (0-100)
     - `1-Calls`: Total calls to this backend
     - `1-Errors`: Total errors from this backend
     - `1-Average`: Average response time
     - `ActiveHostsCount`: Number of healthy backends
     - `LoadBalanceMode`: Load balancing strategy (latency/roundrobin/random)
     - `SuccessRate`: Overall success threshold

### 2. **Event Sent to EventHub**
   - Event is sent via `_statusEvent.SendEvent()`
   - Transmitted to EventHub connector/test clients

### 3. **Parsing in Test Client**
   - **File:** [test/chat_tester/Components/Shared/EventHub/Pipeline/BackendPipelineProcessor.cs](Components/Shared/EventHub/Pipeline/BackendPipelineProcessor.cs)
   - Filters for events with `Type = "S7P-Backend"`
   - Extracts all fields into a `backend` dictionary
   - **File:** [test/chat_tester/Components/Shared/EventHub/ProxyMetricsCatalog.cs](Components/Shared/EventHub/ProxyMetricsCatalog.cs)
   - Parses the numbered fields (1-Status, 1-Latency, etc.)
   - Converts into `BackendHealthSnapshot` objects

### 4. **Model Conversion**
   - **File:** [test/chat_tester/Components/Shared/EventHub/EventHubMonitorModels.cs](Components/Shared/EventHub/EventHubMonitorModels.cs)
   - Creates `BackendHealthSnapshot` record with:
     - `Name`: Backend identifier
     - `Url`: Backend URL
     - `Status`: "Active" or "Below threshold"
     - `LatencyMs`: Numeric latency value
     - `SuccessRate`: Percent
     - `Calls`: Total calls
     - `Errors`: Total errors
     - `Css`: CSS class ("healthy" or "degraded")

### 5. **Display in Web UI**
   - **File:** [test/chat_tester/Components/Pages/EventHubMonitorPage.razor](Components/Pages/EventHubMonitorPage.razor)
   - **Page:** `/eventhub` route
   - Displays in the **Backends** card (left panel)

---

## UI Display Location

### **Page:** EventHub Monitor (`/eventhub`)

### **Section:** Left Panel - "Backends" Card

Each backend is displayed as a **backend tile** showing:

```
┌─ Backend: [Name] ─────────────────────┐
│ ✓ Active                              │
│ URL: https://nvmtr2apim.azure-api.net│
│ Lat: 474.794 ms                       │
│ ProbeOK: 1                            │
│ ProbeFail: 0                          │
│ ReqCalls: 0                           │
│ ReqFail: 0                            │
│ ReqAvg: 0.0 ms                        │
└───────────────────────────────────────┘
```

### **Fields Displayed:**
- **Status indicator (dot):** Color-coded (green=healthy, red=degraded)
- **Status text:** "✓ Active" or "✗ Below threshold"
- **Backend URL:** Full URL with path/probe info
- **Latency:** `1-Latency` (in milliseconds)
- **ProbeOK:** Successful probes
- **ProbeFail:** Failed probes
- **ReqCalls:** Total request calls
- **ReqFail:** Request failures
- **ReqAvg:** Average request latency

### **Update Frequency:**
- Every 60 seconds (backend poller cycle)
- Real-time live indicator shows "Live · updated HH:mm:ss"

---

## Field Mapping: Record → UI

| JSON Field | UI Display | Location |
|---|---|---|
| `1-Status` | Status indicator + text | Tile header |
| `1-Host` | URL | Tile body |
| `1-Latency` | "Lat: X ms" | Metrics section |
| `1-SuccessRate` | Part of calculations | Backend model |
| `1-Calls` | Request call count | Metrics section |
| `1-Errors` | Request failure count | Metrics section |
| `1-Average` | "ReqAvg: X ms" | Metrics section |
| `ActiveHostsCount` | "Backends: N" subtitle | Card header |
| `LoadBalanceMode` | Displayed in metrics catalog | Right panel |
| `SuccessRate` | Overall success rate | Runtime stats |
| `Ver` | Proxy version | Metrics catalog |

---

## Related Components

- **Event Hub Reader:** [EventHubReader.cs](Components/Shared/EventHub/EventHubReader.cs#L418) - Routes S7P-Backend events
- **Metrics Catalog:** [ProxyMetricsCatalog.cs](Components/Shared/EventHub/ProxyMetricsCatalog.cs#L272) - Extracts metrics
- **Monitor Store:** [EventHubMonitorStore.cs](Components/Shared/EventHub/EventHubMonitorStore.cs) - Maintains state
- **Monitor Models:** [EventHubMonitorModels.cs](Components/Shared/EventHub/EventHubMonitorModels.cs) - Data contracts

