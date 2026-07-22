# Troubleshooting

> **Start here.** Find your symptom in the table below and follow the link to the dedicated guide.

## Quick Diagnosis

| Symptom | Guide |
|---------|-------|
| **App Configuration** settings not loading or not refreshing | [App Configuration not loading](app-configuration.md) |
| **Async expected** but request returns sync (no `202 Accepted`) | [Async expected but 202 never issued](async-202-never-issued.md) |
| **Async requests** never completing / blobs empty or missing | [Async requests not completing](async-requests.md) |
| **Backend hosts** not being picked up at startup | [Backend hosts not healthy](backend-hosts.md) |
| **Event Hub** — no messages arriving | [Event Hub messages not appearing](event-hub.md) |
| **Health probes failing** / pod keeps restarting | [Health probe failures](health-probes.md) |
| A backend host is **stuck as unhealthy** / circuit breaker won't close | [Circuit breaker stuck open](circuit-breaker.md) |
| Clients receiving **400 Bad Request** (`InvalidTTL`) | [Getting 400 / invalid TTL format](requests-400-invalid-ttl.md) |
| Clients receiving **412 Precondition Failed** | [Getting 412 / TTL expired](requests-412.md) |
| Clients receiving **429 Too Many Requests** | [Getting 429 responses](requests-429.md) |
| Clients receiving **503 Service Unavailable** or **502** | [Getting 503 / all backends failing](requests-503.md) |
