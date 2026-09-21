using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MetricsServer;

/// <summary>
/// Entry point for the Metrics Server standalone service.
/// Runs a Kestrel-based server that ingests rollup data and answers status queries.
/// </summary>
public class Program
{
    public static async Task Main(string[] args)
    {
        var options = MetricsOptions.FromEnvironment();

        var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton(options);
                services.AddSingleton<MetricsStore>();
                services.AddHostedService<MetricsHttpServer>();
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
