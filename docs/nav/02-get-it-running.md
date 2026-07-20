# Running the Proxy: From Zero to Proxying Traffic in Minutes

You can rung the proxy locally from the source code or container or deployed it to a container service such as the Azure Container Apps.   After you deploy, come back to this guide to configure it.

---

## Step 1: Choose Your Setup

**Choose where the proxy will run.** The next page asks which backend and authentication path you want.

<table>
<tr>
<td width="50%" valign="top" style="background-color:#EEF3F8; border-radius:8px; padding:16px;">

### [💻 Run Locally](02-run-locally.md)

<div align="center">

```sh:
# You need:

* Dotnet 10

# or

* Docker
```

</div>

</td>
<td width="50%" valign="top" style="background-color:#EEF7F1; border-radius:8px; padding:16px;">

### [☁️ Run in Cloud](02-run-in-container-apps.md)


![alt text](image.png)
</td>
</tr>
</table>

---

## Step 2: Where are your LLM models?

**Choose where the are .** The next page asks which backend and authentication path you want.

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

## Related Documents

| Document | What it covers |
|----------|----------------|
| [Quickstart](../QUICKSTART.md) | First-run instructions for Azure Container Apps and local environments |
| [Beginner Development](../BEGINNER_DEVELOPMENT.md) | Local development from source |
| [Container Deployment](../CONTAINER_DEPLOYMENT.md) | Detailed Azure Container Apps deployment |
| [Dummy Backend](../DUMMY_BACKEND.md) | LLM simulator setup for testing without a real backend |
| [Environment Variables](../ENVIRONMENT_VARIABLES.md) | Minimum and complete runtime configuration |

---
