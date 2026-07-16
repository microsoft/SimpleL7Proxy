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
  **Answer:** For source-based local work you need .NET 10 and Git, for local containers you need Docker, and for Azure deployment you need Azure CLI, `azd`, and a subscription that can create Container Apps resources.
- [ ] What is the absolute minimum configuration required to start? (Port + Host1)
  **Answer:** The minimum is `Port` (the port to listen on, typically `8000`) and one backend via `Host1` in the format `host=https://api.example.com;probe=/health`. If your backend has no health-check URL, append `mode=direct` instead of a probe path — this tells the proxy to treat the backend as always available without polling it. See [→ Direct Mode](../Glossary.md#backend-management).
- [ ] Do I need any Azure resources before my first run, or can I run it fully locally?
  **Answer:** You can run it fully locally first, especially if you use the included mock backend or simulator.

### How do I run it?
- [ ] How do I run the proxy from source locally? (exact commands)
  **Answer:** Set `Port` and `Host1` as environment variables, then run `dotnet run` from the `src/SimpleL7Proxy/` directory. Example: `export Port=8000 && export Host1="host=http://localhost:9000;probe=/health" && cd src/SimpleL7Proxy && dotnet run`.
- [ ] How do I run the proxy as a Docker container locally?
  **Answer:** Build from `src` with `docker build -t simplel7proxy:latest -f SimpleL7Proxy/Dockerfile .`, then run `docker run -p 8000:443 -e "Host1=host=https://api.example.com;probe=/health" simplel7proxy:latest`.
- [ ] How do I deploy the proxy to Azure Container Apps? (minimal steps)
  **Answer:** The shortest documented path is `.azure/setup.sh`, `azd provision`, optional App Configuration seeding, and then `.azure/deploy.sh`.
- [ ] What port does the proxy listen on by default?
  **Answer:** The runtime default listen port is `80`. Examples in this documentation set `Port=8000` explicitly to avoid the elevated-permission requirement for ports below 1024 on Linux. Inside the container image, the proxy also listens on port `443` (TLS). For Azure Container Apps deployments that set `Port=8000`, the ACA ingress rule should target port `8000`.

### How do I point it at a backend?
- [ ] What is the `Host1` connection string format? (minimal valid example)
  **Answer:** The minimal recommended form is `Host1="host=https://api.example.com;probe=/health"`.
- [ ] What is the `probe` path and why is it required?
  **Answer:** The probe path is the URL path the [health poller](../Glossary.md#backend-management) calls on a schedule (every 15 seconds by default) to check whether the backend is alive. A probe must return `2xx` for the backend to stay in the [active pool](../Glossary.md#backend-management) and receive traffic. If your backend has no health-check endpoint, use `mode=direct` instead of a probe path — that tells the proxy to skip health polling and always treat the backend as available. See [→ Direct Mode](../Glossary.md#backend-management).
- [ ] Can I use the included LLM simulator as a backend so I don't need a real Azure OpenAI endpoint?
  **Answer:** Yes, the repo includes mock and simulator backends specifically so you can validate the proxy without a real Azure OpenAI deployment.

### How do I know it's working?
- [ ] What health endpoints can I call to verify the proxy is up? (`/liveness`, `/readiness`, `/startup`)
  **Answer:** Call `/liveness`, `/readiness`, and `/startup`, or use `/health` as the simple liveness alias.
- [ ] What does a healthy response look like?
  **Answer:** A healthy probe returns `200 OK`, usually with an `OK` body, while readiness also implies at least one backend is healthy.
- [ ] How do I send a test request and see it proxied?
  **Answer:** Send a normal `curl` request to the proxy host and port. Confirm the response includes the `BackendHost` header — that header is injected by the proxy and shows which backend was used. If `BackendHost` is absent, the request did not pass through the proxy.
- [ ] What headers does the proxy add to the response so I can confirm it passed through?
  **Answer:** The proxy adds these headers to every proxied response: `BackendHost` (the backend URL used), `x-Request-Worker` (which worker processed it), `x-Request-Queue-Duration` (milliseconds the request waited in the queue), `x-Request-Process-Duration` (milliseconds the backend took to respond), and `Total-Latency` (end-to-end time). Seeing `BackendHost` in the response is the simplest confirmation that the request passed through the proxy.

### What do I do when it doesn't start?
- [ ] What are the three most common startup failures and how do I fix each?
  **Answer:** The three most common startup failures: (1) **Missing or malformed `Host1`** — verify the connection string contains a `host=` key and the format matches `host=<url>;probe=<path>` exactly; (2) **Unreachable backend or probe path** — verify the backend URL is reachable from the proxy's network and that the probe path returns `2xx`; (3) **App Configuration or auth mismatch** — verify `AZURE_APPCONFIG_ENDPOINT`, the configured label, and that the managed identity has the `App Configuration Data Reader` RBAC role (Role-Based Access Control — Azure's permission system). In all cases, the console output and `eventslog.json` (written to the proxy's working directory) include the specific error message.
- [ ] Where do I look for startup logs?
  **Answer:** Start with the console output, then inspect `eventslog.json`, and use `docker logs` as well if you launched the container locally.

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
