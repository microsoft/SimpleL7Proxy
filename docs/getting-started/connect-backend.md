# Connect a Backend

Choose one backend type and set `Host1` before testing the proxy.

## TL;DR

- Use `mode=direct` for an endpoint without a health probe.
- Use `probe=/health` when the backend exposes a health endpoint.
- Use the included simulator for a reproducible first run.

| Backend | Required connection-string keys | Authentication |
|---------|---------------------------------|----------------|
| Azure OpenAI or AI Foundry | `host`, `mode=direct`, `path` | API key or managed identity |
| Azure API Management | `host`, `probe` or `mode=direct` | APIM subscription or identity policy |
| LLM simulator | `host`, `probe` | None by default |

## Connect an LLM Endpoint

**Use managed identity where available; otherwise pass the backend API key through the host connection string.**

```bash
export Host1="host=https://example.openai.azure.com;mode=direct;path=/;\
processor=MultiLineAllUsage;usemi=true;\
audience=https://cognitiveservices.azure.com/"
```

> [!WARNING]
> Never commit an API key. Supply secrets through your deployment environment or secret store.

## Connect Azure API Management

**Use the APIM gateway URL as the backend host and configure a valid probe or direct mode.**

```bash
export Host1="host=https://example.azure-api.net;mode=direct;path=/"
export Port=8000
dotnet run --project src/SimpleL7Proxy
```

> [!TIP]
> APIM priority-aware routing requires the matching APIM policy. See the [APIM policy documentation](../../APIM-Policy/readme.md).

## Connect the LLM Simulator

**Use the simulator when you need observable success, latency, or throttling without a real model endpoint.**

```bash
export Host1="host=http://localhost:9000;probe=/health"
export Port=8000
dotnet run --project src/SimpleL7Proxy
```

Follow [Run the LLM Simulator](../how-to/llm-simulator.md) before starting the proxy.

## Full Connection-String Reference

See [Backend Hosts](../reference/backend-hosts.md) for every key, authentication mode, probe behavior, and routing option.
