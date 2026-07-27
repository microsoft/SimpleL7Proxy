# How does APIM determine which backends receive requests?

Priority-aware backend selection, throttling/`429` handling, `LoadBalanceMode` selection, failover, and requeue behavior.

[← Back to FAQ index](README.md)

---

### Where does priority-aware backend routing happen?

The supplied APIM priority policy uses the request priority and determines the eligible backends for each request.

### How does the APIM policy handle a throttled endpoint?

When an endpoint returns `429`, the policy records its retry time and marks it as throttled. Later requests skip that endpoint until the retry time passes, so APIM can use another endpoint instead of immediately repeating an attempt that is expected to throttle.

### What controls SimpleL7Proxy backend selection?

The selector first filters `Host` entries by request path, orders the matching hosts using `LoadBalanceMode` (`latency`, `roundrobin`, or `random`), and then skips unhealthy or open-circuit hosts. The same `LoadBalanceMode` ordering and per-host circuit-breaker gating applies whether the matched hosts are direct or APIM-mode — both are ordered together in one candidate list. It does not use queue priority to determine backend eligibility.

### What happens when a backend attempt fails or returns 429?

The proxy tries the next host in the ordered candidate list. A `429` response with `S7PREQUEUE: true` is collected as requeue-eligible before moving on. Once every host in the list has been tried, the proxy requeues the request — using the shortest eligible delay from any collected `429` responses — instead of failing it outright. This is bounded by the request's overall TTL: if the TTL expires first, iteration stops with `412` rather than continuing to retry.

### What happens when the preferred backend fails?

#### Proxy
If the preferred backend is unavailable, throttled, or unhealthy, the proxy automatically retries the request against the next available backend in the configured list.

#### APIM
When using APIM backend pools, APIM evaluates backend priority groups in their configured order and selects an available endpoint within the highest-priority group. If no healthy endpoints remain in that group, APIM fails over to the next priority group.

This approach enables organizations to reserve specific endpoints or capacity pools for different request priorities, ensuring that critical workloads continue to receive service during capacity constraints or backend failures.

### What happens when every endpoint in an APIM region is throttled?

When APIM has exhausted all eligible endpoints in a region, it can return an HTTP 429 (Too Many Requests) response along with the S7PREQUEUE: true|false header and a recommended retry interval.

SimpleL7Proxy interprets this response as a regional capacity constraint and automatically retries the request against the next configured APIM host. This allows traffic to fail over to another region with available capacity, improving resiliency and reducing the impact of localized throttling.

### What happens when every APIM region is throttled?

After all configured APIM hosts return a requeue response, the proxy selects the shortest eligible retry delay, places the request back in its priority queue, and tries again after that delay. The request remains subject to its overall TTL while it waits and retries.

See [Load Balancing](../reference/load-balancing.md) for backend selection and retry mechanics.
