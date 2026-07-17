# S7P-CircuitBreakerError Event Data Flow & Display

## The Event Record
```json
{
  "Success":"False",
  "Count":"1",
  "Code":"408",
  "Time":"06/24/2026 12:40:32",
  "Ver":"2.2.13",
  "Replica":"",
  "ContainerApp":"ContainerAppName",
  "Type":"S7P-CircuitBreakerError",
  "MID":"",
  "Status":"0",
  "Method":"GET"
}
```

---

## What This Event Means

**S7P-CircuitBreakerError** = Server-level circuit breaker triggered

**Fields:**
- **Code**: HTTP status code that triggered the circuit breaker (408 = Timeout, 500 = Server Error, etc.)
- **Count**: Occurrence count of this specific error pattern
- **Success**: "False" = circuit breaker is in error state
- **Time**: When the circuit breaker event occurred
- **Ver**: Proxy version
- **Status**: "0" (not applicable for circuit breaker events)

---

## Where Is This Data Generated?

**Source:** [src/SimpleL7Proxy/Backend/CircuitBreaker.cs](../../src/SimpleL7Proxy/Backend/CircuitBreaker.cs) 
(or wherever the proxy sends circuit breaker events)

**Trigger:** Server-level circuit breaker activates when:
- Multiple requests fail with 5xx errors (500, 502, 503, etc.)
- Multiple timeout errors (408)
- Error threshold exceeded

**Event Lifecycle:**
1. Circuit breaker condition detected in proxy
2. ProxyEvent of type `S7P-CircuitBreakerError` is created with the error code and count
3. Event is sent to EventHub

---

## How Data Flows Through The System

### 1. **Event Received by Test Client**
   - **File:** [test/chat_tester/Components/Shared/EventHub/EventHubReader.cs](Components/Shared/EventHub/EventHubReader.cs#L428-L432)
   - **Handler:** Identifies event type as `S7P-CircuitBreakerError`

### 2. **Processing**
   ```csharp
   case "S7P-CircuitBreakerError":
       TrackLifecycleEvent(eventData, eventType);           // Records Code/Time in lifecycle
       _store.MarkServerCircuitBreakerSignal();             // Marks server CB as OPEN
       return false;
   ```

### 3. **Lifecycle Tracking** 
   - **File:** [test/chat_tester/Components/Shared/EventHub/EventHubReader.cs](Components/Shared/EventHub/EventHubReader.cs#L562)
   - **Method:** `TrackLifecycleEvent()`
   - Captures:
     - Timestamp (from `Time` field)
     - Status/Code (HTTP error code)
     - Event type
     - Stores in `_requestLifecycle` dictionary

### 4. **Store Update**
   - **File:** [test/chat_tester/Components/Shared/EventHub/EventHubMonitorStore.cs](Components/Shared/EventHub/EventHubMonitorStore.cs#L130-L137)
   - **Method:** `MarkServerCircuitBreakerSignal()`
   - Sets: `_serverCircuitBreakerOpen = true`
   - Raises: `Changed` event to notify UI

### 5. **Snapshot Creation**
   - **File:** [test/chat_tester/Components/Shared/EventHub/EventHubMonitorStore.cs](Components/Shared/EventHub/EventHubMonitorStore.cs#L244)
   - Creates `RuntimeStatsSnapshot` with:
     - `ServerCircuitBreakerOpen = true`
     - `EndpointCircuitBreakerOpenCount` (related endpoint CBreakers)

### 6. **UI Rendering**
   - **File:** [test/chat_tester/Components/Pages/EventHubMonitorPage.razor](Components/Pages/EventHubMonitorPage.razor#L761-L768)
   - Displays stat tile for circuit breaker status

---

## UI Display Location

### **Page:** EventHub Monitor (`/eventhub`)

### **Section:** Right Panel - "Runtime stats" Area

### **Displayed As:** "Circuit breaker" Stat Tile

```
┌─ CIRCUIT BREAKER ─────────────────┐
│ Status: OPEN (or CLOSED)          │
│ Scope: server level               │
│ ───────────────────────────────── │
│ Endpoint open: 0/5                │
│ Scope: server + endpoint          │
└───────────────────────────────────┘
```

### **Tile Properties:**
- **Title:** "Circuit breaker"
- **Primary Value:** 
  - **OPEN** (red/warning) = S7P-CircuitBreakerError received
  - **CLOSED** (green/success) = No circuit breaker active
- **Secondary:** "server level" (indicates scope)
- **Color:**
  - Red/Warning when `ServerCircuitBreakerOpen = true`
  - Green/Success when `ServerCircuitBreakerOpen = false`
- **Metrics:**
  - **Endpoint open:** Count of endpoint circuit breakers currently open vs total
  - **Scope:** "server + endpoint" (shows both server and endpoint CB tracked)

### **Update Behavior:**
- Updates immediately when S7P-CircuitBreakerError event received
- Remains OPEN until server recovers and circuit breaker resets
- Linked to request success/failure patterns

---

## Related Information Displayed

The circuit breaker status is also reflected in:

1. **Runtime Stats Card** (same page, right panel):
   - Overall success rate
   - Failed request count
   - Server latency

2. **Request History** (bottom of same page):
   - Each request shows if it hit the circuit breaker
   - Status shows circuit breaker rejection

3. **Request Lifecycle** (in request details):
   - Shows the exact timeline of when circuit breaker triggered
   - Records the HTTP status code that caused it (408, 500, 503, etc.)

---

## Field Mapping: Record → Display

| JSON Field | Use | Display Location |
|---|---|---|
| `Code` | HTTP status that triggered CB | Lifecycle tracking (not directly displayed) |
| `Count` | Occurrence number | Lifecycle tracking (not directly displayed) |
| `Success` | "False" indicates error | Implicit in OPEN/CLOSED status |
| `Time` | When CB triggered | Lifecycle timestamp |
| `Ver` | Proxy version | Metrics catalog section |
| **Effect** | **Sets `ServerCircuitBreakerOpen`** | **Circuit breaker tile (OPEN/CLOSED)** |

---

## Key Insights for Review/Discrepancies

**What to Look For:**
1. **Code 408** = Proxy timeout (requests taking too long)
2. **Code 500** = Backend server error
3. **Count increasing** = Circuit breaker being triggered repeatedly
4. **No recovery** = If OPEN doesn't go to CLOSED, circuit is stuck

**Discrepancies to Check:**
- S7P-CircuitBreakerError received but tile still shows CLOSED → Check store update logic
- Code 408 but no request timeout showing → Check timeout threshold configuration
- Count keeps increasing → Indicates persistent backend issues
- OPEN without corresponding request failures → Indicates aggressive CB settings

---

## Related Components

- **Event Hub Reader:** [EventHubReader.cs](Components/Shared/EventHub/EventHubReader.cs#L428) - Routes S7P-CircuitBreakerError
- **Monitor Store:** [EventHubMonitorStore.cs](Components/Shared/EventHub/EventHubMonitorStore.cs) - Maintains CB state
- **Monitor Page:** [EventHubMonitorPage.razor](Components/Pages/EventHubMonitorPage.razor) - Renders circuit breaker tile
- **Monitor Models:** [EventHubMonitorModels.cs](Components/Shared/EventHub/EventHubMonitorModels.cs) - RuntimeStatsSnapshot
