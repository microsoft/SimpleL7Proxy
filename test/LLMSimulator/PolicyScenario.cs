using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Company.Function
{
    /// <summary>Returns deterministic responses for APIM retry-policy integration scenarios.</summary>
    public class PolicyScenario
    {
        private const int MaxDelayMs = 120_000;
        private const int MaxSpecBytes = 8_192;
        private const int MaxHeaderCount = 32;
        private const int MaxHeaderValues = 8;
        private const int MaxHeaderValueLength = 2_048;
        private const int MaxBodyTextLength = 16_384;

        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private static readonly HashSet<string> s_bodyModes = new(StringComparer.OrdinalIgnoreCase)
        {
            "openai",
            "text",
            "json-error",
            "empty",
            "context-length",
            "sse",
            "abort"
        };

        private static readonly HashSet<string> s_disallowedHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Connection",
            "Content-Length",
            "Date",
            "Host",
            "Keep-Alive",
            "Proxy-Authenticate",
            "Proxy-Authorization",
            "S7P-ID",
            "Server",
            "TE",
            "Trailer",
            "Transfer-Encoding",
            "Upgrade"
        };

        private readonly ILogger<PolicyScenario> _logger;

        public PolicyScenario(ILogger<PolicyScenario> logger) => _logger = logger;

        /// <summary>
        /// Selects the specification for backend slot <c>a</c> or <c>b</c>, waits the exact configured
        /// delay, and returns the configured status, headers, and body.
        /// </summary>
        [Function("policy_scenario")]
        public async Task<IActionResult> Run(
            [HttpTrigger(
                AuthorizationLevel.Anonymous,
                "get",
                "post",
                Route = "policy-scenario/{slot}/{caseId}/{specA}/{specB}/{*suffix}")] HttpRequest req,
            string slot,
            string caseId,
            string specA,
            string specB)
        {
            if (!string.Equals(slot, "a", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(slot, "b", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest("slot must be 'a' or 'b'");
            }

            if (string.IsNullOrWhiteSpace(caseId) ||
                caseId.Length > 128 ||
                caseId.IndexOfAny(['\r', '\n', '\0']) >= 0)
            {
                return BadRequest("caseId must contain 1 to 128 characters");
            }

            var encodedSpec = string.Equals(slot, "a", StringComparison.OrdinalIgnoreCase) ? specA : specB;
            if (!TryDecodeSpec(encodedSpec, out var spec, out var decodeError))
            {
                return BadRequest(decodeError);
            }

            if (!TryValidateSpec(spec, out var headers, out var validationError))
            {
                return BadRequest(validationError);
            }

            var response = req.HttpContext.Response;
            response.StatusCode = spec.Status;
            foreach (var header in headers)
            {
                if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    response.ContentType = header.Value[0];
                }
                else
                {
                    response.Headers[header.Key] = new StringValues(header.Value);
                }
            }

            response.Headers["X-Sim-Case"] = caseId;
            response.Headers["X-Sim-Slot"] = slot.ToLowerInvariant();
            response.Headers["X-Sim-Delay-Ms"] = spec.DelayMs.ToString();
            response.Headers["X-Sim-Status"] = spec.Status.ToString();
            response.Headers["X-Sim-Body"] = spec.Body;
            response.Headers["X-Sim-Method"] = req.Method;
            response.Headers["X-Sim-Path"] = req.Path.Value ?? string.Empty;
            response.Headers["X-Sim-Has-Authorization"] = req.Headers.ContainsKey("Authorization") ? "true" : "false";
            response.Headers["X-Sim-Has-Api-Key"] = req.Headers.ContainsKey("api-key") ? "true" : "false";
            response.Headers["S7P-ID"] = GetCorrelationId(req, caseId, slot);

            _logger.LogInformation(
                "Policy scenario {CaseId} slot {Slot}: delay={DelayMs}ms status={Status} body={Body}",
                caseId,
                slot,
                spec.DelayMs,
                spec.Status,
                spec.Body);

            if (spec.DelayMs > 0)
            {
                await Task.Delay(spec.DelayMs, req.HttpContext.RequestAborted);
            }

            if (string.Equals(spec.Body, "abort", StringComparison.OrdinalIgnoreCase))
            {
                req.HttpContext.Abort();
                return new EmptyResult();
            }

            await WriteBodyAsync(response, spec, caseId, slot, req.HttpContext.RequestAborted);
            return new EmptyResult();
        }

        private static string GetCorrelationId(HttpRequest request, string caseId, string slot)
        {
            foreach (var headerName in new[] { "x-S7P-ID", "x-ms-client-request-id" })
            {
                var value = request.Headers[headerName].FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(value) &&
                    value.Length <= 512 &&
                    value.IndexOfAny(['\r', '\n', '\0']) < 0)
                {
                    return value;
                }
            }

            return $"sim-{caseId}-{slot.ToLowerInvariant()}";
        }

        private static bool TryDecodeSpec(
            string encoded,
            out ScenarioSpec spec,
            out string error)
        {
            spec = new ScenarioSpec();
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(encoded))
            {
                error = "scenario specification is empty";
                return false;
            }

            try
            {
                var normalized = encoded.Replace('-', '+').Replace('_', '/');
                normalized += (normalized.Length % 4) switch
                {
                    0 => string.Empty,
                    2 => "==",
                    3 => "=",
                    _ => throw new FormatException("invalid Base64Url length")
                };

                var bytes = Convert.FromBase64String(normalized);
                if (bytes.Length > MaxSpecBytes)
                {
                    error = $"decoded scenario specification exceeds {MaxSpecBytes} bytes";
                    return false;
                }

                spec = JsonSerializer.Deserialize<ScenarioSpec>(bytes, s_jsonOptions) ?? new ScenarioSpec();
                return true;
            }
            catch (Exception exception) when (exception is FormatException or JsonException)
            {
                error = "invalid Base64Url JSON scenario specification: " + exception.Message;
                return false;
            }
        }

        private static bool TryValidateSpec(
            ScenarioSpec spec,
            out Dictionary<string, string[]> headers,
            out string error)
        {
            headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            error = string.Empty;

            if (spec.DelayMs is < 0 or > MaxDelayMs)
            {
                error = $"delayMs must be between 0 and {MaxDelayMs}";
                return false;
            }

            if (spec.Status is < 200 or > 599)
            {
                error = "status must be between 200 and 599";
                return false;
            }

            if (string.IsNullOrWhiteSpace(spec.Body) || !s_bodyModes.Contains(spec.Body))
            {
                error = "body must be one of: " + string.Join(", ", s_bodyModes.OrderBy(value => value));
                return false;
            }

            if (spec.BodyText?.Length > MaxBodyTextLength)
            {
                error = $"bodyText exceeds {MaxBodyTextLength} characters";
                return false;
            }

            if (spec.Headers == null)
            {
                return true;
            }

            if (spec.Headers.Count > MaxHeaderCount)
            {
                error = $"headers contains more than {MaxHeaderCount} entries";
                return false;
            }

            foreach (var header in spec.Headers)
            {
                if (!IsValidHeaderName(header.Key))
                {
                    error = $"invalid or unsupported header name: {header.Key}";
                    return false;
                }

                if (!TryReadHeaderValues(header.Value, out var values, out error))
                {
                    error = $"header {header.Key}: {error}";
                    return false;
                }

                headers[header.Key] = values;
            }

            return true;
        }

        private static bool TryReadHeaderValues(
            JsonElement element,
            out string[] values,
            out string error)
        {
            error = string.Empty;
            if (element.ValueKind == JsonValueKind.String)
            {
                values = [element.GetString() ?? string.Empty];
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                var parsedValues = new List<string>();
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        values = [];
                        error = "all values must be strings";
                        return false;
                    }
                    parsedValues.Add(item.GetString() ?? string.Empty);
                }
                values = parsedValues.ToArray();
            }
            else
            {
                values = [];
                error = "value must be a string or string array";
                return false;
            }

            if (values.Length is < 1 or > MaxHeaderValues)
            {
                error = $"must contain between 1 and {MaxHeaderValues} values";
                return false;
            }

            foreach (var value in values)
            {
                if (value.Length > MaxHeaderValueLength || value.IndexOfAny(['\r', '\n', '\0']) >= 0)
                {
                    error = $"value is unsafe or exceeds {MaxHeaderValueLength} characters";
                    return false;
                }
            }

            return true;
        }

        private static bool IsValidHeaderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) ||
                name.Length > 128 ||
                name.StartsWith("X-Sim-", StringComparison.OrdinalIgnoreCase) ||
                s_disallowedHeaders.Contains(name))
            {
                return false;
            }

            foreach (var character in name)
            {
                if (!char.IsAsciiLetterOrDigit(character) &&
                    character is not '!' and not '#' and not '$' and not '%' and not '&' and not '\'' and
                    not '*' and not '+' and not '-' and not '.' and not '^' and not '_' and not '`' and not '|' and not '~')
                {
                    return false;
                }
            }

            return true;
        }

        private static async Task WriteBodyAsync(
            HttpResponse response,
            ScenarioSpec spec,
            string caseId,
            string slot,
            CancellationToken cancellationToken)
        {
            if (spec.Status is 204 or 304 || string.Equals(spec.Body, "empty", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string content;
            if (string.Equals(spec.Body, "openai", StringComparison.OrdinalIgnoreCase))
            {
                response.ContentType ??= "application/json";
                content = SampleContent.Get("4o-mini.txt");
            }
            else if (string.Equals(spec.Body, "json-error", StringComparison.OrdinalIgnoreCase))
            {
                response.ContentType ??= "application/json";
                content = JsonSerializer.Serialize(new
                {
                    error = new
                    {
                        code = "simulated_error",
                        message = spec.BodyText ?? $"Simulated status {spec.Status} for {caseId}/{slot}"
                    }
                });
            }
            else if (string.Equals(spec.Body, "context-length", StringComparison.OrdinalIgnoreCase))
            {
                response.ContentType ??= "application/json";
                content = JsonSerializer.Serialize(new
                {
                    error = new
                    {
                        code = "context_length_exceeded",
                        message = spec.BodyText ?? "Simulated context length exceeded"
                    }
                });
            }
            else if (string.Equals(spec.Body, "sse", StringComparison.OrdinalIgnoreCase))
            {
                response.ContentType ??= "text/event-stream";
                response.Headers["Cache-Control"] = "no-cache";
                content = SampleContent.Get("openAI.txt");
            }
            else
            {
                response.ContentType ??= "text/plain; charset=utf-8";
                content = spec.BodyText ?? $"Simulated status {spec.Status} for {caseId}/{slot}";
            }

            await response.WriteAsync(content, Encoding.UTF8, cancellationToken);
        }

        private static BadRequestObjectResult BadRequest(string message) => new(new
        {
            error = "invalid_policy_scenario",
            message
        });

        private sealed class ScenarioSpec
        {
            public ScenarioSpec() { }

            public int DelayMs { get; init; }
            public int Status { get; init; } = 200;
            public string Body { get; init; } = "openai";
            public string? BodyText { get; init; }
            public Dictionary<string, JsonElement>? Headers { get; init; }
        }
    }
}
