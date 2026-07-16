# SimpleL7Proxy

Depending on whether you are evaluating the architecture, deploying your first instance, tuning production traffic, validating behavior, diagnosing a failure, or contributing code, you will need a different path through the proxy documentation.

AI backends behave differently from normal HTTP services — they throttle, retry, and partially fail in ways that standard load balancers cannot handle. SimpleL7Proxy fills that gap: it sits between your clients and your Azure AI backends and adds priority queuing, health-aware routing, circuit breaking, per-user governance, and per-request telemetry.

---

## Where do you want to start?

<table>
<tr>
<td width="33%" valign="top">

### 🔍 Understand the proxy
Architecture, components, and how requests flow end to end.

**For:** architects and evaluators deciding whether to adopt.

[→ Read the Overview](docs/nav/01-understand-the-proxy.md)

</td>
<td width="33%" valign="top">

### 🚀 Get it running
Deploy to Azure Container Apps or run locally from source in minutes.

**For:** operators and developers doing a first deployment.

[→ Follow the Quickstart](docs/nav/02-get-it-running.md)

</td>
<td width="33%" valign="top">

### ⚙️ Configure backends and settings
Environment variables, host setup, load balancing, and hot-reload settings.

**For:** operators tuning a running deployment.

[→ See Configuration](docs/nav/03-configure-backends-and-settings.md)

</td>
</tr>
<tr>
<td width="33%" valign="top">

### 🧪 Try a proof of concept
Walkthrough guides for failover, priority routing, and chargeback — each runnable in under 5 minutes.

**For:** engineers validating behavior or preparing a demo.

[→ Browse POC guides](docs/nav/04-try-a-proof-of-concept.md)

</td>
<td width="33%" valign="top">

### 🔧 Diagnose a problem
Find your symptom — 429s, 503s, a stuck circuit breaker, async not completing — and fix it fast.

**For:** anyone debugging broken or unexpected behavior.

[→ Open Troubleshooting](docs/nav/05-diagnose-a-problem.md)

</td>
<td width="33%" valign="top">

### 💻 Develop and contribute
Run from source, understand the internals, and contribute changes.

**For:** developers building on or contributing to the proxy.

[→ Start Development](docs/nav/06-develop-and-contribute.md)

</td>
</tr>
</table>

---

> **Not sure where to start?** Run the [Failover POC](docs/POC-Failover-configuration.md) first — it exercises the core behavior in under 5 minutes and makes the architecture concrete before you read anything else.
