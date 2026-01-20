# Azure AI Foundry & OpenAI Integration

SimpleL7Proxy serves as a powerful gateway for **Azure AI Foundry** (formerly Azure OpenAI Studio) workloads. It sits between your client applications and the backend model deployments, adding critical enterprise capabilities like priority queuing, token tracking, and unified governance.

## Configuration Guide

### 1. Integration Patterns

You can configure the proxy to connect to AI Foundry in two primary ways:

#### A. Direct Mode ( Cost Optimized)
Connect directly to the Azure OpenAI endpoint without health probing or intermediate gateways. This is ideal for **lower volume workloads** or scenarios where **Azure API Management (APIM) becomes cost-prohibitive** given the traffic volume. By removing the APIM hop and active probing, you optimize for cost while maintaining core proxy capabilities like priority queuing.
*   **Config**: `mode=direct`
*   **Behavior**: The proxy assumes the backend is always healthy (`SuccessRate=1.0`) and relies on standard retries for failures.

#### B. Standard/APIM Mode (Enterprise Governance)
Connect via **Azure API Management (APIM)** to leverage its robust policy engine. This pattern is essential for **enterprise environments** requiring strict governance, advanced security, and centralized control. By fronting your AI resources with APIM, you can implement sophisticated policies to protect and optimize your endpoints.

**Key APIM Capabilities:**
*   **Smart Rate Limiting**: Enforce limits based on **Token Count** (TPM) rather than just request count (RPM).
*   **Semantic Caching**: Cache common LLM responses to reduce latency and costs.
*   **PII Scrubbing**: Automatically detect and redact sensitive data from prompts and responses.
*   **Advanced Security**: Implement Mutual TLS (mTLS), IP whitelisting, and centralized key management.
*   **Quota Management**: Enforce strict usage budgets per tenant, product, or subscription.

*   **Config**: `mode=apim` (default)
*   **Behavior**: The proxy polls a probe path (e.g., `/status`) and tracks latency for load balancing.

### 2. Backend Host Setup
To integrate with an OpenAI/Foundry endpoint, configure a backend host using the **Advanced Connection String** format.

**Key Requirements:**
*   **`host`**: Your Azure OpenAI Endpoint (e.g., `https://my-resource.openai.azure.com`).
*   **`processor=OpenAI`**: **Crucial**. This tells the proxy to parse the streaming JSON response to extract Token Usage metrics.
*   **`usemi=true`** (Optional but Recommended): Uses Managed Identity to authenticate with AI Foundry, eliminating API Key management.

**Configuration Examples:**

*   **Direct Mode (No Probes):**
    ```bash
    Host1="host=https://my-ai-resource.openai.azure.com;mode=direct;processor=OpenAI;usemi=true"
    ```

*   **Standard Mode (With Probes):**
    ```bash
    Host1="host=https://my-ai-resource.openai.azure.com;processor=OpenAI;usemi=true;probe=/openai/deployments/gpt-4/status"
    ```

### 3. Routing deployments
The proxy transparently forwards URL paths. Clients should target the proxy as if it were the OpenAI endpoint.

**Client Request:**
`POST https://proxy-url/openai/deployments/gpt-4-turbo/chat/completions?api-version=2024-02-15`

**Proxy Forwarding:**
`POST https://my-ai-resource.openai.azure.com/openai/deployments/gpt-4-turbo/chat/completions?api-version=2024-02-15`

### 3. Authentication
*   **Pass-through**: If your client sends an `api-key` header, the proxy forwards it.
*   **Managed Identity**: If `usemi=true` is configured, the proxy automatically acquires a Microsoft Entra ID token for `https://cognitiveservices.azure.com` and attaches it as a `Bearer` token. This allows your clients to be "keyless."

## Advanced Features

### Token Usage Tracking
By enabling `processor=OpenAI`, the proxy inspects the Server-Sent Events (SSE) stream. It captures the `usage` object (often sent in the final event of a stream) and logs it to Application Insights.
*   **Benefit**: You get precise chargeback data (`Prompt_Tokens`, `Completion_Tokens`) even for streaming chat applications.

### Rate Limiting & Prioritization
You can map specific users (via `User Profiles`) to specific backend deployments or Priority lanes.
*   **Scenario**: "Gold" users get routed to `Host1` (PTU/Dedicated capacity), while "Silver" users track to `Host2` (Pay-Go/Standard).

### OpenAI Batch API Support
The proxy detects Batch API requests. Logic in the proxy (`OpenAIProcessor`) can identify batch job submissions and automatically handle them as long-running async operations if configured, ensuring your gateway doesn't hang on massive batch commits.
