using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HealthProbe;

/// <summary>
/// Entry point for the Health Probe standalone service.
/// Runs a Kestrel-based web server on port 9000 for health checks.
/// </summary>
public class Program
{
    public static async Task Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices((context, services) =>
            {
                
                // Register the ProbeServer as a hosted service
                services.AddHostedService<ProbeServer>();
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .Build();

        await host.RunAsync();
    }
}
