# Choose a Container Apps Setup

Deploy SimpleL7Proxy to Azure Container Apps and connect it to the backend that matches your use case.

> **TL;DR**
> - Choose the Azure Functions simulator for a cloud test path.
> - Choose a real LLM endpoint for direct production access.
> - Choose APIM for governed routing, priority, and failover.

```text
ChatTester → Container Apps proxy → simulator, real LLM, or APIM → backend
```

## Choose Your Backend

<details>
<summary><strong>Container Apps proxy → Azure Functions LLM Simulator</strong> — cloud test path</summary>

**Use this path to validate the deployed proxy without consuming a real model endpoint.**

1. Deploy and verify the [Azure Functions LLM Simulator](../../test/LLMSimulator/Readme.md#run-it-on-azure-functions-recommended).
2. Choose public or private networking and [deploy the proxy](../../deployment/README.md).
3. Set `HOST1` to the function URL with `mode=direct`; the included simulator requires no backend authentication.
4. Continue to [Run ChatTester](#run-chattester-and-send-the-first-query).

> [!TIP]
> Confirm the Container App can reach the function app before diagnosing proxy behavior.

</details>

<details>
<summary><strong>Container Apps proxy → real LLM endpoint</strong> — direct production path</summary>

**Use this path for the shortest production topology without APIM.**

1. Choose public or private networking and [deploy the proxy](../../deployment/README.md).
2. Configure the LLM endpoint with [Managed Identity or an API key](../BACKEND_HOSTS.md#per-host-auth-behavior).
3. Grant the Container App identity access to the model resource when using Managed Identity.
4. Continue to [Run ChatTester](#run-chattester-and-send-the-first-query).

> [!WARNING]
> Do not store API keys in committed deployment parameter files.

</details>

<details>
<summary><strong>Container Apps proxy → APIM → LLM or Simulator</strong> — governed production or failover path</summary>

**Use this path when APIM owns backend selection, priority eligibility, retries, or governance.**

1. Prepare real LLM endpoints, an [Azure Functions simulator](../../test/LLMSimulator/Readme.md#run-it-on-azure-functions-recommended), or both.
2. Upload the [APIM policy](../../APIM-Policy/readme.md) and edit every backend URL, path, priority, and `auth` value.
3. [Deploy the proxy](../../deployment/README.md), point `HOST1` at APIM, and configure subscription-key or OAuth authentication.
4. Continue to [Run ChatTester](#run-chattester-and-send-the-first-query).

> [!NOTE]
> For private deployments, Container Apps, APIM, Functions, DNS, and model private endpoints must share a working network path.

</details>

## Run ChatTester and Send the First Query

**The setup is complete only after one request passes through the proxy.**

1. Verify `/health` and `/readiness`.
2. Start [ChatTester](../../test/chat_tester/readme.md#requirements-and-startup).
3. Set `ServerBaseUrl` to the Container App proxy URL and configure client-to-proxy authentication when required.
4. Send one chat request and confirm `200 OK`, response content, `BackendHost`, and `x-Request-Worker`.

> [!TIP]
> If health succeeds but the query fails, test ChatTester → proxy, proxy → APIM, and APIM → backend separately.

[← Choose a different run location](02-get-it-running.md#choose-your-setup)
