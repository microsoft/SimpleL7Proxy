# Building On and Contributing to SimpleL7Proxy

Whether you're adding a feature, fixing a bug, or tracing a request through the code to understand what's happening — here's the map of the codebase, the conventions that apply, and how to get a contribution merged.

---

## Quick Answers

<table>
<tr>
<td width="33%" valign="top">

### How do I build and run from source?
Install .NET 10 SDK. Set `Port` and `Host1`, then `cd src/SimpleL7Proxy && dotnet run`. For VS Code, add a `.vscode/launch.json` with the env vars and press F5.

[→ How do I build and run from source?](#how-do-i-build-and-run-from-source-1)

</td>
<td width="33%" valign="top">

### Where does a request enter the code?
`Server.cs` listens and inserts requests into the priority queue. `ProxyWorker.cs` dequeues and proxies. `IteratorFactory.cs` creates the load-balanced host iterator. `CircuitBreaker.cs` gates each attempt.

[→ Where does a request enter the code?](#where-does-a-request-enter-the-code-1)

</td>
<td width="33%" valign="top">

### What is the request flow in code?
`Server.cs` → Priority Queue → `ProxyWorker.cs` → `IteratorFactory.cs` (path filter → LB order) → `CircuitBreaker.cs` (gate) → backend HTTP call → telemetry event.

[→ What is the request flow in code?](#where-does-a-request-enter-the-code-1)

</td>
</tr>
<tr>
<td width="33%" valign="top">

### Where do I add a new config variable?
Add the property to the relevant config class in `src/SimpleL7Proxy/Config/`. Follow the Warm/Cold/Hidden pattern. Register it in `ConfigFactory.cs` and document it in `ENVIRONMENT_VARIABLES.md`.

[→ Where do I add a new config variable?](#where-do-i-add-a-new-config-variable-1)

</td>
<td width="33%" valign="top">

### What coding conventions apply?
PascalCase for classes/methods/properties, camelCase for locals, `_` prefix for private fields, K&R braces, 4-space indent. XML comments on public methods. See `.github/copilot-instructions.md` for the full standard.

[→ What coding conventions apply?](#where-do-i-add-a-new-config-variable-1)

</td>
<td width="33%" valign="top">

### How do I test without real Azure resources?
Use the LLM Simulator (`test/LLMSimulator`) as a local backend. It returns OpenAI-format responses, simulates `429` throttling, and configurable latency — no Azure subscription needed for most dev scenarios.

[→ How do I test without real Azure resources?](#how-do-i-build-and-run-from-source-1)

</td>
</tr>
</table>

---

## Full Answers

### How do I build and run from source?

#### What tools do I need installed to build and run the proxy from source? (.NET version, IDE, etc.)

SimpleL7Proxy requires .NET 10 and Git at minimum. Docker and an editor such as VS Code are optional but useful for container workflows and debugging.

#### What is the exact command to build the project?

SimpleL7Proxy builds with a single command from the repository root:

```bash
dotnet build SimpleL7Proxy.sln
```

#### What is the exact command to run the proxy locally?

SimpleL7Proxy starts with two environment variables and one `dotnet run` command:

```bash
export Port=8000
export Host1="host=http://localhost:9000;probe=/health"
cd src/SimpleL7Proxy && dotnet run
```

#### What is the exact command to run all tests?

SimpleL7Proxy's tests run from the repository root:

```bash
dotnet test SimpleL7Proxy.sln
```

#### How do I point the local proxy at the LLM simulator for development without real Azure resources?

SimpleL7Proxy works with the included null server or LLM simulator. Start the mock backend and set `Host1` to point at it:

```bash
export Host1="host=http://localhost:3000"
```

See [→ LLM Simulator](../DUMMY_BACKEND.md) for simulator setup.

---

### Where does a request enter the code?

#### What are the main projects/assemblies and what does each one do?

SimpleL7Proxy is organized into these projects:

| Project | Role |
|---------|------|
| `src/SimpleL7Proxy` | Runtime — listener, workers, routing, circuit breaker |
| `src/Shared` | Shared utilities |
| `src/Shared-parser` | Stream and token parsing for AI response bodies |
| `TestClient` | Manual test client |
| Test folders | MSTest suites and helpers |

#### What is the entry point and how does startup work?

SimpleL7Proxy starts at `Program.cs`. On startup it loads configuration (environment variables and, if configured, Azure App Configuration), builds the dependency injection container, registers backend hosts, workers, the health poller, and telemetry sinks, then runs the .NET host until shutdown is signaled.

#### What is the request flow through the code? (which class handles what)

SimpleL7Proxy routes each request through a fixed pipeline:

```
Server.cs → PriorityQueue → ProxyWorker.cs → IteratorFactory.cs → CircuitBreaker.cs → backend → telemetry
```

- **Where does a request arrive?** `Server.cs` — the HTTP listener
- **Where does it get queued?** `Server.cs` inserts accepted work into the in-memory `PriorityQueue`
- **Where does a worker pick it up?** `ProxyWorker.cs` dequeues inside its worker loop
- **Where does backend selection happen?** `IteratorFactory.cs` and iterator classes build and order the eligible backend list
- **Where does the circuit breaker gate the request?** `CircuitBreaker.cs` is checked before each host attempt
- **Where is the response written back?** `ProxyWorker.cs` writes final headers and body to the HTTP response stream

#### What are the key interfaces and why do they exist? (`IEventClient`, `IBackendSelector`, etc.)

SimpleL7Proxy uses interfaces to allow behavior to be swapped without changing worker code. `IEventClient` enables multiple telemetry sinks (App Insights, Event Hub, local file) to each receive the same event via `CompositeEventClient` fan-out. `IBackendSelector` abstracts how the backend candidate list is built. The stream-processor interface allows different response parsers (e.g., OpenAI SSE token extraction) per host. Adding a new telemetry sink means implementing `IEventClient + IHostedService` and registering it — no changes to `ProxyWorker.cs`.

#### What is the object lifecycle? When are workers created, destroyed, and how are resources disposed?

SimpleL7Proxy creates workers and hosted services at startup and runs them until the .NET host signals shutdown (Ctrl+C, SIGTERM, or container stop). Per-request objects such as `RequestData` and `ProxyData` are scoped to a single request — created when a worker picks up the request and released when it completes or fails, using C#'s `IDisposable` pattern.

---

### Where do I add a new config variable?

#### What is the coding style guide for this project? (naming, bracing, spacing, comments)

SimpleL7Proxy follows: PascalCase for public members, camelCase for locals, `_`-prefixed private fields, K&R braces, 4-space indentation, and XML comments on public APIs. See [→ Coding standard](../../.github/copilot-instructions.md).

#### Where do I add a new configuration variable?

SimpleL7Proxy configuration lives in `src/SimpleL7Proxy/Config/`. Add the property to the relevant config class there, wire it through `ConfigFactory.cs`, and document it in `ENVIRONMENT_VARIABLES.md` and the App Configuration docs.

#### Where do I add a new telemetry event?

SimpleL7Proxy builds event payloads around `EventDataBuilder`, `ProxyEvent`, and the sink-specific event client flow. Add new event fields there and ensure all registered `IEventClient` implementations handle them.

#### Where do I add a new validation rule?

SimpleL7Proxy validates requests before queueing. Add the rule in the request validation path, cover it with a focused test, and document the expected response code and body.

#### How do I add a new backend selection strategy?

SimpleL7Proxy selects backends via `IteratorFactory`. Add a new iterator implementation and register it through `IteratorFactory` so the strategy can be selected by configuration.

#### Where do I add tests and what testing pattern does the project use?

SimpleL7Proxy uses MSTest. Add tests to the existing test projects under `SimpleL7Proxy.Test` or `test/ProxyWorkerTests`, keeping each test focused on one behavior or component.

---

### Contributing

#### What is the contribution process? (issue first, then PR?)

SimpleL7Proxy asks contributors to open an issue first for significant changes, then submit a focused pull request.

#### What does a good PR look like for this project?

SimpleL7Proxy PRs should be small, clearly scoped, explain the change, update docs when needed, and include the exact validation you ran.

#### Are there any automated checks that run on PRs and what do they check?

SimpleL7Proxy has no repo-hosted GitHub workflow checks in `.github/workflows`, so contributors should run the local validation steps themselves.

#### How do I run the linter / style checker locally before pushing?

There is no separate lint step. Run `dotnet build` and `dotnet test`, and keep your changes aligned with the style guide. The build will catch most structural problems.

---

### Advanced development

#### How do I run performance profiling or load testing locally?

SimpleL7Proxy can be load-tested locally using the mock backend. Raise `Workers` and `MaxQueueLength` as needed, drive traffic with `curl` loops, and watch `eventslog.json` or telemetry while the proxy runs.

#### How do I test async mode locally without Azure Service Bus and Blob Storage?

SimpleL7Proxy's sync-to-async decision logic can be tested locally, but a full end-to-end async completion still requires Blob Storage and Service Bus configuration.

#### How do I add support for a new stream processor type?

SimpleL7Proxy's stream processors are pluggable. Add the implementation under the stream processor code, then reference it from the backend host connection string via the `processor=` key.

#### How does hot-reload work in the code — where are settings re-read?

SimpleL7Proxy's `AppConfigService` polls App Configuration for `Warm:Sentinel`. When it changes, the runtime reloads Warm settings without restarting the process. Cold settings are read once at startup and not re-read until the next container start.

---

## You Should Now Be Able To

- [ ] Proxy builds and runs from source with `dotnet run`
- [ ] Tests pass with `dotnet test`
- [ ] Can navigate to any part of the request flow in the source code
- [ ] Understands the coding conventions well enough to write a new feature that passes review
- [ ] Can submit a PR with confidence about format and process

---

## Related Documents

| Document | What it covers |
|----------|----------------|
| [Beginner Development](../BEGINNER_DEVELOPMENT.md) | Local setup, essential settings, and first run |
| [Advanced Development](../ADVANCED_DEVELOPMENT.md) | Performance tuning and advanced features |
| [Design](../design.md) | Code structure, request flow, and class responsibilities |
| [Disposal Architecture](../code/DISPOSAL_ARCHITECTURE.md) | Object lifecycle and disposal patterns |
| [Proxy Object Lifecycle](../../src/SimpleL7Proxy/Proxy/ObjectLifecycle.md) | Worker and request object lifecycles near the source |
| [Dummy Backend](../DUMMY_BACKEND.md) | LLM simulator setup for local development |

---
