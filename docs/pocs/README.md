# Proofs of Concept

Run observable scenarios that demonstrate SimpleL7Proxy behavior.

## TL;DR

- Start with failover to observe backend retry behavior.
- Use priority routing to validate APIM eligibility policy.
- Use chargeback or security scenarios for governance controls.

| Scenario | What it proves |
|----------|----------------|
| [Failover](failover.md) | A failed backend attempt advances to another backend |
| [OpenAI Failover](openai-failover.md) | Azure OpenAI capacity can fail over across deployments |
| [Priority Routing](priority-routing.md) | APIM backend eligibility follows request priority |
| [Chargeback](chargeback.md) | Usage telemetry can be attributed to callers |
| [Secure the Proxy](secure-the-proxy.md) | Proxy ingress rejects unauthorized callers |
| [Secure APIM](secure-apim.md) | APIM validates callers before forwarding |

> [!TIP]
> Use the [LLM Simulator](../how-to/llm-simulator.md) when a scenario does not require a real model endpoint.
