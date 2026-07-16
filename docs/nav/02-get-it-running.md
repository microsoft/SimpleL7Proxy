# Running the Proxy: From Zero to Proxying Traffic in Minutes

Two environment variables, one command. Here's what to set up, what commands to run, and how to confirm it's working.

---

## Quick Answers

<table>
<tr>
<td width="33%" valign="top">

### Minimum required before starting?
.NET 10 SDK (for local dev) or Docker + Azure CLI (for container deployment). Minimum config: `Port` and one `Host1` connection string. No other Azure resources required for a first run.

[→ Minimum required before starting?](#minimum-required-before-starting)

</td>
<td width="33%" valign="top">

### How do I run it locally?
`export Port=8000`, `export Host1="host=<url>;probe=/health"`, then `cd src/SimpleL7Proxy && dotnet run`. The proxy is ready when you see the startup banner in the console.

[→ How do I run it?](#how-do-i-run-it)

</td>
<td width="33%" valign="top">

### How do I deploy to Azure?
Use the interactive deployment script in `deployment/README.md`. Fill in a parameters file, run the script, and Azure Container Apps (ACA) handles the rest. Port 8000 is the expected ingress target.

[→ How do I run it?](#how-do-i-run-it)

</td>
</tr>
<tr>
<td width="33%" valign="top">

### How do I point it at a backend?
Set `Host1` to a connection string: `host=https://<endpoint>;probe=/health`. Use `mode=direct` for serverless endpoints that don't support probing. See the LLM simulator for a no-Azure-needed backend.

[→ How do I point it at a backend?](#how-do-i-point-it-at-a-backend)

</td>
<td width="33%" valign="top">

### How do I verify it's working?
Call `curl -i http://localhost:8000/health` — a `200 OK` means the proxy is up. Send a test request and check the response for the `x-Request-Worker` header, which the proxy injects on every proxied response.

[→ How do I verify it's working?](#how-do-i-verify-its-working)

</td>
<td width="33%" valign="top">

### What if it doesn't start?
Check that `Host1` is reachable and that the probe path returns a success response (HTTP 200–299). Review the console log and `eventslog.json`. Most startup failures trace back to a missing `Host1`, an unreachable backend, or a bad connection string key.

> **⚠️ GAP:** No "top 3 startup failure" quick-reference exists. → [Content gap details](#content-gaps-to-fill)

[→ What if it doesn't start?](#what-if-it-doesnt-start)

</td>
</tr>
</table>

---

## Full Answers

### Minimum required before starting?

#### What do I need installed before I can run the proxy? (.NET version, Docker, Azure CLI, subscriptions)

SimpleL7Proxy requires .NET 10 and Git for source-based local work. For local containers, add Docker. For Azure deployment, add Azure CLI, `azd`, and a subscription that can create Container Apps resources.

#### What is the absolute minimum configuration required to start? (Port + Host1)

SimpleL7Proxy needs two things to start: `Port` (the port to listen on, typically `8000`) and one backend via `Host1` in the format `host=https://api.example.com;probe=/health`. If your backend has no health-check URL, append `mode=direct` instead of a probe path — this tells the proxy to treat the backend as always available without polling it. See [→ Direct Mode](../Glossary.md#backend-management).

```bash
export Port=8000
export Host1="host=https://api.example.com;probe=/health"
```

#### Do I need any Azure resources before my first run, or can I run it fully locally?

SimpleL7Proxy can run fully locally first, especially if you use the included mock backend or simulator — no Azure account or subscription required for an initial run.

---

### How do I run it?

#### How do I run the proxy from source locally? (exact commands)

SimpleL7Proxy starts with two environment variables and one command:

```bash
export Port=8000
export Host1="host=http://localhost:9000;probe=/health"
cd src/SimpleL7Proxy && dotnet run
```

The proxy is ready when you see the startup banner in the console.

#### How do I run the proxy as a Docker container locally?

SimpleL7Proxy packages as a container. Build from `src`, then run with environment variables:

```bash
docker build -t simplel7proxy:latest -f SimpleL7Proxy/Dockerfile .
docker run -p 8000:443 -e "Host1=host=https://api.example.com;probe=/health" simplel7proxy:latest
```

#### How do I deploy the proxy to Azure Container Apps? (minimal steps)

SimpleL7Proxy deploys to ACA via the included scripts. The shortest documented path is:

```bash
.azure/setup.sh       # provision infrastructure
azd provision
# optionally seed App Configuration
.azure/deploy.sh      # deploy the container
```

#### What port does the proxy listen on by default?

Use `Port=8000` for local development — it's the value all examples in this documentation use, and it avoids the elevated-permission requirement that Linux places on ports below 1024 (including the built-in default of `80`). Inside the container image, the proxy also listens on port `443` (TLS). For ACA deployments using `Port=8000`, point the ingress rule at port `8000`.

---

### How do I point it at a backend?

#### What is the `Host1` connection string format? (minimal valid example)

SimpleL7Proxy uses a semicolon-delimited connection string for each backend:

```
Host1="host=https://api.example.com;probe=/health"
```

The `host` and `probe` keys are the minimum for a probed backend.

#### What is the `probe` path and why is it required?

SimpleL7Proxy uses the probe path to call each backend on a schedule (every 15 seconds by default) to confirm it is alive. A probe must return a success response (HTTP 200–299) for the backend to stay in the [active pool](../Glossary.md#backend-management) and receive traffic. If your backend has no health-check endpoint, use `mode=direct` to skip health polling and always treat the backend as available. See [→ Direct Mode](../Glossary.md#backend-management).

![Health probe flow](../helthprobe.png)

#### Can I use the included LLM simulator as a backend so I don't need a real Azure OpenAI endpoint?

SimpleL7Proxy includes a mock backend and LLM simulator specifically for this purpose — you can validate the full proxy pipeline, including `429` throttling and streaming responses, without a real Azure OpenAI deployment. See [→ LLM Simulator](../DUMMY_BACKEND.md).

---

### How do I verify it's working?

#### What health endpoints can I call to verify the proxy is up? (`/liveness`, `/readiness`, `/startup`)

SimpleL7Proxy exposes `/liveness`, `/readiness`, and `/startup` probes. Use `/health` as a simple liveness alias.

```bash
curl -i http://localhost:8000/health
# expect: HTTP/1.1 200 OK
```

#### What does a healthy response look like?

SimpleL7Proxy returns `200 OK` with an `OK` body for a healthy liveness check. The readiness probe also confirms at least one backend is in the active pool — if readiness returns non-200, no healthy backends are available.

#### How do I send a test request and see it proxied?

SimpleL7Proxy injects `BackendHost` into every proxied response. Send a request and check for that header:

```bash
curl -i http://localhost:8000/your/path
# look for: BackendHost: https://api.example.com
```

If `BackendHost` is absent, the request did not pass through the proxy.

#### What headers does the proxy add to the response so I can confirm it passed through?

SimpleL7Proxy adds five headers to every proxied response: `BackendHost` (the backend URL used), `x-Request-Worker` (which worker processed it), `x-Request-Queue-Duration` (milliseconds the request waited in the queue), `x-Request-Process-Duration` (milliseconds the backend took to respond), and `Total-Latency` (end-to-end time). Seeing `BackendHost` is the simplest confirmation.

---

### What if it doesn't start?

#### What are the three most common startup failures and how do I fix each?

SimpleL7Proxy startup fails in three predictable ways:

| Symptom | Cause | Fix |
|---------|-------|-----|
| `Host1` parse error or no backends registered | Missing or malformed `Host1` | Verify the connection string contains `host=` and matches `host=<url>;probe=<path>` exactly |
| Probe fails immediately, backend never enters pool | Backend unreachable or probe path returns a non-success response | Verify the backend URL and probe path are reachable from the proxy's network |
| App Configuration connection refused or `403` | Missing endpoint, wrong label, or identity missing role | Verify `AZURE_APPCONFIG_ENDPOINT`, the label, and that the managed identity has `App Configuration Data Reader` |

In all cases, the console output and `eventslog.json` include the specific error message.

#### Where do I look for startup logs?

SimpleL7Proxy writes startup events to the console and to `eventslog.json` in its working directory. For containers, also check `docker logs <container>`.

---

## You Should Now Be Able To

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
