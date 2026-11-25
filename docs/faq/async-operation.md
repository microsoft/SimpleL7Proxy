# When should clients stop waiting synchronously?

What async solves, how a request enters async mode, the trigger timeout/`202` flow, and when to stay synchronous.

[← Back to FAQ index](README.md)

---

### What problem does async solve?

Async separates backend processing time from the caller's HTTP connection lifetime. It is useful for long model runs, background jobs, or any request likely to exceed a client, gateway, or network timeout.

### Does async make backend processing faster?

No. Async changes how the client waits for and retrieves the result; it does not reduce backend execution time. The benefit is releasing the HTTP connection while work continues.

### How does a request enter async mode?

Async mode must be enabled with an available blob writer, the selected user profile must contain valid enabled async settings with a Blob container and Service Bus topic, and the request header must parse as `true`. Fast requests remain synchronous; response timing and blob queue pressure also affect whether the client receives a 202.

### What happens after the async trigger timeout?

When an eligible opted-in request exceeds `AsyncTriggerTimeout` and queue pressure allows the upgrade, the proxy may return `202 Accepted`. Processing continues, response data is written to Azure Blob Storage, and lifecycle status is published through Azure Service Bus.

### When should I keep requests synchronous?

Keep them synchronous when they reliably finish inside the caller's wait budget. Async adds Storage and Service Bus dependencies, RBAC configuration, result retention, and client-side status handling.

See [Async Operation Configuration](../concepts/async-processing.md) for the complete setup.
