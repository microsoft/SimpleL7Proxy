using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Company.Function
{
    public class health
    {
        private readonly ILogger<health> _logger;

        public health(ILogger<health> logger)
        {
            _logger = logger;
        }

        [Function("health")]
        public IActionResult Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequest req)
        {
            return new OkObjectResult("OK");
        }
    }
}
