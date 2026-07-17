# SimpleL7Proxy

Depending on whether you are serving live users, high-priority business workflows, or low-priority background jobs, you will likely want control over when, where, and how your traffic is fulfilled. AI backends can throttle, regions can become constrained, and models eventually reach the end of their lifecycle. These are some of the reasons teams place a proxy in front of their AI services. The questions below are the ones most teams ask when deciding whether this approach fits their architecture.

---

## Where do you want to start?

<table>
<tr>
<td width="33%" valign="top">

### [🔍 Understand the proxy](docs/nav/01-understand-the-proxy.md)
Architecture, components, and how requests flow end to end.

**For:** architects and evaluators deciding whether to adopt.

</td>
<td width="33%" valign="top">

### [🚀 Get it running](docs/nav/02-get-it-running.md)
Deploy to Azure Container Apps or run locally from source in minutes.

**For:** operators and developers doing a first deployment.

</td>
<td width="33%" valign="top">

### [⚙️ Configure backends and settings](docs/nav/03-configure-backends-and-settings.md)
Environment variables, host setup, load balancing, and hot-reload settings.

**For:** operators tuning a running deployment.

</td>
</tr>
<tr>
<td width="33%" valign="top">

### [🧪 Try a proof of concept](docs/nav/04-try-a-proof-of-concept.md)
Walkthrough guides for failover, priority routing, and chargeback — each runnable in under 5 minutes.

**For:** engineers validating behavior or preparing a demo.

</td>
<td width="33%" valign="top">

### [🔧 Diagnose a problem](docs/nav/05-diagnose-a-problem.md)
Find your symptom — 429s, 503s, a stuck circuit breaker, async not completing — and fix it fast.

**For:** anyone debugging broken or unexpected behavior.

</td>
<td width="33%" valign="top">

### [💻 Develop and contribute](docs/nav/06-develop-and-contribute.md)
Run from source, understand the internals, and contribute changes.

**For:** developers building on or contributing to the proxy.

</td>
</tr>
</table>

---

> **Not sure where to start?** Run the [Failover POC](docs/POC-Failover-configuration.md) first — it exercises the core behavior in under 5 minutes and makes the architecture concrete before you read anything else.
