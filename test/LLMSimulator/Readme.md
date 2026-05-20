# LLM Simulator (Azure Function)

**Purpose:** Simulate Azure OpenAI, OpenAI, Anthropic, and Gemini endpoints — returning deterministic canned responses and injectable errors — without needing real model access.

> [!IMPORTANT]
> **The rule: your client code points at this simulator unchanged. It mirrors real provider URL shapes, so existing SDKs, proxies, and gateways work without modification.**

## TL;DR (< 5 minutes)

1. Deploy `function.zip` to a Function App via portal ZIP deploy, or run `func start` locally.
2. Set `BASE` to your function host URL.
3. Copy any `curl` from [Use every model](#use-every-model-cut--paste) or [Trigger failures](#trigger-failures) — check `X-Sample-File` and HTTP status to confirm what was served.

**What it returns:** model routes always return `200 OK` with a canned provider-shaped JSON body; `/api/error/429` returns `429` with a real `Retry-After`; `/api/delay` returns after the requested duration.

## Deploy

### Run it on Azure Functions (recommended)

**What matters:** a pre-built `function.zip` is included — you do not need to build from source unless you changed the code.

1. Open the Function App in the [Azure portal](https://portal.azure.com) → **Deployment Center** → **ZIP Deploy** tab (or go directly to `https://<funcapp>.scm.azurewebsites.net/ZipDeployUI`).
2. Drag-and-drop `function.zip`. The portal extracts, restarts, and the functions are live in ~30 seconds.
3. Confirm: `curl https://<funcapp>.azurewebsites.net/api/health` → `200 OK`.

> [!NOTE]
> The Function App must already exist (Flex Consumption plan, .NET 9 isolated runtime). The deployed identity needs **Storage Table Data Contributor** and **Storage Blob Data Owner** on the function's storage account.

Then set `BASE` (see [below](#set-base)) and use any command in [Use every model](#use-every-model-cut--paste).

<details>
<summary><b>Run it locally (60 seconds)</b> — for devs with the .NET 9 toolchain</summary>

**What matters:** requires [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) and [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local).

```bash
cd test/LLMSimulator
func start
```

First run prompts for the worker runtime — pick **`1. dotnet (isolated worker model)`**.

Once running, every endpoint is reachable at `http://localhost:7071/api/<route>`.

</details>

## Set BASE

**What matters:** set `BASE` once — every command in this doc reuses it unchanged, so the same command works against local or deployed.

```bash
# Local (func start on port 7071):
BASE="http://localhost:7071/api"

# Deployed Function App:
FUNCAPP="<your-funcapp>"
BASE="https://${FUNCAPP}.azurewebsites.net/api"
```

**One-shot validation** — hit every endpoint at once:

```bash
./validate.sh                     # local
./validate.sh "$BASE"             # deployed
```

## Use every model (cut & paste)

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

## Trigger failures

**What matters:** these routes return real error shapes so your retry logic, failover policies, and circuit breakers see exactly what they would from a real provider.

```bash
# 429 with Retry-After (10s default)
curl -i "$BASE/error/429"

# 429 with custom Retry-After
curl -i "$BASE/error/429?retryAfter=5"

# 500 temporary error
curl -i "$BASE/error/500"

# 302 redirect (configurable target)
curl -i "$BASE/error/302?to=$BASE/openai/deployments/gpt-4o-mini/chat/completions"

# Variable latency (ms, normal distribution)
curl -i "$BASE/delay?delay=2000"
```

> [!NOTE]
> `/api/error/429` also sets `S7PREQUEUE: true` — SimpleL7Proxy reads this header to requeue the request instead of surfacing the `429` to the client.

---

## Deploy alternatives

**What matters:** the portal ZIP deploy above is the fastest path. Use the options below only if you need to rebuild or script the deploy.

<details>
<summary>Rebuilding <code>function.zip</code> (only if you changed the code)</summary>

```bash
cd test/LLMSimulator
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

### Toggle streaming

**What matters:** the first matching rule wins. Per-request flags always beat global settings.

| Precedence | Mechanism | Values accepted |
| :--- | :--- | :--- |
| 1 (highest) | `?stream=true\|false` query param | `true/false`, `on/off`, `1/0`, `yes/no` |
| 2 | `X-Force-Stream: true\|false` request header | same |
| 3 | `FORCE_STREAM` app setting / env var | same |
| 4 (lowest) | Route built-in default | see endpoint tables above |

> [!NOTE]
> `FORCE_STREAM` flips every model endpoint at once. Set it in the portal under **Function App → Configuration → Application settings**, or in `local.settings.json` for local runs. Restart the app to apply.

Every model response includes `X-Sample-File: <filename>` so you can confirm which sample was served.

### Adding new samples

**What matters:** drop a `.txt` file in `Samples/` and wire it to a function — that is the entire contract.

1. Drop a `.txt` file into [`Samples/`](./Samples/) — it's auto-copied to output via `functions.csproj`.
2. Add a `[Function(...)]` method in [`ModelEndpoints.cs`](./ModelEndpoints.cs) calling `Serve(req, "yourfile.txt", defaultStream: …)`.

### Changing the default `Retry-After`

**What matters:** `ERROR429_RETRY_AFTER_DEFAULT` sets the fallback used by `/api/error/429` when `?retryAfter` is not in the query string. Default is `10` seconds.

**Azure portal:** Function App → **Settings** → **Environment variables** → **App settings** → add or update `ERROR429_RETRY_AFTER_DEFAULT` → **Apply** → **Confirm**.

```bash
# CLI alternative
az functionapp config appsettings set \
  --name <funcapp> --resource-group <rg> \
  --settings ERROR429_RETRY_AFTER_DEFAULT=30
```

> [!WARNING]
> Changing app settings restarts the Function App. Requests in flight will be dropped.

## Troubleshooting

**What matters:** each symptom maps to one concrete cause and one concrete check.

| Symptom | Likely cause | Check |
| :--- | :--- | :--- |
| `func start` fails immediately | .NET 9 SDK or Core Tools v4 not installed | `dotnet --version` (need 9.x); `func --version` (need 4.x) |
| `/api/health` returns `404` after deploy | Wrong runtime or corrupt ZIP | Confirm Flex Consumption plan with .NET 9 isolated runtime; rebuild `function.zip` from source |
| Storage error on startup | Missing role assignments | Add **Storage Table Data Contributor** and **Storage Blob Data Owner** to the function's managed identity on its storage account |
| `/api/error/429` returns wrong `Retry-After` | `ERROR429_RETRY_AFTER_DEFAULT` not applied | Confirm app setting; restart the Function App after changing it |
| Streaming response arrives all at once | `FORCE_STREAM=false` or `?stream=false` overriding route default | Check the toggle precedence table; remove `FORCE_STREAM` or change the query param |
| `X-Sample-File` shows unexpected file | Anthropic `model` field not matching expected pattern | Check the Anthropic model → sample mapping table; confirm `model` value in request body |
| Client SDK fails with schema error | Outdated sample file | Update the `.txt` file in `Samples/` to match the current provider response shape |



