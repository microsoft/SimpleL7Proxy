# Proofs of Concept: See the Proxy in Action

Want to see what SimpleL7Proxy actually does? These are hands-on scenarios you can run and watch. Each one proves a single behavior so you can see it, verify it, and explain it.

## Not sure where to start?

- 🔁 **New here?** Start with **Failover** to watch a failed backend hand off to another.
- 🎯 **Care about routing?** Try **Priority Routing** to see APIM honor request priority.
- 🛡️ **Locking things down?** Jump to the **Chargeback** or **Security** scenarios.

## Pick a scenario

<table>
<tr>
<td width="33%" valign="top">

### [🔁 Failover](failover.md)
A failed backend attempt advances to another backend.
<img src="region-fail.png" alt="Client sends a request through the proxy; traffic fails over from Region 1 to Region 2" width="80%">
</td>
<td width="33%" valign="top">

### [🧠 Capacity Failover](openai-failover.md)
Azure OpenAI capacity fails over across deployments.
<img src="capacity-fail.png" alt="Proxy sends requests through APIM; traffic fails over from PTU 1 and PTU 2 to PTU 3" width="80%">
</td>
<td width="33%" valign="top">

### [🎯 Priority Routing](priority-routing.md)
APIM backend eligibility follows request priority.

<img src="routing.png" alt="Proxy and APIM route requests to GPT-4 Production or GPT-4 Turbo Dev" width="80%">
</td>
</tr>
<tr>
<td width="33%" valign="top">

### [💰 Chargeback](chargeback.md)
Usage telemetry gets attributed to the right caller.

<img src="chargeback.png" alt="A $2500 PTU cost divided among Team A, Team B, and Team C" width="80%">
</td>
<td width="33%" valign="top">

### [🔒 Secure the Proxy](secure-the-proxy.md)
Proxy ingress rejects unauthorized callers.

<img src="eazy-auth.png" alt="Client request passes through Easy Auth before reaching the proxy" width="80%">
</td>
<td width="33%" valign="top">

### [🛡️ Secure APIM](secure-apim.md)
APIM validates callers before forwarding.

<img src="jwt-validate.png" alt="Proxy request passes JWT validation at APIM" width="80%">

</td>
</tr>
</table>

> [!TIP]
> No real model endpoint handy? Use the [LLM Simulator](../how-to/llm-simulator.md) — it's built in and you don't need anything else.
