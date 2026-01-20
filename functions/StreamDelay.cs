using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Company.Function
{
    public class StreamDelay
    {
        private readonly ILogger<StreamDelay> _logger;
        private static readonly Random rng = new Random();
        private static readonly double mean = 1000.0;
        private static readonly double stdDev = 200.0;

        public StreamDelay(ILogger<StreamDelay> logger)
        {
            _logger = logger;
        }

        [Function("streamdelay")]
        public async Task Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequest req)
        {
            // Generate a delay using a normal distribution with mean 1000 ms and standard deviation 200 ms
            double u1 = 1.0 - rng.NextDouble(); // uniform(0,1] random doubles
            double u2 = 1.0 - rng.NextDouble();
            double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2); // random normal(0,1)
            double delay = mean + stdDev * randStdNormal; // random normal(mean,stdDev)
            delay = Math.Max(10, delay); // Ensure the delay is at least 10 ms
            await Task.Delay((int)delay);

            var story = GetCopilotNovelStory();
            var lines = story.Split('\n');

            req.HttpContext.Response.ContentType = "text/event-stream";
            req.HttpContext.Response.Headers.Add("Cache-Control", "no-cache");
            req.HttpContext.Response.Headers.Add("Connection", "keep-alive");
            
            string tokenProcessor = Environment.GetEnvironmentVariable("TOKENPROCESSOR");
            if (string.IsNullOrEmpty(tokenProcessor))
            {
                tokenProcessor = "OPENAI";
            }
            req.HttpContext.Response.Headers.Add("TOKENPROCESSOR", tokenProcessor);

            int count = 0;
            foreach (var line in lines)
            {
                await req.HttpContext.Response.WriteAsync($"data: {line}\n\n");
                await req.HttpContext.Response.Body.FlushAsync();
                if (++count < 10)
                {
                    await Task.Delay(200); // slight delay to simulate streaming                
                }
                else if ( count < 100 ) {
                    await Task.Delay(50); // slight delay to simulate streaming                
                }
                else {
                    await Task.Delay(10); // slight delay to simulate streaming                
                }
            }

            _logger.LogInformation("Completed api/StreamDelay response.");
        }

        private static string GetCopilotNovelStory()
        {
            return Novel.content;
        }
    }
}
