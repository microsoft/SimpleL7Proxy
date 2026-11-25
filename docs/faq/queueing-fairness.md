# How does the proxy keep queueing fair across users?

Priority queue ordering, worker priority preferences, and the `UserPriorityThreshold` secondary queue preference.

[← Back to FAQ index](README.md)

---

### How does the proxy keep high-priority traffic from starving lower-priority requests?

`PriorityWorkers` assigns preferred priorities when workers start. A worker can process other priorities when its preference is unavailable; these are preferences, not reserved pools.

### How does the proxy adjust queue preference based on each user's active share?

The proxy tracks each user's share of active requests. Users below `UserPriorityThreshold` (default `0.1`, or 10%) receive a secondary queue-priority preference; crossing the threshold removes that preference until the tracked share falls below it. This does not change primary priority or cap or reject requests.

See [Advanced Configuration](../reference/advanced-configuration.md#userprioritythreshold) for how to tune that threshold, with a worked example.
