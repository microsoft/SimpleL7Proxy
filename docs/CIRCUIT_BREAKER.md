# Circuit Breaker & Resilience

SimpleL7Proxy implements a robust, self-healing **Circuit Breaker** pattern to prevent cascading failures when backend services become unstable. Instead of continuously hammering a failing service (which makes outages worse), the proxy "breaks the circuit" and stops sending traffic to that specific host for a period of time.

## Support Logic

The circuit breaker operates on a **Sliding Time Window** principle.

1.  **Tracking**: Every request to a backend is monitored.
2.  **Failure Detection**: If a request returns a status code **not** in the `AcceptableStatusCodes` list (e.g., 500, 502, 503) or throws a network exception, it is recorded as a failure.
3.  **Threshold Check**: The proxy counts the number of failures that occurred within the last `CBTimeslice` seconds.
4.  **Tripping**: If the count of recent failures exceeds `CBErrorThreshold`, the circuit **Opens** (breaks).
5.  **Blocking**: While Open, the host is marked as "Unhealthy." The Load Balancer will skip this host and route traffic to other healthy backends.
6.  **Recovery (Auto-Healing)**: As time passes, failure timestamps fall out of the `CBTimeslice` window. Once the count drops below the threshold, the circuit **Closes** automatically, and traffic resumes.

## Configuration

Control the sensitivity of the circuit breaker using these environment variables:

| Variable | Default | Description |
| :--- | :--- | :--- |
| **`CBErrorThreshold`** | `50` | The number of errors required to trip the circuit. Lower values make it more sensitive. |
| **`CBTimeslice`** | `60` | The sliding window duration (in seconds). Errors older than this are ignored. |
| **`AcceptableStatusCodes`** | `200, 202, 401...` | List of HTTP codes considered "Success". Anything else counts towards the error threshold. |

### Example Scenarios

*   **Fast Failover**: Set `CBErrorThreshold=5` and `CBTimeslice=10`. The proxy will stop using a host almost immediately after a burst of 5 errors.
*   **Tolerant**: Set `CBErrorThreshold=100`. Useful for "flaky" non-critical backends where you strictly prefer retries over disabling the host.

## Global Safety Net
The proxy monitors the state of **all** circuit breakers. If **all** configured backends are tripped (meaning the entire backend tier is down), the proxy may enter a fail-safe mode or return a `503 Service Unavailable` to the client immediately, protecting the proxy itself from resource exhaustion.
