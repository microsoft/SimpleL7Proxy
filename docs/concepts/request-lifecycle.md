# Request Lifecycle

Follow a request from admission through backend selection, retries, and the final response.

## TL;DR

- Validation and TTL checks happen before queue admission.
- A worker selects an eligible backend and retries within the request budget.
- The final response includes headers that expose queueing, processing, and backend selection.

## Lifecycle

**The request TTL is the end-to-end budget across queue time and every backend attempt.**

```text
client → validate → priority queue → worker → select backend
       → call backend → retry/requeue if eligible → respond
```

1. The listener receives and validates the request.
2. User profile and request headers determine priority and overrides.
3. The request enters the priority queue.
4. A worker chooses backends by path, health, load-balancing order, and circuit state.
5. The worker retries or requeues eligible failures while TTL remains.
6. The proxy returns the final response with diagnostic headers and telemetry.

> [!NOTE]
> Exact settings and response fields are defined in [Configuration](../reference/configuration.md) and [Headers and Status Codes](../reference/headers-and-status-codes.md).

> [!WARNING]
> If the TTL expires in the queue or during retries, the proxy returns its TTL-expired response instead of starting another attempt.
