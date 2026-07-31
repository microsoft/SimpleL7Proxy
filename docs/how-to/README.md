# How-to Guides

Use these guides to complete a specific deployment or configuration task.

## TL;DR

- Configure backends, custom token providers, App Configuration, or request security.
- Deploy to Azure Container Apps or as a sidecar.
- Use common scenarios and the simulator for repeatable setups.

## Quick Topics

<table>
<tr>
<td width="33%" valign="top">

### Configure Backends

The settings that separate a reliable AI gateway from one that fails under load: **backends**, **load balancing**, **circuit breaking**, and **timeouts** — and how to change them without taking a running container offline.

[Configure Backends →](configure-backends.md)

</td>
<td width="33%" valign="top">

### Configure App Configuration

The proxy reads settings from **Container App environment variables** or **Azure App Configuration**; the latter lets operators view, change, and reload **warm** settings from a central store without restarting a replica.

[Configure Azure App Configuration →](configure-app-configuration.md)

</td>
<td width="33%" valign="top">

### Configure Security

Reject or sanitize incoming requests before they enter the queue — enforce **required headers**, per-user **value allowlists**, and inbound **auth** so unknown app IDs or missing keys never reach a backend.

[Configure Security →](configure-security.md)

</td>
</tr>
<tr>
<td width="33%" valign="top">

### Deploy to Azure Container Apps

Build a **Docker** image from the `src/` directory and deploy it to **Azure Container Apps**, where the proxy serves traffic on port **443** and exposes its health probes on port **9000**.

[Deploy to Azure Container Apps →](deploy-container-apps.md)

</td>
<td width="33%" valign="top">

### Deploy as a Sidecar

Run the proxy as a **two-container** Container App: the **proxy** handles traffic while a separate **HealthProbe** sidecar answers liveness, readiness, and startup probes over shared `localhost` networking.

[Deploy as a Sidecar →](deploy-sidecar.md)

</td>
<td width="33%" valign="top">

### Common Scenarios

Canonical, **copy-paste-ready** configuration blocks for common deployment shapes, all using the connection string format for `Host1`–`Host9` so a scenario can be applied end to end.

[Common Scenarios →](common-scenarios.md)

</td>
</tr>
<tr>
<td width="33%" valign="top">

### LLM Simulator

Stand up a local **mock backend** to exercise the proxy without any cloud deployment — the included null server needs only **Python** and no extra dependencies.

[LLM Simulator →](llm-simulator.md)

</td>
<td width="33%" valign="top">

### Customize a Token Provider

Implement `IBackendTokenProvider`, register one or more implementations at startup, and select the exact implementation class per backend.

[Customize a Token Provider →](customize-token-provider.md)

</td>
</tr>
</table>
