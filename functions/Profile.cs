using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Company.Function
{
    public class two
    {
        private readonly ILogger<two> _logger;

        public two(ILogger<two> logger)
        {
            _logger = logger;
        }

        [Function("two")]
        public IActionResult Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequest req)
        {
            var response = new object[]
            {
                new
                {
                    userId = "lowpriority",
                    S7PPriorityKey = "234",
                    Header1 = "low priority value 1",
                    Header2 = "low priority value 2"
                },
                new
                {
                    userId = "highpriority",
                    S7PPriorityKey = "12345",
                    Header1 = "High priority value 1",
                    Header2 = "high priority value 2"
                }
            };

            return new JsonResult(response);
        }
    }
}
