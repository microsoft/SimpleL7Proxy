# Understanding the SimpleL7Proxy — FAQ

Detailed questions and answers for each [Quick Topic](../README.md#quick-topics) in the proxy discovery path. Each topic below has its own page.

---

## FAQ Topics

<table>
<tr>
<td width="50%" valign="top">

### [How do user profiles determine when requests run?](user-profiles.md)
Where a request's priority comes from, when it takes effect, model overrides, and defaults.

</td>
<td width="50%" valign="top">

### [What does a request's priority level control?](priority-levels.md)
How priority is structured (`PriorityKeys`/`PriorityValues`), what it controls in the proxy vs. APIM, and its effect on backends and retries.

</td>
</tr>
<tr>
<td width="50%" valign="top">

### [How does the proxy keep queueing fair across users?](queueing-fairness.md)
Per-priority worker pools and the `UserPriorityThreshold` mechanism that stops one user monopolizing a level.

</td>
<td width="50%" valign="top">

### [How does APIM determine which backends receive requests?](apim-backend-routing.md)
Priority-aware backend selection, throttling/`429` handling, `LoadBalanceMode` selection, failover, and requeue behavior.

</td>
</tr>
<tr>
<td width="50%" valign="top">

### [When should traffic go directly to a backend or through APIM?](direct-vs-apim.md)
What each mode is, when to use direct mode, and when to route through APIM.

</td>
<td width="50%" valign="top">

### [How does the proxy stay resilient and autoscale with ACA?](resilience-autoscaling.md)
Backpressure, circuit breaking, KEDA scale triggers, health probes, replica lifecycle, and draining.

</td>
</tr>
<tr>
<td width="50%" valign="top">

### [When should clients stop waiting synchronously?](async-operation.md)
What async solves, how a request enters async mode, the trigger timeout/`202` flow, and when to stay synchronous.

</td>
<td width="50%" valign="top">

### [How does the proxy handle observability and telemetry?](observability-telemetry.md)
Telemetry channels (App Insights, `ProxyEvent`/`EVENT_LOGGERS`), event fan-out, event contents, streaming token counting, and custom sinks.

</td>
</tr>
</table>