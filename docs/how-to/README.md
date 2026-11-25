# How-to Guides

Use these guides to complete a specific deployment or configuration task.

## TL;DR

- Configure backends, custom token providers, App Configuration, or request security.
- Deploy to Azure Container Apps using the prebuilt package.
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

Deploy the proxy and CompanionApp to **Azure Container Apps** using the prebuilt package.

[Deploy to Azure Container Apps →](../getting-started/container-apps.md)

</td>
<td width="33%" valign="top">

### Alternative Deployment Paths

Find interactive, custom-package, and older deployment guides.

[Alternative Deployment Paths →](../Legacy/README.md)

</td>
<td width="33%" valign="top">

### Common Scenarios

Canonical, **copy-paste-ready** configuration blocks for common deployment shapes, all using the connection string format for `Host1`–`Host9` so a scenario can be applied end to end.

[Common Scenarios →](common-scenarios.md)

</td>
</tr>
<tr>
<td width="33%" valign="top">

### Configure Companion Authentication

Protect **Companion App** with GitHub, Google, or OAuth2 bearer tokens; configure provider registration, Container App overrides, allowlists, callbacks, and session behavior.

[Configure Companion Authentication →](configure-companion-authentication.md)

</td>
<td width="33%" valign="top">

### LLM Simulator

Stand up a local **mock backend** to exercise the proxy without any cloud deployment — the included null server needs only **Python** and no extra dependencies.

[LLM Simulator →](llm-simulator.md)

</td>
</tr>
<tr>
<td width="33%" valign="top">

### Customize a Token Provider

Implement `IBackendTokenProvider`, register one or more implementations at startup, and select the exact implementation class per backend.

[Customize a Token Provider →](customize-token-provider.md)

</td>
</tr>
</table>
