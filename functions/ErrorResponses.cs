using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Company.Function
{
    /// <summary>Synthetic error responses for failover / retry POCs (429, 500, 302).</summary>
    public class ErrorResponses
    {
        private readonly ILogger<ErrorResponses> _logger;

        public ErrorResponses(ILogger<ErrorResponses> logger) => _logger = logger;

        /// <summary>429 Too Many Requests with <c>retry-after</c> + <c>retry-after-ms</c> + <c>S7PREQUEUE</c>.</summary>
        [Function("error429")]
        public IActionResult Error429(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "error/429")] HttpRequest req)
        {
            int retryAfterSec = ParseIntQuery(req, "retryAfter", 10);
            req.HttpContext.Response.Headers["Retry-After"] = retryAfterSec.ToString();
            req.HttpContext.Response.Headers["retry-after-ms"] = (retryAfterSec * 1000).ToString();
            req.HttpContext.Response.Headers["S7PREQUEUE"] = "true";
            _logger.LogInformation("Returning 429 with Retry-After={Sec}s", retryAfterSec);
            return new ContentResult { StatusCode = 429, Content = "Too Many Requests", ContentType = "text/plain" };
        }

        /// <summary>500 Internal Server Error \u2014 classified as a temporary error by the policy.</summary>
        [Function("error500")]
        public IActionResult Error500(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "error/500")] HttpRequest req)
        {
            _logger.LogInformation("Returning 500");
            return new ContentResult { StatusCode = 500, Content = "Internal Server Error", ContentType = "text/plain" };
        }

        /// <summary>302 Found with a <c>Location</c> header \u2014 useful for redirect-handling tests.</summary>
        [Function("error302")]
        public IActionResult Error302(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "error/302")] HttpRequest req)
        {
            // ?to=<url> overrides the default target. Defaults to /api/openai/v1/chat/completions on the same host.
            string defaultTarget = $"{req.Scheme}://{req.Host}/api/openai/v1/chat/completions";
            string target = req.Query["to"].FirstOrDefault() ?? defaultTarget;
            req.HttpContext.Response.Headers["Location"] = target;
            _logger.LogInformation("Returning 302 \u2192 {Target}", target);
            return new ContentResult { StatusCode = 302, Content = $"Redirecting to {target}", ContentType = "text/plain" };
        }

        private static int ParseIntQuery(HttpRequest req, string key, int fallback)
            => int.TryParse(req.Query[key].FirstOrDefault(), out var v) && v > 0 ? v : fallback;
    }
}
