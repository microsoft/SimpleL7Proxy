using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SimpleL7Proxy.Events;

/// <summary>
/// Extension methods for registering proxy event clients.
/// Each IEventClient adds itself to CompositeEventClient during its own StartAsync.
/// </summary>
public static class ProxyEventServiceCollectionExtensions
{
    /// <summary>
    /// Registers EventHub and AppInsights event clients and their hosted services.
    /// </summary>
    public static IServiceCollection AddProxyEventClient(
        this IServiceCollection services,
        string? aiConnectionString)
    {
        // CompositeEventClient is the single fan-out point; clients self-register via Add(this)
        services.TryAddCompositeEventClient();

        AddAppInsightsClient(services, aiConnectionString);

        // EventHubClient checks EventHubConfig in StartAsync and decides whether to run
        try
        {
            Console.WriteLine("Registering EventHubClient");
            services.AddSingleton<EventHubClient>();
            services.AddSingleton<IHostedService>(svc => svc.GetRequiredService<EventHubClient>());
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to create EventHubClient: " + ex.Message);
        }

        return services;
    }

    /// <summary>
    /// Registers LogFile event client and its hosted service.
    /// </summary>
    public static IServiceCollection AddProxyEventLogFileClient(
        this IServiceCollection services,
        string? filename,
        string? aiConnectionString)
    {
        // CompositeEventClient is the single fan-out point; clients self-register via Add(this)
        services.TryAddCompositeEventClient();

        AddAppInsightsClient(services, aiConnectionString);

        if (!string.IsNullOrEmpty(filename))
        {
            try
            {
                services.AddSingleton<LogFileEventClient>(svc =>
                    new LogFileEventClient(filename, svc.GetRequiredService<CompositeEventClient>()));
                services.AddSingleton<IHostedService>(svc => (IHostedService)svc.GetRequiredService<LogFileEventClient>());
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to create LogFileEventClient: " + ex.Message);
            }
        }

        return services;
    }

    /// <summary>
    /// Helper method to register AppInsights event client.
    /// </summary>
    private static void AddAppInsightsClient(IServiceCollection services, string? aiConnectionString)
    {
        if (string.IsNullOrEmpty(aiConnectionString))
            return;

        try
        {
            services.AddSingleton<AppInsightsEventClient>();
            services.AddSingleton<IHostedService>(svc => (IHostedService)svc.GetRequiredService<AppInsightsEventClient>());
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to create AppInsightsEventClient: " + ex.Message);
        }
    }

    /// <summary>
    /// Ensures CompositeEventClient is registered exactly once regardless of which Add* method is called.
    /// </summary>
    private static void TryAddCompositeEventClient(this IServiceCollection services)
    {
        // Avoid duplicate registrations when multiple Add* methods are chained
        if (services.Any(sd => sd.ServiceType == typeof(CompositeEventClient)))
            return;

        services.AddSingleton<CompositeEventClient>();
        // Expose the composite as the IEventClient so ProxyEvent.Initialize can resolve it
        services.AddSingleton<IEventClient>(svc => svc.GetRequiredService<CompositeEventClient>());
    }
}