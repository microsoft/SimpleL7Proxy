# How do user profiles determine when requests run?

Where a request's priority comes from, when it takes effect, model overrides, and defaults.

[← Back to FAQ index](README.md)

---

### Where does a user's priority come from?

A request's priority can come from an incoming request header or from the user's profile. When user profiles are used, the proxy caches them from CosmosDB into memory, refreshing the cache every hour, and matches each incoming request to a profile to assign its priority.

See [Priority Levels](priority-levels.md#what-does-a-requests-priority-actually-control) for how priority values are structured and what they control.

### When does the profile priority take effect?

The proxy resolves the profile before admitting the request to the queue. It assigns the mapped priority when the request is enqueued, so the value affects dispatch order as soon as the request begins waiting for a worker.

### Can a profile change the requested model?

Yes. A user profile can specify a model override. The proxy rewrites the original request to use that model before forwarding it, so model selection can be controlled per user without requiring the caller to change the request.

### What happens when no profile priority is available?

The proxy uses `DefaultPriority` when it cannot override it.

See [User Profiles](../USER_PROFILES.md) for profile structure and loading.
