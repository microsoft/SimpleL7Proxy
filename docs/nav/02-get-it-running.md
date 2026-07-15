# Content Brief: 🚀 Get It Running

> **Purpose:** Guide a new operator or developer from zero to a running proxy with traffic flowing to a backend. Every step must be runnable. Every command must produce visible output. No hand-waving.

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
