# Content Brief: 🚀 Get It Running

> **Purpose:** Guide a new operator or developer from zero to a running proxy with traffic flowing to a backend. Every step must be runnable. Every command must produce visible output. No hand-waving.

---

## Quick Answers

<table>
<tr>
<td width="33%" valign="top">

### Minimum required before starting?
.NET 10 SDK (for local dev) or Docker + Azure CLI (for container deployment). Minimum config: `Port` and one `Host1` connection string. No other Azure resources required for a first run.

[→ Prerequisites](../QUICKSTART.md#prerequisites)

</td>
<td width="33%" valign="top">

### How do I run it locally?
`export Port=8000`, `export Host1="host=<url>;probe=/health"`, then `cd src/SimpleL7Proxy && dotnet run`. The proxy is ready when you see the startup banner in the console.

[→ Run as Code](../QUICKSTART.md#run-as-code)

</td>
<td width="33%" valign="top">

### How do I deploy to Azure?
Use the interactive deployment script in `deployment/README.md`. Fill in a parameters file, run the script, and ACA handles the rest. Port 8000 is the expected ingress target.

[→ Deploy to ACA](../QUICKSTART.md#deploy-to-azure-container-apps)

</td>
</tr>
<tr>
<td width="33%" valign="top">

### How do I point it at a backend?
Set `Host1` to a connection string: `host=https://<endpoint>;probe=/health`. Use `mode=direct` for serverless endpoints that don't support probing. See the LLM simulator for a no-Azure-needed backend.

[→ Backend host format](../BACKEND_HOSTS.md#configuring-hosts)

</td>
<td width="33%" valign="top">

### How do I verify it's working?
Call `curl -i http://localhost:8000/health` — a `200 OK` means the proxy is up. Send a test request and check the response for the `x-Request-Worker` header, which the proxy injects on every proxied response.

[→ Check the health probe](../QUICKSTART.md#check-the-health-probe)

</td>
<td width="33%" valign="top">

### What if it doesn't start?
Check that `Host1` is reachable and the probe path returns 2xx. Review the console log and `eventslog.json`. Most startup failures are a missing `Host1`, an unreachable backend, or a bad connection string key.

> **⚠️ GAP:** No "top 3 startup failure" quick-reference exists. → [Content gap details](#content-gaps-to-fill)

</td>
</tr>
</table>

---

## Reader Profile

| | |
|---|---|
| **Who** | New operators, developers doing a first deployment, anyone setting up a demo or dev environment |
| **Why they come here** | They want a working proxy — not theory — as fast as possible |
| **When they read this** | First deployment; setting up a new environment; onboarding |

---

## Questions this section MUST answer

### Before I start
- [ ] What do I need installed before I can run the proxy? (.NET version, Docker, Azure CLI, subscriptions)
- [ ] What is the absolute minimum configuration required to start? (Port + Host1)
- [ ] Do I need any Azure resources before my first run, or can I run it fully locally?

### How do I run it?
- [ ] How do I run the proxy from source locally? (exact commands)
- [ ] How do I run the proxy as a Docker container locally?
- [ ] How do I deploy the proxy to Azure Container Apps? (minimal steps)
- [ ] What port does the proxy listen on by default?

### How do I point it at a backend?
- [ ] What is the `Host1` connection string format? (minimal valid example)
- [ ] What is the `probe` path and why is it required?
- [ ] Can I use the included LLM simulator as a backend so I don't need a real Azure OpenAI endpoint?

### How do I know it's working?
- [ ] What health endpoints can I call to verify the proxy is up? (`/liveness`, `/readiness`, `/startup`)
- [ ] What does a healthy response look like?
- [ ] How do I send a test request and see it proxied?
- [ ] What headers does the proxy add to the response so I can confirm it passed through?

### What do I do when it doesn't start?
- [ ] What are the three most common startup failures and how do I fix each?
- [ ] Where do I look for startup logs?

---

## What the reader can do AFTER reading this

- [ ] Proxy is running and accepting requests
- [ ] At least one backend is healthy in the backend pool
- [ ] Can hit `/readiness` and see a `200 OK`
- [ ] Can send a request and see it proxied with `x-Request-Worker` in the response headers
- [ ] Knows where to go next: CONFIGURATION_CATEGORIES for tuning, or a POC for validation

---

## Existing documents that cover this area

| Document | What it covers | Gap? |
|----------|----------------|------|
| [QUICKSTART.md](../QUICKSTART.md) | First-run guide for ACA and local | Primary document — verify it is self-contained |
| [BEGINNER_DEVELOPMENT.md](../BEGINNER_DEVELOPMENT.md) | Local dev from source | Covers dev path; verify it links to QUICKSTART |
| [CONTAINER_DEPLOYMENT.md](../CONTAINER_DEPLOYMENT.md) | ACA deployment detail | May have more depth than needed here — link from QUICKSTART |
| [DUMMY_BACKEND.md](../DUMMY_BACKEND.md) | LLM simulator setup | Critical for readers without a real backend |
| [ENVIRONMENT_VARIABLES.md](../ENVIRONMENT_VARIABLES.md) | Minimum required config section | Needs a clear "minimum config" callout |

---

## Content gaps to fill

- [ ] A single "5 commands to a running proxy" block at the very top — no prerequisites section before it
- [ ] A working `Host1` connection string example with a real probe path (`host=http://localhost:9000;probe=/health`)
- [ ] A "what you will see" block: exact expected output when the proxy starts successfully
- [ ] A table: "symptom → cause → fix" for the three most common startup failures
- [ ] A clear link to the LLM simulator as the recommended backend for first-time runs
