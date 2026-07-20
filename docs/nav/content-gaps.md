# Navigation Documentation Gap Backlog

This file consolidates all content gaps that were previously embedded in individual `docs/nav/*.md` guides.

## 01-understand-the-proxy.md

- A single annotated architecture diagram (one diagram, not many) covering the full pipeline: client → queue → worker → backend selector → circuit breaker → backend → telemetry
- A "not this, but that" table comparing the proxy to common alternatives (APIM, nginx, Azure Front Door)
- A "non-goals" list so readers know what to stop looking for
- A one-paragraph plain-English answer to "what problem does this solve?"

## 02-get-it-running.md

- A single "5 commands to a running proxy" block at the very top — no prerequisites section before it
- A working `Host1` connection string example with a real probe path (`host=http://localhost:9000;probe=/health`)
- A "what you will see" block: exact expected output when the proxy starts successfully
- A table: "symptom → cause → fix" for the three most common startup failures
- A clear link to the LLM simulator as the recommended backend for first-time runs

## 03-configure-backends-and-settings.md

- Guidance on workers-per-backend sizing formula
- A "start here" decision tree: what kind of workload? → which settings matter?
- An annotated minimal `Host1` connection string with all optional keys explained inline
- A single "Warm / Cold / Hidden" table at the top of the config section so operators know what they can change live
- A worked example: two backends, path routing, different timeouts — shows all the moving parts together
- A "do not set these unless you understand them" callout for dangerous settings (`Workers`, `CBErrorThreshold`)

## 04-try-a-proof-of-concept.md

- Every POC must have a "TL;DR < 5 min" section at the top with numbered steps and expected output
- Every POC must have a "What you will observe" block listing behavior as pure bullets (not narrative)
- Every POC must have a verification checklist (not a table — a checklist of pass/fail signals)
- Every POC must have a "why this happened" state machine (even a simple 3-state diagram)
- Every POC must be verifiable without Azure App Insights (observable from response headers alone)
- Add a POC index page that shows all scenarios at a glance with a one-line description of what each proves

## 05-diagnose-a-problem.md

- Every guide must end with a "Verification" checklist (not a table) — explicit pass/fail signals
- `TroubleshootTOC.md` should show the most common symptoms first, rare ones last
- Add a "first 5 checks" block at the top of `TroubleshootTOC.md` for when you don't know the symptom yet
- Distinguish proxy-generated error codes from backend pass-through codes in every guide
- Add a "what you will see in logs / App Insights" note to each guide so SREs can correlate

## 06-develop-and-contribute.md

- A "3-command quickstart" block at the very top: build → run → test (exact commands, expected output)
- A class-responsibility table: `Server.cs` → listens; `ProxyWorker.cs` → processes; `BackendSelector.cs` → picks host; etc.
- An annotated request flow showing which class handles which step (same pipeline as architecture but mapped to filenames)
- A "where to add X" guide: new config var, new header, new validation rule, new event — with exact file paths
- Explicit coding standard section (or link to `.editorconfig` / `copilot-instructions.md`)
- A contributor checklist: what to do before submitting a PR (tests pass, no secrets, lint clean, PR template filled)
