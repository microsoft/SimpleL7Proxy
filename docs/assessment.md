# Documentation Goal Assessment

This assessment groups the documentation by how clearly each page serves a reader goal.

## TL;DR

- **50 documents have one clear goal**, including indexes and focused references.
- **22 cover multiple related goals** that can remain together when the page has one clear purpose.
- **11 need structural attention:** split 6 broad pages and reorganize 5 unorganized or misplaced pages.

## How to read the assessment

- **Single goal:** the page has one clear reader outcome.
- **Multiple related goals:** the page covers several related topics under one understandable purpose.
- **Split:** topics serve different reader tasks and belong in separate pages.
- **Reorganize:** the page's structure, ordering, duplication, or placement obscures its purpose.

## Single goal — 50 documents

| Document | Reader goal |
|---|---|
| [docs/README.md](README.md) | Navigate the documentation. |
| [reference/README.md](reference/README.md) | Find a reference topic. |
| [reference/Tokenomics-event-spec.md](reference/Tokenomics-event-spec.md) | Look up the Tokenomics policy-decision event contract. |
| [reference/backend-hosts.md](reference/backend-hosts.md) | Configure backend hosts. |
| [reference/backend-log.md](reference/backend-log.md) | Interpret the backend activity log. |
| [reference/circuit-breaker.md](reference/circuit-breaker.md) | Understand and configure circuit breaking. |
| [reference/configuration-categories.md](reference/configuration-categories.md) | Choose configuration settings by category. |
| [reference/glossary.md](reference/glossary.md) | Look up project terminology. |
| [reference/load-balancing.md](reference/load-balancing.md) | Understand backend selection and retries. |
| [reference/security.md](reference/security.md) | Report a security vulnerability. |
| [reference/storage-blob-config.md](reference/storage-blob-config.md) | Configure blob retention and lifecycle. |
| [reference/timeouts.md](reference/timeouts.md) | Understand request time budgets and overrides. |
| [how-to/README.md](how-to/README.md) | Choose a task-specific guide. |
| [how-to/DUMMY_BACKEND.md](how-to/DUMMY_BACKEND.md) | Redirect to mock-backend guidance; retained because a current guide links to it. |
| [how-to/configure-app-configuration.md](how-to/configure-app-configuration.md) | Configure and operate App Configuration. |
| [how-to/customize-token-provider.md](how-to/customize-token-provider.md) | Implement and select a custom backend token provider. |
| [Legacy/deploy-sidecar.md](Legacy/deploy-sidecar.md) | Deploy the proxy with a HealthProbe sidecar. |
| [troubleshooting/README.md](troubleshooting/README.md) | Find a troubleshooting guide by symptom. |
| [troubleshooting/app-configuration.md](troubleshooting/app-configuration.md) | Diagnose App Configuration loading or refresh. |
| [troubleshooting/async-202-never-issued.md](troubleshooting/async-202-never-issued.md) | Diagnose why a request did not switch to async mode. |
| [troubleshooting/backend-hosts.md](troubleshooting/backend-hosts.md) | Diagnose backend host health or discovery. |
| [troubleshooting/circuit-breaker.md](troubleshooting/circuit-breaker.md) | Diagnose a circuit breaker stuck open. |
| [troubleshooting/event-hub.md](troubleshooting/event-hub.md) | Diagnose missing Event Hub messages. |
| [troubleshooting/requests-400-invalid-ttl.md](troubleshooting/requests-400-invalid-ttl.md) | Diagnose invalid TTL format responses. |
| [troubleshooting/requests-412.md](troubleshooting/requests-412.md) | Diagnose TTL expiration and 412 responses. |
| [faq/README.md](faq/README.md) | Find an answer by question. |
| [faq/async-operation.md](faq/async-operation.md) | Decide when a client should stop waiting synchronously. |
| [faq/direct-vs-apim.md](faq/direct-vs-apim.md) | Choose between direct backend and APIM routing. |
| [faq/queueing-fairness.md](faq/queueing-fairness.md) | Understand queue fairness mechanisms. |
| [concepts/architecture.md](concepts/architecture.md) | Understand the system at a high level. |
| [concepts/observability.md](concepts/observability.md) | Understand telemetry and observability. |
| [concepts/reliability.md](concepts/reliability.md) | Understand failure handling. |
| [concepts/request-lifecycle.md](concepts/request-lifecycle.md) | Follow a request through its lifecycle. |
| [concepts/tokenomics_scenario.md](concepts/tokenomics_scenario.md) | Walk through one limits, budgets, and actions scenario. |
| [getting-started/connect-apim.md](getting-started/connect-apim.md) | Connect the proxy to APIM. |
| [getting-started/connect-endpoint.md](getting-started/connect-endpoint.md) | Connect directly to Azure OpenAI or AI Foundry. |
| [getting-started/connect-llm-simulator.md](getting-started/connect-llm-simulator.md) | Connect to the LLM Simulator. |
| [getting-started/verify.md](getting-started/verify.md) | Verify startup and proxied requests. |
| [pocs/README.md](pocs/README.md) | Choose a proof of concept. |
| [pocs/failover.md](pocs/failover.md) | Demonstrate backend failover. |
| [pocs/secure-apim.md](pocs/secure-apim.md) | Demonstrate JWT validation at APIM. |
| [pocs/secure-the-proxy.md](pocs/secure-the-proxy.md) | Demonstrate protecting proxy ingress. |
| [internal/README.md](internal/README.md) | Find internal documentation. |
| [internal/content-gaps.md](internal/content-gaps.md) | Track documentation gaps. |
| [internal/design-notes/README.md](internal/design-notes/README.md) | Find internal design notes. |
| [internal/design-notes/async-branch-changes.md](internal/design-notes/async-branch-changes.md) | Record one historical branch comparison. |
| [internal/design-notes/disposal-architecture.md](internal/design-notes/disposal-architecture.md) | Document disposal architecture. |
| [internal/troubleshooting-todo.md](internal/troubleshooting-todo.md) | Track troubleshooting-guide work. |
| [contributing/README.md](contributing/README.md) | Find contributor guidance. |
| [contributing/testing.md](contributing/testing.md) | Validate code and documentation changes. |

## Multiple related goals — 22 documents

| Document | Topics and assessment |
|---|---|
| [reference/ai-foundry-integration.md](reference/ai-foundry-integration.md) | Azure AI Foundry/OpenAI integration and configuration; coherent feature reference. |
| [reference/environment-variables.md](reference/environment-variables.md) | Variables across features; the exhaustive lookup purpose is clear. |
| [reference/headers-and-status-codes.md](reference/headers-and-status-codes.md) | Response statuses, proxy headers, and protocol signals; coherent protocol reference. |
| [reference/health-endpoints.md](reference/health-endpoints.md) | Health endpoints and reliability modes; keep deployment instructions elsewhere. |
| [reference/user-profiles.md](reference/user-profiles.md) | Profile source, identity, rules, validation, async, and security; broad but cohesive. |
| [how-to/common-scenarios.md](how-to/common-scenarios.md) | Several copy-ready configurations; an intentional cookbook. |
| [how-to/configure-companion-authentication.md](how-to/configure-companion-authentication.md) | GitHub, Google, and OAuth as alternative Companion App authentication methods. |
| [how-to/configure-security.md](how-to/configure-security.md) | Header, app-ID, shared-key, and OAuth controls under one request-protection goal. |
| [how-to/llm-simulator.md](how-to/llm-simulator.md) | Mock-server options and scenarios for local simulation. |
| [troubleshooting/async-requests.md](troubleshooting/async-requests.md) | Empty blobs, missing URIs, timeouts, and incomplete async requests. |
| [troubleshooting/health-probes.md](troubleshooting/health-probes.md) | Probe failures, sidecar status, and orchestrator restarts. |
| [troubleshooting/requests-429.md](troubleshooting/requests-429.md) | Distinguishes admission/enqueue 429s from backend/APIM throttling. |
| [troubleshooting/requests-503.md](troubleshooting/requests-503.md) | Distinguishes proxy/backend and health-endpoint 503/502 causes. |
| [faq/apim-backend-routing.md](faq/apim-backend-routing.md) | Priority, load balancing, throttling, failover, and requeue; monitor scope. |
| [faq/observability-telemetry.md](faq/observability-telemetry.md) | Telemetry channels, event data, token counts, and sinks under observability. |
| [faq/priority-levels.md](faq/priority-levels.md) | Priority mapping, proxy/APIM roles, and backend/retry effects; keep setup detail in references. |
| [faq/user-profiles.md](faq/user-profiles.md) | Profile identity, priority, model overrides, and async behavior. |
| [concepts/README.md](concepts/README.md) | Overview hub; remove repeated introductory explanations of async, telemetry, and scaling. |
| [concepts/UserProfiles.md](concepts/UserProfiles.md) | Profile source, identity, rules, precedence, and verification; exact settings belong in references. |
| [getting-started/container-apps.md](getting-started/container-apps.md) | ACA deployment methods; link to canonical detailed workflows. |
| [getting-started-developer/README.md](getting-started-developer/README.md) | Source and Docker alternatives for running locally. |
| [pocs/chargeback.md](pocs/chargeback.md) | Connectivity, telemetry setup, validation, and sinks for one chargeback demonstration. |

## Split into different documents — 6 documents

| Document | Recommended change |
|---|---|
| [reference/advanced-configuration.md](reference/advanced-configuration.md) | Separate priority mapping, header validation, and per-user queue preference, or make this page an index. |
| [reference/configuration.md](reference/configuration.md) | Split its server, profiles, validation, endpoints, logging, and async settings by domain, or make it a reference index. |
| [how-to/configure-backends.md](how-to/configure-backends.md) | Re-home its broad FAQ coverage of hosts, balancing, retries, timeouts, circuit breakers, workers, App Configuration, and governance. |
| [Legacy/deploy-container-apps.md](Legacy/deploy-container-apps.md) | Separate AZD, manual CLI, image updates, monitoring, and GitHub Actions deployment paths. |
| [faq/resilience-autoscaling.md](faq/resilience-autoscaling.md) | Separate proxy resilience from ACA scaling, probes, and draining. |
| [contributing/development.md](contributing/development.md) | Move operational feature setup and mock load testing to their relevant guides; retain developer workflow guidance. |

## Reorganize or reclassify — 5 documents

| Document | Issue and next step |
|---|---|
| [getting-started/README.md](getting-started/README.md) | Combines deployment choice, Companion App auth, backend/model/pricing setup, telemetry, and verification; rebuild as a short run → connect → verify path with links out. |
| [pocs/openai-failover.md](pocs/openai-failover.md) | Repeats the POC structure; consolidate into one walkthrough. |
| [pocs/priority-routing.md](pocs/priority-routing.md) | A complete POC is followed by an older setup/configuration/test block; merge unique material and remove duplication. |
| [contributing/architecture.md](contributing/architecture.md) | Mixes overview, dependencies, source map, and runtime glossary; separate the architecture explanation from the code map and add an H1. |
| [concepts/async-processing.md](concepts/async-processing.md) | Focused topic, but primarily configuration/setup material in the concepts section; move/reclassify it or refocus it on conceptual explanation. |

## Redirect cleanup

Ten moved stubs with no inbound links to those stub paths were removed. The [DUMMY_BACKEND.md](how-to/DUMMY_BACKEND.md) compatibility page remains because [connect-llm-simulator.md](getting-started/connect-llm-simulator.md) links to it directly.

