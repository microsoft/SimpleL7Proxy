using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SimpleL7Proxy.Events;

/// <summary>
/// Extension methods for registering proxy event clients.
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
        AddAppInsightsClient(services, aiConnectionString);

        // EventHubClient checks EventHubConfig in constructor and decides whether to run
        try
        {
            Console.WriteLine("Registering EventHubClient");
            services.AddSingleton<EventHubClient>();
            services.AddSingleton<IEventClient>(svc => svc.GetRequiredService<EventHubClient>());
            services.AddSingleton<IHostedService>(svc => (IHostedService)svc.GetRequiredService<EventHubClient>());
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to create EventHubClient: " + ex.Message);
        }

        // Register the composite if you want to inject it, but do not overwrite IEventClient
        services.AddSingleton<CompositeEventClient>(svc =>
        {
            var clients = svc.GetServices<IEventClient>().ToList();
            return new CompositeEventClient(clients);
        });

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
        AddAppInsightsClient(services, aiConnectionString);

        if (!string.IsNullOrEmpty(filename))
        {
            try
            {
                services.AddSingleton<LogFileEventClient>(svc => new LogFileEventClient(filename));
                services.AddSingleton<IEventClient>(svc => svc.GetRequiredService<LogFileEventClient>());
                services.AddSingleton<IHostedService>(svc => (IHostedService)svc.GetRequiredService<LogFileEventClient>());
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to create LogFileEventClient: " + ex.Message);
            }
        }

        // Register the composite if you want to inject it, but do not overwrite IEventClient
        services.AddSingleton<CompositeEventClient>(svc =>
        {
            var clients = svc.GetServices<IEventClient>().ToList();
            return new CompositeEventClient(clients);
        });

        return services;
    }

    /// <summary>
    /// Helper method to register AppInsights event client
    /// </summary>
    private static void AddAppInsightsClient(IServiceCollection services, string? aiConnectionString)
    {
        if (string.IsNullOrEmpty(aiConnectionString))
            return;

        try
        {
            services.AddSingleton<AppInsightsEventClient>();
            services.AddSingleton<IEventClient, AppInsightsEventClient>();
            services.AddSingleton<IHostedService>(svc => (IHostedService)svc.GetRequiredService<AppInsightsEventClient>());
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to create AppInsightsEventClient: " + ex.Message);
        }
    }
}