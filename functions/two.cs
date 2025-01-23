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
                    userId = "123456",
                    S7PPriorityKey = "12345",
                    Header1 = "Value1",
                    Header2 = "Value2"
                },
                new
                {
                    userId = "123457",
                    Header1 = "Value-1",
                    Header2 = "Value-2"
                }
            };

            return new JsonResult(response);
        }
    }
}
