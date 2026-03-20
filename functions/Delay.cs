using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Company.Function
{
    public class Delay
    {
        private readonly ILogger<Delay> _logger;
        private static readonly Random rng = new Random();
        private static readonly double mean = 1000.0;
        private static readonly double stdDev = 200.0;

        public Delay(ILogger<Delay> logger)
        {
            _logger = logger;
        }

        [Function("delay")]
        public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequest req)
        {
            string delayParam = req.Query["delay"];
            double effectiveMean = (string.IsNullOrEmpty(delayParam) || !double.TryParse(delayParam, out double requestedDelay))
                ? mean
                : requestedDelay;

            double delay;
            if (effectiveMean == 0)
            {
                delay = 0;
            }
            else
            {
                // Generate a delay using a normal distribution around the effective mean
                double u1 = 1.0 - rng.NextDouble(); // uniform(0,1] random doubles
                double u2 = 1.0 - rng.NextDouble();
                double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2); // random normal(0,1)
                delay = effectiveMean + stdDev * randStdNormal; // random normal(effectiveMean,stdDev)
                delay = Math.Max(10, delay); // Ensure the delay is at least 10 ms
            }

            if (delay > 0)
                await Task.Delay((int)delay);

            var story = Novel.content;

            string tokenProcessor = Environment.GetEnvironmentVariable("TOKENPROCESSOR");
            if (string.IsNullOrEmpty(tokenProcessor))
            {
                tokenProcessor = "OPENAI";
            }
            req.HttpContext.Response.Headers.Add("TOKENPROCESSOR", tokenProcessor);

            _logger.LogInformation("Completed api/Delay response.");

            return new OkObjectResult(story);
        }
    }
}
