# Choose a Local Setup

Run SimpleL7Proxy from source or Docker and connect it to the backend that matches your use case.

> **TL;DR**
> - Choose the local simulator for the fastest no-Azure path.
> - Choose a real LLM endpoint for direct model access.
> - Choose APIM to test policy routing and governance.

```text
ChatTester → local proxy → simulator, real LLM, or APIM → backend
```

## Choose Your Backend

<details>
<summary><strong>Local proxy → local LLM Simulator</strong> — fastest path, no Azure resources</summary>

**Use this path to prove the request flow without Azure or real model access.**

1. Run the simulator with [LLM Simulator](../../test/LLMSimulator/Readme.md#run-it-locally-60-seconds).
2. Run the proxy [from source](02-get-it-running.md#how-do-i-run-the-proxy-from-source-locally-exact-commands) or [in Docker](02-get-it-running.md#how-do-i-run-the-proxy-as-a-docker-container-locally).
3. Set `Host1` to the simulator URL with `mode=direct`; no backend authentication is required.
4. Continue to [Run ChatTester](#run-chattester-and-send-the-first-query).

> [!TIP]
> If the proxy runs in Docker, use `host.docker.internal` instead of `localhost` for the simulator host.

</details>

<details>
<summary><strong>Local proxy → real LLM endpoint</strong> — direct connection</summary>

**Use this path when APIM is not required and the proxy can reach the model endpoint directly.**

1. Run the proxy [from source](02-get-it-running.md#how-do-i-run-the-proxy-from-source-locally-exact-commands) or [in Docker](02-get-it-running.md#how-do-i-run-the-proxy-as-a-docker-container-locally).
2. Configure the endpoint with [Managed Identity or an API key](../BACKEND_HOSTS.md#per-host-auth-behavior).
3. Use `mode=direct` when the endpoint has no suitable health probe.
4. Continue to [Run ChatTester](#run-chattester-and-send-the-first-query).

> [!WARNING]
> Managed Identity works locally only when the local credential chain has access to the LLM resource.

</details>

<details>
<summary><strong>Local proxy → APIM → LLM or Simulator</strong> — policy routing and governance</summary>

**Use this path to test APIM priority, retry, throttling, affinity, or mixed-backend behavior.**

1. Deploy the simulator to [Azure Functions](../../test/LLMSimulator/Readme.md#run-it-on-azure-functions-recommended), or prepare real LLM endpoints.
2. Upload the [APIM policy](../../APIM-Policy/readme.md) and edit every backend URL, path, priority, and `auth` value.
3. Configure `Host1` with the APIM URL and its required subscription-key or OAuth authentication.
4. Run the proxy and continue to [Run ChatTester](#run-chattester-and-send-the-first-query).

> [!NOTE]
> APIM-to-backend authentication is separate from proxy-to-APIM authentication. Configure both hops.

</details>

## Run ChatTester and Send the First Query

**The setup is complete only after one request passes through the proxy.**

1. Verify `/health` and `/readiness`.
2. Start [ChatTester](../../test/chat_tester/readme.md#requirements-and-startup).
3. Set `ServerBaseUrl` to the local proxy URL.
4. Send one chat request and confirm `200 OK`, response content, `BackendHost`, and `x-Request-Worker`.

> [!TIP]
> If health succeeds but the query fails, test each hop directly.

[← Choose a different run location](02-get-it-running.md#choose-your-setup)
