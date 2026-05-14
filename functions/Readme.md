# LLM Simulator (Azure Function)

A drop-in **LLM simulator** that speaks Azure OpenAI, OpenAI, Anthropic, and Google Gemini at the real provider URL shapes. Every endpoint returns a canned response from [`Samples/`](./Samples/), so any client, SDK, proxy, or gateway works against it unchanged.

Use it for zero-cost, deterministic LLM responses in CI, integration tests, retry / failover demos, APIM and proxy policy validation, load testing, and offline development — no tokens, no rate limits, no real model latency. Built-in `/error/429` (with `Retry-After`), `/error/500`, `/error/302`, and `/delay` endpoints let you inject failures to exercise retry, circuit-breaker, and timeout logic. Streaming and at-once modes are supported on every model route and can be flipped per request, per header, or globally with one env var.

A pre-built `function.zip` is included, so you can drag it into any Azure Function App and be running in under a minute. The source is also here if you'd rather build it yourself with the .NET 9 SDK and Azure Functions Core Tools.

## Run it on Azure Functions (recommended)

The fastest path: drop the pre-built `function.zip` into an existing Function App. If you'd rather build from source, see [Run it locally](#run-it-locally-60-seconds) or [Deploy alternatives](#deploy-alternatives).

1. **Open the Function App** in the Azure Portal → **Deployment Center** → **ZIP Deploy** tab (or go directly to `https://<funcapp>.scm.azurewebsites.net/ZipDeployUI`).
2. **Drag-and-drop `function.zip`** onto the page. The portal extracts, restarts, and the functions are live in ~30 seconds.
3. **Verify**: `curl https://<funcapp>.azurewebsites.net/api/health` → `200 OK`.

> The Function App must already exist (Flex Consumption plan, .NET 9 isolated runtime). The deployed identity needs **Storage Table Data Contributor** and **Storage Blob Data Owner** on the function's storage account.

Then jump to [Try every model](#try-every-model-cut--paste) and set `BASE` to your deployed URL.

<details>
<summary><b>Run it locally (60 seconds)</b> — for devs with the .NET 9 toolchain</summary>

Use this only if you have the .NET 9 toolchain installed. Otherwise the Azure route above is faster.

**Prerequisites:** [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) and [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local).

```bash
cd functions
func start
```

First run prompts for the worker runtime — pick **`1. dotnet (isolated worker model)`**.

Once running, every endpoint is reachable at `http://localhost:7071/api/<route>`.

</details>

## Try every model (cut & paste)

Each scenario sets `BASE` once — change only that line to swap targets. All commands below reuse `$BASE`.

**One-shot validation:** to confirm every endpoint at once, run [`./validate.sh`](./validate.sh). It defaults to the local host and accepts an override:

```bash
./validate.sh                                              # local
./validate.sh https://<funcapp>.azurewebsites.net/api      # deployed
BASE=https://<funcapp>.azurewebsites.net/api ./validate.sh # via env var
```

Pick a target by setting `BASE` — every command below reuses it.

```bash
# Local (func start on port 7071):
BASE="http://localhost:7071/api"

# Deployed Function App (anonymous routes; for keyed routes append &code=<host-key>):
FUNCAPP="<your-funcapp>"        # e.g. nullbackend-001
BASE="https://${FUNCAPP}.azurewebsites.net/api"
```

<details>
<summary><b>At-once (non-streaming)</b> — full body in one shot, <code>Content-Type: text/plain</code></summary>

```bash
# Azure OpenAI — chat completions
curl -s -X POST "$BASE/openai/deployments/gpt-4o-mini/chat/completions?stream=false" \
  -H "content-type: application/json" \
  -d '{"messages":[{"role":"user","content":"hi"}]}'

curl -s -X POST "$BASE/openai/deployments/aoai2/chat/completions?stream=false" \
  -H "content-type: application/json" \
  -d '{"messages":[{"role":"user","content":"hi"}]}'

curl -s -X POST "$BASE/openai/deployments/gpt-5-nano/chat/completions?stream=false" \
  -H "content-type: application/json" \
  -d '{"messages":[{"role":"user","content":"hi"}]}'

# Azure OpenAI — responses & embeddings (non-streaming by design)
curl -s -X POST "$BASE/openai/v1/responses" \
  -H "content-type: application/json" -d '{"input":"hi"}'

curl -s -X POST "$BASE/openai/deployments/text-embedding-ada-002/embeddings" \
  -H "content-type: application/json" -d '{"input":"hi"}'

# OpenAI (public)
curl -s -X POST "$BASE/v1/chat/completions?stream=false" \
  -H "content-type: application/json" \
  -d '{"model":"gpt-4o-mini","messages":[{"role":"user","content":"hi"}]}'

# Anthropic — model picked from JSON body
curl -s -X POST "$BASE/anthropic/v1/messages?stream=false" \
  -H "content-type: application/json" \
  -d '{"model":"claude-3-5-haiku-20241022","messages":[{"role":"user","content":"hi"}]}'

curl -s -X POST "$BASE/anthropic/v1/messages?stream=false" \
  -H "content-type: application/json" \
  -d '{"model":"claude-3-5-sonnet-20241022","messages":[{"role":"user","content":"hi"}]}'

curl -s -X POST "$BASE/anthropic/v1/messages?stream=false" \
  -H "content-type: application/json" \
  -d '{"model":"claude-sonnet-4-20250514","messages":[{"role":"user","content":"hi"}]}'

# Gemini — generateContent (non-streaming)
curl -s -X POST "$BASE/v1beta/models/gemini-2.0-flash:generateContent" \
  -H "content-type: application/json" \
  -d '{"contents":[{"parts":[{"text":"hi"}]}]}'

curl -s -X POST "$BASE/v1beta/models/gemini-1.5-pro:generateContent" \
  -H "content-type: application/json" \
  -d '{"contents":[{"parts":[{"text":"hi"}]}]}'

# Fixtures
curl -s "$BASE/samples/lorem"
curl -s "$BASE/samples/multiline"
```

</details>

<details>
<summary><b>Streaming</b> — Server-Sent Events, <code>Content-Type: text/event-stream</code> (use <code>curl -N</code>)</summary>

> Add `?delay=<ms>` to pace lines (default ~50ms). `?delay=0` flushes as fast as possible.

```bash
# Azure OpenAI — chat completions
curl -N -X POST "$BASE/openai/deployments/gpt-4o-mini/chat/completions?stream=true&delay=20" \
  -H "content-type: application/json" \
  -d '{"messages":[{"role":"user","content":"hi"}],"stream":true}'

curl -N -X POST "$BASE/openai/deployments/aoai2/chat/completions?stream=true&delay=20" \
  -H "content-type: application/json" \
  -d '{"messages":[{"role":"user","content":"hi"}],"stream":true}'

curl -N -X POST "$BASE/openai/deployments/gpt-5-nano/chat/completions?stream=true&delay=20" \
  -H "content-type: application/json" \
  -d '{"messages":[{"role":"user","content":"hi"}],"stream":true}'

# OpenAI (public)
curl -N -X POST "$BASE/v1/chat/completions?stream=true&delay=20" \
  -H "content-type: application/json" \
  -d '{"model":"gpt-4o-mini","messages":[{"role":"user","content":"hi"}],"stream":true}'

# Anthropic — model picked from JSON body
curl -N -X POST "$BASE/anthropic/v1/messages?stream=true&delay=20" \
  -H "content-type: application/json" \
  -d '{"model":"claude-3-5-haiku-20241022","messages":[{"role":"user","content":"hi"}],"stream":true}'

curl -N -X POST "$BASE/anthropic/v1/messages?stream=true&delay=20" \
  -H "content-type: application/json" \
  -d '{"model":"claude-sonnet-4-20250514","messages":[{"role":"user","content":"hi"}],"stream":true}'

# Gemini — streamGenerateContent
curl -N -X POST "$BASE/v1beta/models/gemini-2.0-flash:streamGenerateContent?delay=20" \
  -H "content-type: application/json" \
  -d '{"contents":[{"parts":[{"text":"hi"}]}]}'

curl -N -X POST "$BASE/v1beta/models/gemini-1.5-pro:streamGenerateContent?delay=20" \
  -H "content-type: application/json" \
  -d '{"contents":[{"parts":[{"text":"hi"}]}]}'

# Built-in stream fixture
curl -N "$BASE/streamdelay?delay=50"
```

</details>

<details>
<summary><b>Failover triggers</b> — errors &amp; delay</summary>

```bash
curl -i "$BASE/error/429?retryAfter=5"
curl -i "$BASE/error/500"
curl -i "$BASE/error/302?to=$BASE/openai/deployments/gpt-4o-mini/chat/completions"
curl -i "$BASE/delay?delay=2000"
```

</details>

---

## Deploy alternatives

The [Run it on Azure Functions](#run-it-on-azure-functions-recommended) section at the top covers the portal ZIP deploy — the fastest path. If you need to rebuild or script the deploy, use one of the options below.

<details>
<summary>Rebuilding <code>function.zip</code> (only if you changed the code)</summary>

```bash
cd functions
dotnet publish -c Release -o publish
cd publish && zip -r ../function.zip . && cd ..
```

On Windows PowerShell, replace the last line with:
```powershell
Compress-Archive -Path publish\* -DestinationPath function.zip -Force
```

</details>

<details>
<summary>Scripted deploy via <code>deploy-flex.sh</code></summary>

Edit `RESOURCE_GROUP` and `FUNCTION_APP` at the top of [`deploy-flex.sh`](./deploy-flex.sh), then:

```bash
./deploy-flex.sh
```

</details>

---

## Reference

### Capabilities at a glance

| Area | What you get |
| :--- | :--- |
| Providers | Azure OpenAI (chat / responses / embeddings), public OpenAI `/v1`, Anthropic `/v1/messages` (model from JSON body), Google Gemini `/v1beta/models/{model}:generateContent` and `:streamGenerateContent` |
| Modes | Streaming (SSE) and at-once on every model endpoint |
| Toggles | `?stream=true|false` per request · `X-Force-Stream` header · `FORCE_STREAM` env var (global) |
| Failure injection | `/api/error/429` with `Retry-After`, `/api/error/500`, `/api/error/302` (configurable target), `/api/delay?delay=<ms>` |
| Fixtures | `/api/samples/lorem`, `/api/samples/multiline`, `/api/streamdelay`, `/api/health`, `/api/profile` |
| Deploy | Pre-built `function.zip` for portal ZIP deploy · `deploy-flex.sh` for scripted deploy · `func start` for local |
| Compatible with | SimpleL7Proxy, APIM policies, OpenAI / Anthropic / Google SDKs, LangChain, Semantic Kernel, custom clients, load-test rigs |

### Endpoints

All routes accept `GET` and `POST` with **anonymous** auth. Real clients can hit them with their normal request bodies — the body is ignored, the canned sample is returned.

#### Error responses (failover / retry triggers)

| Route | Status | Notes |
| :--- | :--- | :--- |
| `/api/error/429` | 429 | Sets `Retry-After`, `retry-after-ms`, and `S7PREQUEUE: true`. Override with `?retryAfter=<sec>` (default 10). |
| `/api/error/500` | 500 | Classified as temporary error. |
| `/api/error/302` | 302 | Sets `Location`. Override target with `?to=<url>`. |

#### Azure OpenAI

```
POST /api/openai/deployments/gpt-4o-mini/chat/completions
POST /api/openai/deployments/aoai2/chat/completions
POST /api/openai/deployments/gpt-5-nano/chat/completions
POST /api/openai/deployments/{deployment}/embeddings
POST /api/openai/v1/responses
```

#### OpenAI (public)

```
POST /api/v1/chat/completions
```

#### Anthropic

```
POST /api/anthropic/v1/messages
```

The sample served is chosen from the `"model"` field of the JSON body, matching Anthropic's real contract:

| `model` contains | Sample file |
| :--- | :--- |
| `haiku` | `claude-3.5-haiku.txt` |
| `3-5-sonnet` / `sonnet-3-5` / `sonnet-3` | `claude-sonnet-3.5.txt` |
| `code` | `claude-code-cli.txt` |
| (anything else / missing) | `anthropoc-claude-sonnet-4.txt` |

The selected model is echoed back in the `X-Anthropic-Model` response header.

#### Google Gemini

```
POST /api/v1beta/models/{model}:generateContent
POST /api/v1beta/models/{model}:streamGenerateContent
```

Sample file is chosen from the `{model}` path segment: `flash-lite` → `gemini-2.5-flash-lite.txt`, `pro` → `gemini-2.5-pro.txt` (or `gemini-2.5-pro-stream.txt` on the streaming route), otherwise `gemini-2.5.txt`.

#### Fixtures & misc

```
GET /api/samples/lorem        → lorem_ipsum.txt
GET /api/samples/multiline    → multiline.txt
GET /api/delay?delay=<ms>     → variable response delay (normal distribution, see Delay.cs)
GET /api/streamdelay          → SSE stream with built-in pacing
GET /api/health               → 200 OK
GET /api/profile              → user-profile fixture
```

### Query options (model endpoints)

| Query param | Effect |
| :--- | :--- |
| `?stream=true` / `?stream=false` | Override the route's default streaming mode for this request. |
| `?delay=<ms>` | Per-line delay when streaming; pre-write delay when not. |

### Toggle streaming globally

Resolution order (first match wins):

1. **Per-request:** `?stream=true|false` on the URL.
2. **Per-request:** `X-Force-Stream: true|false` request header.
3. **Function-wide:** `FORCE_STREAM=true|false` app setting / env var — flips every model endpoint at once. Set it in the portal under **Function App → Configuration → Application settings**, or in `local.settings.json` for local runs. Restart the app to apply.
4. The route's built-in default (see endpoint tables above).

Accepted values for any of the above: `true`/`false`, `on`/`off`, `1`/`0`, `yes`/`no` (case-insensitive).

Every model response also includes `X-Sample-File: <filename>` so you can confirm which sample was served.

### Adding new samples

1. Drop a `.txt` file into [`Samples/`](./Samples/) — it's auto-copied to output via `functions.csproj`.
2. Add a `[Function(...)]` method in [`ModelEndpoints.cs`](./ModelEndpoints.cs) calling `Serve(req, "yourfile.txt", defaultStream: …)`.

That's the entire contract. The simulator is designed to be used straight from a client SDK, or extended by building from source when you need new routes.



