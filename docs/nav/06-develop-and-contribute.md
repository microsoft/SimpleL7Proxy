# Content Brief: 💻 Develop and Contribute

> **Purpose:** Get a developer from zero to a running local build, navigating the codebase, running tests, and able to make a meaningful change — without guessing at project conventions.

---

## Quick Answers

<table>
<tr>
<td width="33%" valign="top">

### How do I build and run from source?
Install .NET 10 SDK. Set `Port` and `Host1`, then `cd src/SimpleL7Proxy && dotnet run`. For VS Code, add a `.vscode/launch.json` with the env vars and press F5.

[→ Local setup](../BEGINNER_DEVELOPMENT.md#setting-up-locally)

</td>
<td width="33%" valign="top">

### Where does a request enter the code?
`Server.cs` listens and inserts requests into the priority queue. `ProxyWorker.cs` dequeues and proxies. `IteratorFactory.cs` creates the load-balanced host iterator. `CircuitBreaker.cs` gates each attempt.

[→ Main code flow](../design.md#main-code-flow)

</td>
<td width="33%" valign="top">

### What is the request flow in code?
`Server.cs` → Priority Queue → `ProxyWorker.cs` → `IteratorFactory.cs` (path filter → LB order) → `CircuitBreaker.cs` (gate) → backend HTTP call → telemetry event.

[→ Request flow diagram](../design.md#request-flow)

</td>
</tr>
<tr>
<td width="33%" valign="top">

### Where do I add a new config variable?
Add the property to the relevant config class in `src/SimpleL7Proxy/Config/`. Follow the Warm/Cold/Hidden pattern. Register it in `ConfigFactory.cs` and document it in `ENVIRONMENT_VARIABLES.md`.

> **⚠️ GAP:** No step-by-step "where to add X" guide exists — the above is inferred from patterns, not documented. → [Content gap details](#content-gaps-to-fill)

</td>
<td width="33%" valign="top">

### What coding conventions apply?
PascalCase for classes/methods/properties, camelCase for locals, `_` prefix for private fields, K&R braces, 4-space indent. XML comments on public methods. See `.github/copilot-instructions.md` for the full standard.

[→ Coding standard](../../.github/copilot-instructions.md)

</td>
<td width="33%" valign="top">

### How do I test without real Azure resources?
Use the LLM Simulator (`test/LLMSimulator`) as a local backend. It returns OpenAI-format responses, simulates `429` throttling, and configurable latency — no Azure subscription needed for most dev scenarios.

[→ LLM Simulator](../DUMMY_BACKEND.md)

</td>
</tr>
</table>

---

## Reader Profile

| | |
|---|---|
| **Who** | Developers building on or extending the proxy; open-source contributors; engineers debugging internals |
| **Why they come here** | They need to run the proxy from source, understand how it works internally, add a feature, or fix a bug |
| **When they read this** | First time contributing; debugging an issue that requires reading source code; extending the proxy for a custom scenario |

---

## Questions this section MUST answer

### Getting set up
- [ ] What tools do I need installed to build and run the proxy from source? (.NET version, IDE, etc.)
- [ ] What is the exact command to build the project?
- [ ] What is the exact command to run the proxy locally?
- [ ] What is the exact command to run all tests?
- [ ] How do I point the local proxy at the LLM simulator for development without real Azure resources?

### Understanding the code
- [ ] What are the main projects/assemblies and what does each one do?
- [ ] What is the entry point and how does startup work?
- [ ] What is the request flow through the code? (which class handles what)
  - Where does a request arrive?
  - Where does it get queued?
  - Where does a worker pick it up?
  - Where does backend selection happen?
  - Where does the circuit breaker gate the request?
  - Where is the response written back?
- [ ] What are the key interfaces and why do they exist? (`IEventClient`, `IBackendSelector`, etc.)
- [ ] What is the object lifecycle? When are workers created, destroyed, and how are resources disposed?

### Making changes
- [ ] What is the coding style guide for this project? (naming, bracing, spacing, comments)
- [ ] Where do I add a new configuration variable?
- [ ] Where do I add a new telemetry event?
- [ ] Where do I add a new validation rule?
- [ ] How do I add a new backend selection strategy?
- [ ] Where do I add tests and what testing pattern does the project use?

### Contributing
- [ ] What is the contribution process? (issue first, then PR?)
- [ ] What does a good PR look like for this project?
- [ ] Are there any automated checks that run on PRs and what do they check?
- [ ] How do I run the linter / style checker locally before pushing?

### Advanced development
- [ ] How do I run performance profiling or load testing locally?
- [ ] How do I test async mode locally without Azure Service Bus and Blob Storage?
- [ ] How do I add support for a new stream processor type?
- [ ] How does hot-reload work in the code — where are settings re-read?

---

## What the reader can do AFTER reading this

- [ ] Proxy builds and runs from source with `dotnet run`
- [ ] Tests pass with `dotnet test`
- [ ] Can navigate to any part of the request flow in the source code
- [ ] Understands the coding conventions well enough to write a new feature that passes review
- [ ] Can submit a PR with confidence about format and process

---

## Existing documents that cover this area

| Document | What it covers | Gap? |
|----------|----------------|------|
| [BEGINNER_DEVELOPMENT.md](../BEGINNER_DEVELOPMENT.md) | Local setup, essential settings, first run | Entry point — verify build + test commands are present |
| [ADVANCED_DEVELOPMENT.md](../ADVANCED_DEVELOPMENT.md) | Performance tuning, advanced features | Covers config but may lack code contribution guidance |
| [design.md](../design.md) | Code structure, request flow, class responsibilities | Primary code-navigation document — verify it covers all key classes |
| [docs/code/DISPOSAL_ARCHITECTURE.md](../code/DISPOSAL_ARCHITECTURE.md) | Object lifecycle and disposal patterns | Useful for contributors modifying worker/resource code |
| [src/SimpleL7Proxy/Proxy/ObjectLifecycle.md](../../src/SimpleL7Proxy/Proxy/ObjectLifecycle.md) | Worker/object lifecycle | Near the source — verify it stays in sync with the code |
| [DUMMY_BACKEND.md](../DUMMY_BACKEND.md) | LLM simulator setup | Required for local dev without Azure — link prominently |

---

## Content gaps to fill

- [ ] A "3-command quickstart" block at the very top: build → run → test (exact commands, expected output)
- [ ] A class-responsibility table: `Server.cs` → listens; `ProxyWorker.cs` → processes; `BackendSelector.cs` → picks host; etc.
- [ ] An annotated request flow showing which class handles which step (same pipeline as architecture but mapped to filenames)
- [ ] A "where to add X" guide: new config var, new header, new validation rule, new event — with exact file paths
- [ ] Explicit coding standard section (or link to `.editorconfig` / `copilot-instructions.md`)
- [ ] A contributor checklist: what to do before submitting a PR (tests pass, no secrets, lint clean, PR template filled)
