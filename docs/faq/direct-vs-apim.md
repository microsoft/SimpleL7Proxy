# When should traffic go directly to a backend or through APIM?

What each mode is, when to use direct mode, and when to route through APIM.

[← Back to FAQ index](README.md)

---

### What is a direct backend?

A direct backend uses `mode=direct`. The proxy does not send active health probes to it and always includes it in the active host set. Real request failures are still recorded by the circuit breaker.

```bash
Host_<name>="host=https://model.example.com;mode=direct;path=/model"
```

### When should I use direct mode?

Use it when probing would be unsafe or undesirable—for example, when a serverless target scales to zero or has no suitable probe endpoint. Because direct mode has no probe-derived latency, it sorts first when `LoadBalanceMode=latency`.

### When should I route through APIM?

Use APIM in the backend path when requests need gateway policies, transformations, subscriptions, caller authentication, or priority-aware selection across the services behind APIM. This adds APIM as an operational dependency, so use it for capabilities the direct path does not provide.

### What is an APIM backend?

An APIM backend points a `Host_<name>` entry at Azure API Management. `mode=apim` is standard non-direct behavior: the proxy sends the configured probe on every `PollInterval`, recording both a rolling success rate and latency on each successful probe. It can remove APIM from the active set when health falls below the required success rate, and uses the recorded latency to order hosts when `LoadBalanceMode=latency`.

```bash
Host_<name>="host=https://gateway.azure-api.net;mode=apim;path=/shared;probe=/health"
```

### Why put APIM behind the proxy?

APIM can supply API gateway capabilities such as caller authentication, subscriptions, transformations, and priority-aware backend policies. The proxy adds its own queue, worker controls, health tracking, circuit breaking, and telemetry around that gateway path.

See [Backend Host Configuration](../BACKEND_HOSTS.md) for all host options.
