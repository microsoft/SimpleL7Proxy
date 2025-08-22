# SimpleL7Proxy

Enterprise-grade HTTP/HTTPS proxy offers high availability, smart load balancing with failover, priority traffic control, advanced security, circuit breaking, async support, deep observability, and gov. cloud compliance. 

This codebase implements the following scenarios when used with APIM and OpenAI.

## Proxy Scenarios:
* 100% internal VNET traffic with Managed Identity and OAuth2 authentication.
* Prioritize urgent requests to ensure critical workloads are served first.
* Prevent resource monopolization by limiting greedy users.
* Distribute traffic across multiple regions for resilience and performance.
* Automatically retry and fail over during outages or backend errors.
* Seamlessly support both synchronous and asynchronous client patterns.
* Capture AI token metrics even if using streaming.
* Enforce strict header validation before processing requests.
* Restrict or suspend user or group access based on policy.
* Throttle or reject requests when circuit breaker thresholds are reached.

## APIM Policy Scenarios:
* Route high-priority requests to designated backend services.
* Sustain high throughput, exceeding 23M TPM.
* Control concurrency for each backend independently.
* Enable streaming with real-time token capture.
* Enforce backend timeouts to ensure responsiveness.
* Maximize PTU  usage, while choosing which priorities use PayGo.

# Arch Diagram
![Architecture Diagram](docs/arch.png)

Documentation is located in the [docs/README.md](docs/README.md) file.