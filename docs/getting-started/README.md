# Getting Started

Run SimpleL7Proxy, connect one backend, and verify a proxied request.

## TL;DR

- [Run locally](local.md) or [deploy to Azure Container Apps](container-apps.md).
- [Connect a backend](connect-backend.md), using the simulator if you do not have an endpoint.
- [Verify the deployment](verify.md) with health checks and response headers.

## Choose a Runtime

| Goal | Guide |
|------|-------|
| Run from source or Docker | [Run locally](local.md) |
| Deploy to Azure Container Apps | [Run in Azure Container Apps](container-apps.md) |
| Connect Azure OpenAI, APIM, or the simulator | [Connect a backend](connect-backend.md) |
| Confirm that traffic passes through the proxy | [Verify the proxy](verify.md) |

> [!TIP]
> For a first run without an Azure OpenAI endpoint, use the [LLM simulator](../how-to/llm-simulator.md).
