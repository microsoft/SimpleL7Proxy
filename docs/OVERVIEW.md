# SimpleL7Proxy

SimpleL7Proxy is an open-source Layer 7 proxy for Azure AI workloads. It sits between your clients and backend model endpoints, adding priority queuing, circuit breaking, load balancing, and request governance that a standard HTTP proxy or load balancer doesn't provide.

It is self-hosted — you run it in your own environment, typically as an Azure Container App alongside Azure API Management and AI Foundry.

> Need help diagnosing issues quickly? Start at [TroubleshootTOC.md](TroubleshootTOC.md).

## What problems does it solve?

| Problem | How the proxy helps |
|---------|---------------------|
| Interactive requests blocked by batch jobs | Priority queuing: give interactive requests a higher priority so they don't wait behind batch work. |
| One user consuming all capacity | Per-user throttling: limits how much of the queue any single user can occupy at once. |
| Backend failures disrupting clients | Circuit breaker and retry: failing backends are skipped automatically and retried later when they recover. |
| Token usage invisible in streaming responses | Token telemetry: captures token counts from streaming responses for cost tracking and chargebacks. |
| Uneven backend response times | Load balancing: spreads requests across backends using round-robin, latency-based, or random ordering. |
| Long-running AI tasks timing out | Async support: requests that exceed normal HTTP timeouts run asynchronously, with status updates via Service Bus. |
| Running inside regulated or sovereign environments | VNet and Managed Identity: runs entirely inside your own VNet with no external data dependencies. |



## Self-hosted and open source

Since you run it yourself, you own the data plane — nothing leaves your environment. It integrates closely with Azure API Management and can be extended or forked to fit your needs.

If you prefer a managed service and don't want to operate your own infrastructure, alternatives like Portkey.ai or Helicone may be a better fit.

## Supported Architectural Scenarios

SimpleL7Proxy works well alongside:

* **[Azure AI Foundry](AI_FOUNDRY_INTEGRATION.md):** routing and rate-limiting for model endpoints.
* **[Azure API Management (APIM)](https://learn.microsoft.com/en-us/azure/api-management/api-management-key-concepts):** adds queuing and async capabilities on top of APIM's policy engine.
* **[Custom APIM Policy](../APIM-Policy/readme.md):** a reference policy for connecting the proxy to APIM backends.
* **Sovereign & Hybrid Cloud:** works in sovereign and government cloud regions.
* **Other clouds:** the Docker image runs on any container platform; it has been used on AWS and GCP as well.

## When to Choose SimpleL7Proxy

### Ideal Use Cases
* **Mixed workloads:** you want batch jobs (embeddings, summarization) to yield to interactive requests (chat).
* **Long-running requests:** your AI tasks can take 30 minutes or more and can't complete within a normal HTTP timeout.
* **Strict compliance:** you need everything to run inside your own VNet with no external data dependencies.
* **PTU cost control:** you want to maximize dedicated-capacity usage before falling back to Pay-As-You-Go.
* **Token tracking:** you need accurate token counts from streaming responses for billing or auditing.
* **Azure-native integration:** you're already using Managed Identity, APIM, Container Apps, and AI Foundry.

### When to consider alternatives
* **You prefer a managed service:** if you don't want to operate your own infrastructure, consider Portkey.ai or Helicone.
* **Simple routing is enough:** if you only need basic round-robin load balancing without priority queuing or token inspection, [Azure Application Gateway](https://learn.microsoft.com/en-us/azure/application-gateway/overview) is simpler to run.
* **Azure API Management:** APIM has built-in public/private gateways and streaming token counting, but doesn't support priority queuing, user profiles, or stream inspection. When using APIM as a backend for SimpleL7Proxy, see the [recommended high-throughput policy](../APIM-Policy/readme.md).

## Capabilities

### Security
- **VNet support:** runs inside a VNet using [Managed Identity](https://learn.microsoft.com/en-us/entra/identity/managed-identities-azure-resources/overview) for authentication, including sovereign regions.
- **OAuth2 and header validation:** validates or restricts incoming requests based on headers before forwarding them.
- **Live access control:** suspend or restrict users via configuration without redeploying.

### Reliability
- **Multi-region failover:** distribute traffic across regions and fail over when backends become unreachable.
- **Retry and [circuit breaker](CIRCUIT_BREAKER.md):** retries failed requests on alternate backends; stops sending to backends that are consistently failing.
- **[Sidecar health probes](HEALTH_CHECKING.md):** an optional sidecar serves health endpoints so high load won't cause false Kubernetes restarts.
- **TTL expiry:** requests that wait too long in the queue are rejected rather than processed stale.

### Performance
- **Flexible load balancing:** choose between round-robin, latency-based, or random host selection.
- **Priority queuing:** high-priority requests get dedicated workers and run before lower-priority ones.
- **Sync and async:** handles standard HTTP requests and long-running async tasks via Service Bus.

### Observability
- **[Token telemetry](OBSERVABILITY.md):** captures token usage from streaming AI responses and sends it to Application Insights or Event Hubs.
- **Throttling and circuit breaking:** limits queue depth and cuts off failing backends before they affect other users.

### Cost
- **Per-user throttling:** prevents any single user from consuming all available capacity.

## APIM Policy Scenarios

* Route high-priority requests to designated backend services.
* Sustain high throughput, exceeding 23M TPM.
* Control concurrency for each backend independently.
* Enable streaming with real-time token capture.
* Enforce backend timeouts to ensure responsiveness.
* Maximize PTU usage, while choosing which priorities use PayGo.

## Architecture

![Architecture Diagram](arch.png)
