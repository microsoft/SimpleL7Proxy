# How does the proxy keep queueing fair across users?

Per-priority worker pools and the `UserPriorityThreshold` mechanism that stops one user monopolizing a level.

[← Back to FAQ index](README.md)

---

### How does the proxy keep high-priority traffic from starving lower-priority requests?

Each priority level has its own dedicated pool of workers, so higher-priority traffic can't consume the capacity reserved for lower-priority requests.

### How does the proxy keep one user from monopolizing a priority level?

Within a priority level, the proxy tracks each user's share of active requests. A user who stays under `UserPriorityThreshold` (default `0.1`, i.e. 10%) gets a fairness boost ahead of other users at that level; once their share crosses the threshold, the boost is withheld until it drops back down.

See [Advanced Configuration](../reference/advanced-configuration.md#userprioritythreshold) for how to tune that threshold, with a worked example.
