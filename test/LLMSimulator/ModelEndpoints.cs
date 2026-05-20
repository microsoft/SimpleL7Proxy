using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Company.Function
{
    /// <summary>
    /// Serves each file under <c>Samples/</c> at a route that resembles the real provider URL for that model.
    /// Streaming endpoints emit <c>text/event-stream</c>; non-streaming return the body verbatim.
    /// Add <c>?stream=true</c> to force SSE on any endpoint, or <c>?delay=&lt;ms&gt;</c> for per-line pacing.
    /// </summary>
    public class ModelEndpoints
    {
        private readonly ILogger<ModelEndpoints> _logger;

        public ModelEndpoints(ILogger<ModelEndpoints> logger) => _logger = logger;

        // ── Azure OpenAI ─────────────────────────────────────────────────────────
        //   POST /openai/deployments/{deployment}/chat/completions?api-version=...

        [Function("openai_gpt4o_mini")]
        public Task Gpt4oMini([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post",
            Route = "openai/deployments/gpt-4o-mini/chat/completions/{*rest}")] HttpRequest req)
            => Serve(req, "4o-mini.txt", defaultStream: true);

        [Function("openai_aoai2")]
        public Task Aoai2([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post",
            Route = "openai/deployments/aoai2/chat/completions/{*rest}")] HttpRequest req)
            => Serve(req, "aoai2.txt", defaultStream: true);

        [Function("openai_gpt5_nano")]
        public Task Gpt5Nano([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post",
            Route = "openai/deployments/gpt-5-nano/chat/completions/{*rest}")] HttpRequest req)
            => Serve(req, "gpt5-nano.txt", defaultStream: true);

        [Function("openai_responses")]
        public Task OpenAIResponses([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post",
            Route = "openai/v1/responses/{*rest}")] HttpRequest req)
            => Serve(req, "gpt5-nano-response.txt", defaultStream: false);

        [Function("openai_embeddings")]
        public Task Embeddings([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post",
            Route = "openai/deployments/{deployment}/embeddings/{*rest}")] HttpRequest req, string deployment)
            => Serve(req, "embeddings.txt", defaultStream: false);

        // ── OpenAI (public) ──────────────────────────────────────────────────────
        //   POST /v1/chat/completions

        [Function("openai_public_chat")]
        public Task OpenAIPublicChat([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post",
            Route = "v1/chat/completions/{*rest}")] HttpRequest req)
            => Serve(req, "openAI.txt", defaultStream: true);

        // ── Anthropic ────────────────────────────────────────────────────────────
        //   POST /v1/messages   { "model": "claude-...", ... }
        //   The model is in the JSON body \u2014 we dispatch from there.

        [Function("anthropic_messages")]
        public async Task AnthropicMessages([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post",
            Route = "anthropic/v1/messages/{*rest}")] HttpRequest req)
        {
            string model = await ReadModelFromBodyAsync(req) ?? "claude-sonnet-4";
            string file = model switch
            {
                var m when m.Contains("haiku",     StringComparison.OrdinalIgnoreCase) => "claude-3.5-haiku.txt",
                var m when m.Contains("sonnet-3",  StringComparison.OrdinalIgnoreCase) ||
                           m.Contains("sonnet-3-5",StringComparison.OrdinalIgnoreCase) ||
                           m.Contains("3-5-sonnet",StringComparison.OrdinalIgnoreCase) => "claude-sonnet-3.5.txt",
                var m when m.Contains("code",      StringComparison.OrdinalIgnoreCase) => "claude-code-cli.txt",
                _                                                                      => "anthropoc-claude-sonnet-4.txt",
            };
            req.HttpContext.Response.Headers["X-Anthropic-Model"] = model;
            await Serve(req, file, defaultStream: true);
        }

        // ── Google Gemini ────────────────────────────────────────────────────────
        //   POST /v1beta/models/{model}:generateContent
        //   POST /v1beta/models/{model}:streamGenerateContent

        [Function("gemini_generate")]
        public Task GeminiGenerate([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post",
            Route = "v1beta/models/{model}:generateContent/{*rest}")] HttpRequest req, string model)
            => Serve(req, GeminiFile(model, streaming: false), defaultStream: false);

        [Function("gemini_stream_generate")]
        public Task GeminiStreamGenerate([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post",
            Route = "v1beta/models/{model}:streamGenerateContent/{*rest}")] HttpRequest req, string model)
            => Serve(req, GeminiFile(model, streaming: true), defaultStream: true);

        // ── Filler / fixture content ─────────────────────────────────────────────

        [Function("samples_lorem")]
        public Task Lorem([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post",
            Route = "samples/lorem/{*rest}")] HttpRequest req)
            => Serve(req, "lorem_ipsum.txt", defaultStream: false);

        [Function("samples_multiline")]
        public Task Multiline([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post",
            Route = "samples/multiline/{*rest}")] HttpRequest req)
            => Serve(req, "multiline.txt", defaultStream: false);

        // ── Helpers ──────────────────────────────────────────────────────────────

        /// <summary>Parses true/false/on/off/1/0 (case-insensitive). Returns null when value is null/blank/unrecognized.</summary>
        private static bool? TryParseBool(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return value.Trim().ToLowerInvariant() switch
            {
                "true" or "on"  or "1" or "yes" => true,
                "false" or "off" or "0" or "no" => false,
                _ => null,
            };
        }

        /// <summary>Pick the right Gemini sample file for the requested model + mode.</summary>
        private static string GeminiFile(string model, bool streaming)
        {
            if (model.Contains("flash-lite", StringComparison.OrdinalIgnoreCase)) return "gemini-2.5-flash-lite.txt";
            if (streaming && model.Contains("pro", StringComparison.OrdinalIgnoreCase)) return "gemini-2.5-pro-stream.txt";
            if (model.Contains("pro", StringComparison.OrdinalIgnoreCase))              return "gemini-2.5-pro.txt";
            return "gemini-2.5.txt";
        }

        /// <summary>Reads <c>model</c> from a JSON body without consuming the stream for downstream code.</summary>
        private static async Task<string?> ReadModelFromBodyAsync(HttpRequest req)
        {
            if (!req.Body.CanSeek) req.EnableBuffering();
            req.Body.Position = 0;
            try
            {
                using var doc = await JsonDocument.ParseAsync(req.Body);
                if (doc.RootElement.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
                    return m.GetString();
            }
            catch { /* not JSON or missing field \u2014 fall through */ }
            finally { if (req.Body.CanSeek) req.Body.Position = 0; }
            return null;
        }

        /// <summary>
        /// Default per-line delay (ms) when <c>?delay=</c> is not supplied, sourced from the
        /// <c>SAMPLE_DELAY_MS_DEFAULT</c> app setting / environment variable. Falls back to 0
        /// (no extra pacing) when unset or invalid.
        /// </summary>
        private static int GetDefaultDelayMs()
        {
            var raw = Environment.GetEnvironmentVariable("SAMPLE_DELAY_MS_DEFAULT");
            return int.TryParse(raw, out var v) && v > 0 ? v : 0;
        }

        /// <summary>Writes the sample file to the response, optionally as SSE with per-line pacing.</summary>
        private async Task Serve(HttpRequest req, string fileName, bool defaultStream)
        {
            // Resolution order (highest wins):
            //   1. ?stream=true|false on the request
            //   2. X-Force-Stream header (true|false)
            //   3. FORCE_STREAM env var (true|false|on|off) \u2014 global kill / force switch
            //   4. route's defaultStream
            bool stream =
                  TryParseBool(req.Query["stream"].FirstOrDefault())            // 1
               ?? TryParseBool(req.Headers["X-Force-Stream"].FirstOrDefault())  // 2
               ?? TryParseBool(Environment.GetEnvironmentVariable("FORCE_STREAM")) // 3
               ?? defaultStream;                                                // 4

            int delayMs = int.TryParse(req.Query["delay"].FirstOrDefault(), out var d) && d > 0 ? d : GetDefaultDelayMs();

            var body = SampleContent.Get(fileName);
            var resp = req.HttpContext.Response;
            resp.Headers["X-Sample-File"] = fileName;

            if (!stream)
            {
                resp.ContentType = fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? "application/json"
                    : "text/plain; charset=utf-8";
                if (delayMs > 0) await Task.Delay(delayMs);
                await resp.WriteAsync(body);
                _logger.LogInformation("Served {File} ({Bytes} bytes)", fileName, body.Length);
                return;
            }

            resp.ContentType = "text/event-stream";
            resp.Headers["Cache-Control"] = "no-cache";
            resp.Headers["Connection"] = "keep-alive";

            int count = 0;
            foreach (var line in body.Split('\n'))
            {
                await resp.WriteAsync(line.StartsWith("data:") ? line + "\n\n" : $"data: {line}\n\n");
                await resp.Body.FlushAsync();
                if (delayMs > 0) await Task.Delay(delayMs);
                else if (++count < 10) await Task.Delay(50);
            }
            _logger.LogInformation("Streamed {File} ({Lines} lines)", fileName, count);
        }
    }
}
