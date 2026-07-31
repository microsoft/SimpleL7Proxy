using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Company.Function;

/// <summary>Provides independently rate-limited simulator endpoints for sustained APIM policy tests.</summary>
public sealed class PolicyStress
{
    private const int MaxEndpointIdLength = 64;
    private const int MaxRunIdLength = 128;

    private readonly PolicyStressState _state;
    private readonly ILogger<PolicyStress> _logger;

    public PolicyStress(PolicyStressState state, ILogger<PolicyStress> logger)
    {
        _state = state;
        _logger = logger;
    }

    /// <summary>Returns a 1,000-token response while the named endpoint remains below 100,000 TPM.</summary>
    [Function("policy_stress_response")]
    public async Task<IActionResult> Respond(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "post",
            Route = "policy-stress/{endpointId}/stress/{runId}/{*suffix}")] HttpRequest request,
        string endpointId,
        string runId)
    {
        if (!IsValidIdentifier(endpointId, MaxEndpointIdLength))
        {
            return BadRequest("endpointId must contain only letters, digits, '.', '_', or '-'");
        }
        if (!IsValidIdentifier(runId, MaxRunIdLength))
        {
            return BadRequest("runId must contain only letters, digits, '.', '_', or '-'");
        }

        var utcNow = DateTime.UtcNow;
        var decision = _state.TryConsume(runId, endpointId, utcNow);
        var response = request.HttpContext.Response;
        SetDiagnosticHeaders(response, runId, endpointId, decision);

        if (!decision.Accepted)
        {
            response.StatusCode = StatusCodes.Status429TooManyRequests;
            response.ContentType = "application/json";
            response.Headers.RetryAfter = Math.Max(
                1,
                (int)Math.Ceiling(decision.RetryAfterMilliseconds / 1000.0)).ToString(CultureInfo.InvariantCulture);
            response.Headers["retry-after-ms"] = decision.RetryAfterMilliseconds.ToString(CultureInfo.InvariantCulture);
            await response.WriteAsync(
                JsonSerializer.Serialize(new
                {
                    error = new
                    {
                        code = "simulated_token_limit",
                        message = $"Endpoint '{endpointId}' exhausted its {decision.TokenLimit} TPM budget."
                    }
                }),
                Encoding.UTF8,
                request.HttpContext.RequestAborted);
            return new EmptyResult();
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/json";
        response.Headers["S7P-ID"] = GetCorrelationId(request, runId, endpointId);
        await response.WriteAsync(
            SampleContent.Get("4o-mini.txt"),
            Encoding.UTF8,
            request.HttpContext.RequestAborted);
        return new EmptyResult();
    }

    /// <summary>Returns current, cumulative, and completed-minute statistics for every endpoint in a run.</summary>
    [Function("policy_stress_stats")]
    public IActionResult Stats(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "get",
            Route = "policy-stress-runs/{runId}")] HttpRequest request,
        string runId)
    {
        if (!IsValidIdentifier(runId, MaxRunIdLength))
        {
            return BadRequest("runId must contain only letters, digits, '.', '_', or '-'");
        }

        request.HttpContext.Response.Headers["X-Sim-Instance"] = Environment.MachineName;
        return new OkObjectResult(_state.GetSnapshot(runId, DateTime.UtcNow));
    }

    /// <summary>Clears all in-memory endpoint windows and totals for a run.</summary>
    [Function("policy_stress_reset")]
    public IActionResult Reset(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "delete",
            Route = "policy-stress-runs/{runId}")] HttpRequest request,
        string runId)
    {
        if (!IsValidIdentifier(runId, MaxRunIdLength))
        {
            return BadRequest("runId must contain only letters, digits, '.', '_', or '-'");
        }

        var removedEndpoints = _state.Reset(runId);
        _logger.LogInformation(
            "Reset policy stress run {RunId}; removed {EndpointCount} endpoint states",
            runId,
            removedEndpoints);
        request.HttpContext.Response.Headers["X-Sim-Instance"] = Environment.MachineName;
        return new OkObjectResult(new { runId, removedEndpoints });
    }

    private static void SetDiagnosticHeaders(
        HttpResponse response,
        string runId,
        string endpointId,
        TokenDecision decision)
    {
        response.Headers["X-Sim-Run"] = runId;
        response.Headers["X-Sim-Endpoint"] = endpointId;
        response.Headers["X-Sim-Instance"] = Environment.MachineName;
        response.Headers["X-Sim-Window-Start"] = decision.WindowStartUtc.ToString("O", CultureInfo.InvariantCulture);
        response.Headers["X-Sim-TPM-Limit"] = decision.TokenLimit.ToString(CultureInfo.InvariantCulture);
        response.Headers["X-Sim-TPM-Used"] = decision.TokensUsed.ToString(CultureInfo.InvariantCulture);
        response.Headers["X-Sim-Response-Tokens"] = decision.ResponseTokens.ToString(CultureInfo.InvariantCulture);
    }

    private static string GetCorrelationId(HttpRequest request, string runId, string endpointId)
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

        return $"stress-{runId}-{endpointId}";
    }

    private static bool IsValidIdentifier(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
        {
            return false;
        }

        return value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    }

    private static BadRequestObjectResult BadRequest(string message) => new(new
    {
        error = "invalid_policy_stress_request",
        message
    });
}