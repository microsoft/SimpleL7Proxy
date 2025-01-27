using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Company.Function
{
    public class one
    {
        private readonly ILogger<one> _logger;
        private static readonly Random rng = new Random();
        private static readonly double mean = 1000.0;
        private static readonly double stdDev = 200.0;

        public one(ILogger<one> logger)
        {
            _logger = logger;
        }

        [Function("one")]
        public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequest req)
        {
            _logger.LogInformation("C# HTTP trigger function processed a request.");

            // Generate a delay using a normal distribution with mean 1000 ms and standard deviation 200 ms
            double u1 = 1.0 - rng.NextDouble(); // uniform(0,1] random doubles
            double u2 = 1.0 - rng.NextDouble();
            double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2); // random normal(0,1)
            double delay = mean + stdDev * randStdNormal; // random normal(mean,stdDev)
            delay = Math.Max(10, delay); // Ensure the delay is at least 10 ms

            await Task.Delay((int)delay);

            return new OkObjectResult("Welcome to Azure Functions!");
        }
    }
}
