# Event Data Analysis - Unique Events with Information Gathered

## Event Type 1: S7P-ProxyRequestEnqueued
**Information Gathered:**
- **Message**: "Enqueued request" → Request accepted and queued for processing
- **GUID**: Unique identifier for tracking this request across all related events
- **Path**: /struts2-showcase/struts/utils.js?session=82 → Target resource path with query parameters
- **RequestHost**: localhost:8000 → Client's request host and port
- **Method**: GET → HTTP method used
- **Priority**: 2 → Request priority level for processing
- **S7P-Priority**: 2 → Proxy-specific priority value
- **S7P-Priority2**: 0 → Secondary priority flag
- **QueueLength**: 1 → Number of requests in queue
- **DefaultTimeout**: 1200000 → Timeout value in milliseconds (20 minutes)
- **ExpiresAt**: 2026-06-17T20:22:05 → When request expires and should fail
- **ActiveHosts**: 1 → Number of available backend hosts
- **UserID**: defaultUser → User making the request
- **RequestUserAgent**: N/A → No user agent provided
- **S7P-ID**: S7P-LAPTOP-KOKKFB9M-200 → Worker/machine identifier
- **MID**: S7P-LAPTOP-KOKKFB9M-200 → Message/worker ID
- **Ver**: 2.2.13 → Proxy version
- **Replica**: (empty) → Container replica name (if applicable)
- **ContainerApp**: ContainerAppName → Container application identifier
- **Type**: S7P-ProxyRequestEnqueued → Event classification
- **Status**: 0 → No response yet (enqueued)

---

## Event Type 2: S7P-BackendRequest
**Information Gathered:**
- **Type**: S7P-BackendRequest → Proxy attempting to forward to backend
- **GUID**: Same as enqueued event → Correlates with original request
- **Path**: /struts2-showcase/struts/utils.js?session=82 → Target path
- **RequestHost**: localhost:8000 → Client host
- **Method**: GET → HTTP method
- **Host-URL**: https://localhost → Backend URL being called
- **Backend-Host**: https://localhost → Configured backend host
- **Request-Date**: 2026-06-17T20:17:05.1683493Z → When request sent to backend
- **Request-Queue-Duration**: 0.1067 → Time spent in queue (seconds)
- **Request-Process-Duration**: 0.0135 → Processing time before sending to backend
- **Attempt**: 1 → Attempt number (1st try)
- **Lifetime-Attempt**: 1 → Total lifetime attempts
- **Status**: 404 → HTTP status code from backend
- **x-ms-request-id**: 356d4f92-3f6f-47bd-8036-76af5f5b13e5 → Azure request tracking ID
- **x-ms-correlation-id**: 356d4f92-3f6f-47bd-8036-76af5f5b13e5 → Request correlation ID
- **Request-Context**: appId=cid-v1:d5a7cc01-2aaa-4e64-8e84-92457137e12c → Application context
- **MID**: S7P-LAPTOP-KOKKFB9M-200-1 → Message ID with attempt suffix
- **S7P-ID**: S7P-LAPTOP-KOKKFB9M-200 → Worker ID
- **Priority**: 2 → Request priority
- **S7P-Priority**: 2 → Proxy priority
- **QueueLength**: 1 → Queue state
- **ActiveHosts**: 1 → Active backends available
- **UserID**: defaultUser → User identity
- **RequestContentLength**: N/A → No request body
- **ExpiresAt**: 2026-06-17T20:22:05.1681890Z → Request expiration time
- **EnqueueTime**: 2026-06-17T20:17:05.1681890Z → When request was enqueued
- **DefaultTimeout**: 1200000 → Default timeout setting
- **RequestType**: Sync → Synchronous request processing
- **ContainerApp**: ContainerAppName → Application container
- **Ver**: 2.2.13 → Version
- **Replica**: (empty) → Replica identifier

---

## Event Type 3: S7P-ProxyRequest (Response)
**Information Gathered:**
- **Type**: S7P-ProxyRequest → Final proxy response event
- **Message**: "No active hosts were able to handle the request" → Failure reason/status message
- **GUID**: Same as request events → Request correlation
- **Path**: /struts2-showcase/struts/utils.js?session=82 → Original requested path
- **Url**: https://localhost/struts2-showcase/struts/utils.js?session=82 → Complete URL attempted
- **Status**: 404 / NotFound → Final response status (HTTP code + description)
- **Method**: GET → HTTP method used
- **RequestHost**: localhost:8000 → Client that made request
- **Backend-Host**: "No Active Hosts Available" → Backend availability status
- **Attempt-1-Backend-Host**: https://localhost → Backend host for attempt 1
- **Attempt-1-Status**: 404 → HTTP status from backend attempt 1
- **Attempt-1-Request-Date**: 2026-06-17T20:17:05.1683493Z → When attempt 1 was made
- **Attempt-1-Duration**: 16.4221 → How long attempt 1 took (ms)
- **Attempt-1-State**: "Backend proxy status code: 404" → State description for attempt
- **Attempt-1-Reason**: NotFound → Failure reason for attempt
- **Attempt-1-Host-URL**: https://localhost → Host URL for attempt
- **Attempts**: 1 → Total number of attempts made
- **Request-Process-Duration**: 0.0135 → Total processing time
- **Request-Queue-Duration**: 0.1067 → Total queue wait time
- **Total-Latency**: 16.821 → Total latency from request to response
- **Response-Latency**: 16.706 → Response time from backend
- **Average-Backend-Probe-Latency**: 16.813 ms → Health probe latency
- **Content-Type**: application/json; charset=utf-8 → Response content type
- **Content-Length**: 369 → Response body size in bytes
- **QueueLength**: 1 → Queue state at response
- **Priority**: 2 → Request priority
- **S7P-Priority**: 2 → Proxy priority
- **S7P-Priority2**: 0 → Secondary priority
- **ActiveHosts**: 1 → Active hosts available
- **UserID**: defaultUser → User identity
- **RequestUserAgent**: N/A → No user agent
- **RequestType**: Sync → Processing type
- **RequestContentLength**: N/A → No request body
- **DefaultTimeout**: 1200000 → Default timeout
- **ExpiresAt**: 2026-06-17T20:22:05.1681890Z → Request expiration
- **EnqueueTime**: 2026-06-17T20:17:05.1681890Z → Enqueue timestamp
- **S7P-ID**: S7P-LAPTOP-KOKKFB9M-200 → Worker ID
- **MID**: S7P-LAPTOP-KOKKFB9M-200 → Message ID
- **Ver**: 2.2.13 → Version
- **Replica**: (empty) → Replica name
- **ContainerApp**: ContainerAppName → Container app name

---

## Key Patterns for Finding Discrepancies

### Cross-Event Consistency Checks:
1. **GUID Matching**: Each logical request should have same GUID across:
   - S7P-ProxyRequestEnqueued
   - S7P-BackendRequest (for each attempt)
   - S7P-ProxyRequest (final response)

2. **Timestamp Sequence** (ignoring milliseconds):
   - EnqueueTime ≤ Request-Date (BackendRequest) ≤ Response Date
   - Request-Queue-Duration should equal time between EnqueueTime and Request-Date

3. **Status Code Consistency**:
   - All 404 responses should show "No Active Hosts Available" or specific backend error
   - Attempt-1-Status should match Status in ProxyRequest

4. **Duration Calculations**:
   - Total-Latency ≈ Request-Queue-Duration + Request-Process-Duration + Attempt-1-Duration
   - Attempt-1-Duration should be close to Response-Latency

5. **Configuration Consistency**:
   - Priority, S7P-Priority, UserID should remain consistent for same request
   - DefaultTimeout should be same across all events for same request

6. **ActiveHosts Counter**:
   - Should be consistent across related events
   - If "No Active Hosts Available" but ActiveHosts=1, that's a discrepancy

7. **Field Presence**:
   - Enqueued events should NOT have Attempt-1-* fields
   - BackendRequest should have Request-Date but not Total-Latency
   - ProxyRequest should have all latency and attempt information

