# SimpleL7Proxy: Enterprise AI Gateway for Azure #

SimpleL7Proxy is a high-performance, intelligent Layer 7 router engineered to optimize **Large Language Model (LLM)** workloads. Deployed alongside **Azure API Management** and **AI Foundry**, it provides an advanced orchestration layer for **(LLM)** model providers.

Unlike proprietary gateways, SimpleL7Proxy is a **fully open-source, self-hosted solution,** offering unparalleled customization for data residency, sovereign cloud requirements (GCC High), and bespoke enterprise logic.

## Core Value Propositions

| Challenge | Enterprise-Grade Solution |
|-----------|--------------------------|
| **Workload Contention** | **Tiered Priority Queuing:** Preemptive scheduling ensures mission-critical AI requests bypass background batch processing. |
| **Resource Monopolization** | **Fair-Share Governance:** Granular user/group throttling prevents "noisy neighbor" scenarios and ensures equitable capacity distribution. |
| **System Instability** | **Self-Healing Resiliency:** Integrated Circuit Breaker and automated Retry patterns prevent backend failures from cascading into outages. |
| **Observability Gaps** | **Streaming Telemetry:** Real-time capture of AI token metrics and consumption data, even across high-velocity streaming responses. |
| **Regional Latency** | **Global Traffic Steering:** Intelligent multi-region load balancing with latency-based routing for optimal response times. |
| **Timeout Constraints** | **Stateful Async Orchestration:** Native support for long-running requests (>30 min) via Azure Service Bus notifications and status tracking. |
| **Compliance Barriers** | **Zero-Trust Connectivity:** Hardened with VNET Injection, Managed Identity, and OAuth2, purpose-built for regulated industries and Gov Cloud. |



##The Open Source Advantage##

While commercial alternatives like Portkey.ai or Helicone offer managed services, and LiteLLM provides broad provider support, **SimpleL7Proxy** is uniquely optimized for the Azure ecosystem.
By leveraging a self-hosted architecture on Azure Container Apps, organizations maintain complete ownership of the data plane. This eliminates third-party dependency, simplifies Azure API Management policy integration, and allows for deep extensibility that proprietary "black box" gateways cannot match.

##Supported Architectural Scenarios##

**SimpleL7Proxy** is designed to be the backbone of your AI platform, seamlessly integrating with:

* **[Azure AI Foundry](docs/AI_FOUNDRY_INTEGRATION.md):** Advanced routing and rate-limiting for model endpoints.
* **Azure API Management (APIM):** Enhancing the APIM policy engine with sophisticated queuing and async state management.
* **Sovereign & Hybrid Cloud:** Standardizing AI egress and governance across public and government regions.

## When to Choose SimpleL7Proxy

### Ideal Use Cases
*   **Mixed Workloads**: You need to prevent batch processing (e.g., embeddings, summarization) from blocking interactive users (e.g., chat) using **Preemptive Priority Queuing**.
*   **Long-Running Operations**: Your AI tasks exceed standard HTTP timeouts (30+ minutes) and require **Async/Stateful** execution.
*   **Strict Compliance**: You require a **fully self-hosted** solution that runs entirely within your VNET (e.g., Gov Cloud) with no data egress to third-party gateways.
*   **Cost Management**: You want to maximize efficient use of fixed-capacity (PTU) throughput before spilling over to Pay-As-You-Go.
*   **Deep Observability**: You need to capture **Token Usage** metrics from streaming LLM responses for chargeback or auditing.
*   **Azure Stack integration**: This proxy is deeply integrated with **Azure Managed Identity**, **APIM**, **ACA** and **AI Foundry**. 

### When to Consider Alternatives
*   **Managed Service Preference**: If you prefer a SaaS solution and do not want to manage Azure Container Apps infrastructure, consider managed gateways like Portkey.ai or Helicone.
*   **Basic Routing**: If you only require simple round-robin load balancing without priority queuing or token inspection, standard Azure Application Gateway is less complex to maintain.

## Capabilities:

### Security
- ** Virtual Network Injection:** Secure mission-critical workloads with native **VNET Integration** and identity-based access through [Microsoft Entra ID (Managed Identity)](https://learn.microsoft.com/en-us/entra/identity/managed-identities-azure-resources/overview) across public and sovereign regions.
- **Identity-Driven Edge Security:** Enforce **Zero Trust** principles with integrated **OAuth2 authentication** and customizable **Header Policy Enforcement** to validate or restrict inbound request structures.
- **Dynamic Access Governance:** Centrally manage user and group permissions via **External Configuration Providers**, enabling real-time suspension or restriction of access without code changes.

### Reliability
- **Global Traffic Steering & Automated Failover:** Ensure business continuity with **Multi-Region Traffic Distribution** and high-speed **DNS Propagation** for instant disaster recovery.
- **Resilient Request Handling:** Mitigate transient failures using automated **Retry Policies** and built-in **[Circuit Breaker](docs/CIRCUIT_BREAKER.md)** patterns to protect backend health during outages.
- **Temporal Request Validation:** Maintain system integrity by automatically expiring stale requests via **TTL (Time-to-Live) Management**, preventing the processing of outdated data.

### Performance Efficiency
- **Intelligent Traffic Management:** Optimize response times with **Adaptive Load Balancing**, supporting latency-based, weighted round-robin, and randomized routing modes.
- **Tiered Workload Prioritization:** Guarantee performance for critical tasks through **Configurable Priority Levels** and isolated **Dedicated Worker Threads**.
- **Integrated Async/Sync Processing:** Deliver seamless user experiences with native support for both **Real-Time Synchronous** and **Decoupled Asynchronous** messaging patterns.

### Operational Excellence
- **Advanced AI Observability:** Real-time **[Token Usage Telemetry](docs/OBSERVABILITY.md)** for generative AI workloads, providing precise metric capture even for high-velocity **Streaming Responses**.
- **Automated Resource Safeguards:** Prevent service degradation with **Proactive Throttling** and intelligent **Circuit Breaker rejection** when backend thresholds are exceeded.

### Cost Optimization
- **Fair-Share Resource Governance:** Maximize ROI by preventing resource monopolization through **User-Level Throttling** and **Anti-Starvation** algorithms to ensure equitable allocation.



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

## Local Development Setup

For local development and testing, SimpleL7Proxy includes an interactive setup script (`local-setup.sh`) to configure the proxy environment.

For detailed instructions on local setup, manual configuration, and running mock backends, see [Development and Testing](docs/DEVELOPMENT.md).

## Deployment

SimpleL7Proxy is designed to be deployed to Azure Container Apps (ACA) using the Azure Developer CLI (AZD).

For comprehensive deployment instructions, including standard, high-performance, and VNET-secured scenarios, see [Container Deployment](docs/CONTAINER_DEPLOYMENT.md).

