# What does a request's priority level control?

How priority is structured (`PriorityKeys`/`PriorityValues`), what it controls in the proxy vs. APIM, and its effect on backends and retries.

[← Back to FAQ index](README.md)

---

### What is a priority level made of?

Each priority level has two parts: a human-friendly string sent in a header (`PriorityKeys`) and its mapped numeric value used for ordering (`PriorityValues`).

**Example:** With `PriorityKeys=high,medium,low` and `PriorityValues=1,2,3`, the strings map to values as follows:

| Header string (`PriorityKeys`) | Numeric value (`PriorityValues`) |
|---|---|
| `high` | `1` |
| `medium` | `2` |
| `low` | `3` |

A profile containing `"S7PPriorityKey": "high"` therefore receives priority `1`.

### What does a request's priority actually control?

In the **proxy**, priority changes the order in which a queued request is selected by a worker. In **APIM**, it selects the order and priority of eligible endpoints.

### Does priority affect which backends or capacity a request can use?

Yes, but not through SimpleL7Proxy's own backend selection — that filters by request path and `LoadBalanceMode`, not priority. Within APIM, though, each backend declares which priorities it accepts, so a capacity pool can be reserved for higher-priority traffic while lower-priority requests are routed elsewhere.

### Do different priorities get different retry behavior?

Retry count and whether an exhausted request may be requeued are both configured per priority level in the APIM policy, so retry aggressiveness can be tuned independently for each priority.

### What happens when no backend accepts a given priority?

APIM returns `503 Service Unavailable`. Changing retry count cannot help because the candidate set is empty.

See [Priority Levels POC](../POC-Priority-configuration.md) for a runnable example.
