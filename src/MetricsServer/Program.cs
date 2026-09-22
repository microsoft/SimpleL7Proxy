using Microsoft.ApplicationInsights.Extensibility;
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
                ConfigureAppInsights(services, options);
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

    /// <summary>
    /// Registers Application Insights when a connection string is configured. Without one the
    /// service runs with console logging only.
    /// </summary>
    private static void ConfigureAppInsights(IServiceCollection services, MetricsOptions options)
    {
        if (string.IsNullOrEmpty(options.AppInsightsConnectionString))
        {
            return;
        }

        services.AddApplicationInsightsTelemetryWorkerService(telemetry =>
        {
            telemetry.ConnectionString = options.AppInsightsConnectionString;
            telemetry.EnableAdaptiveSampling = false;
        });

        services.Configure<TelemetryConfiguration>(config =>
        {
            config.TelemetryInitializers.Add(new MetricsTelemetryInitializer());
        });
    }
}
