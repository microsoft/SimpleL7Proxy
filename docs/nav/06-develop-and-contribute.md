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
  **Answer:** You need .NET 10 and Git at minimum, with Docker and an editor such as VS Code being optional but useful.
- [ ] What is the exact command to build the project?
  **Answer:** Run `dotnet build /home/runner/work/SimpleL7Proxy/SimpleL7Proxy/SimpleL7Proxy.sln` from the repo root.
- [ ] What is the exact command to run the proxy locally?
  **Answer:** Set `Port` and `Host1`, then run `cd /home/runner/work/SimpleL7Proxy/SimpleL7Proxy/src/SimpleL7Proxy && dotnet run`.
- [ ] What is the exact command to run all tests?
  **Answer:** Run `dotnet test /home/runner/work/SimpleL7Proxy/SimpleL7Proxy/SimpleL7Proxy.sln`.
- [ ] How do I point the local proxy at the LLM simulator for development without real Azure resources?
  **Answer:** Start the included mock or simulator backend and point `Host1` at it, for example `Host1=http://localhost:3000` for the Python null server.

### Understanding the code
- [ ] What are the main projects/assemblies and what does each one do?
  **Answer:** `src/SimpleL7Proxy` is the runtime, `src/Shared` holds shared utilities, `src/Shared-parser` holds stream and token parsing logic, `TestClient` is a manual client, and the test folders contain MSTest suites and helpers.
- [ ] What is the entry point and how does startup work?
  **Answer:** `Program.cs` is the entry point, and it loads config, builds DI, registers backends and hosted services, starts the probe server, and then runs the host.
- [ ] What is the request flow through the code? (which class handles what)
  **Answer:** The code path is `Server.cs` → priority queue → `ProxyWorker.cs` → `IteratorFactory.cs` and host iterators → `CircuitBreaker.cs` → backend response handling and telemetry.
  - Where does a request arrive?
    **Answer:** Requests first arrive at the listener in `Server.cs`.
  - Where does it get queued?
    **Answer:** `Server.cs` inserts accepted work into the in-memory `PriorityQueue`.
  - Where does a worker pick it up?
    **Answer:** `ProxyWorker.cs` dequeues the request inside its worker loop and owns the forwarding flow.
  - Where does backend selection happen?
    **Answer:** `IteratorFactory.cs` and the iterator classes build and order the eligible backend list.
  - Where does the circuit breaker gate the request?
    **Answer:** `CircuitBreaker.cs` is checked before each host attempt so open hosts are skipped.
  - Where is the response written back?
    **Answer:** `ProxyWorker.cs` writes the final headers and body back to the HTTP response stream.
- [ ] What are the key interfaces and why do they exist? (`IEventClient`, `IBackendSelector`, etc.)
  **Answer:** The important abstractions are the event sink, backend health, async storage, and stream processor interfaces, which keep telemetry, host state, and async or parsing behavior swappable.
- [ ] What is the object lifecycle? When are workers created, destroyed, and how are resources disposed?
  **Answer:** Workers and hosted services are created during startup and live until coordinated shutdown, while per-request objects such as `RequestData` and `ProxyData` are created for one request and disposed when that request finishes.

### Making changes
- [ ] What is the coding style guide for this project? (naming, bracing, spacing, comments)
  **Answer:** Follow the repo guidance: PascalCase for public members, camelCase locals, `_`-prefixed private fields, K&R braces, 4-space indentation, and XML comments on public APIs.
- [ ] Where do I add a new configuration variable?
  **Answer:** Add it in the config model under `src/SimpleL7Proxy/Config/`, wire it through `ConfigFactory.cs`, and document it in the environment variable and App Configuration docs.
- [ ] Where do I add a new telemetry event?
  **Answer:** Add it where request event payloads are built, usually around `EventDataBuilder`, `ProxyEvent`, and the sink-specific event client flow.
- [ ] Where do I add a new validation rule?
  **Answer:** Add it in the request validation path before queueing, then cover it with a focused test and document the expected response behavior.
- [ ] How do I add a new backend selection strategy?
  **Answer:** Add a new iterator implementation and register it through `IteratorFactory` so the strategy can be selected by configuration.
- [ ] Where do I add tests and what testing pattern does the project use?
  **Answer:** Add tests to the existing MSTest projects under `SimpleL7Proxy.Test` or `test/ProxyWorkerTests`, keeping them focused on one behavior or component at a time.

### Contributing
- [ ] What is the contribution process? (issue first, then PR?)
  **Answer:** The README asks contributors to open an issue first for significant changes and then submit a focused pull request.
- [ ] What does a good PR look like for this project?
  **Answer:** A good PR is small, clearly scoped, explains the change, updates docs when needed, and includes the exact validation you ran.
- [ ] Are there any automated checks that run on PRs and what do they check?
  **Answer:** There are no repo-hosted GitHub workflow checks checked into `.github/workflows`, so the documented expectation is that contributors run the local validation steps themselves.
- [ ] How do I run the linter / style checker locally before pushing?
  **Answer:** The repo does not document a separate linter command today, so the practical local validation path is to run `dotnet build` and `dotnet test` and keep changes aligned with the documented style guide.

### Advanced development
- [ ] How do I run performance profiling or load testing locally?
  **Answer:** Use the local mock backend, raise `Workers` and `MaxQueueLength` as needed, drive traffic with the documented `curl` loops, and watch `eventslog.json` or telemetry while the proxy runs.
- [ ] How do I test async mode locally without Azure Service Bus and Blob Storage?
  **Answer:** You can test the sync-to-async decision logic locally, but a full end-to-end async completion still requires Blob Storage and Service Bus configuration.
- [ ] How do I add support for a new stream processor type?
  **Answer:** Add the implementation under the stream processor code, then reference it from the backend host configuration through the `processor` value.
- [ ] How does hot-reload work in the code — where are settings re-read?
  **Answer:** `AppConfigService` polls App Configuration for `Warm:Sentinel`, and when it changes the runtime reloads Warm settings without restarting the process.

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
