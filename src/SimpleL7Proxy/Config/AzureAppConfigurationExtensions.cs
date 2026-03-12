using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.Logging;

using Microsoft.Extensions.DependencyInjection;

namespace SimpleL7Proxy.Config;

// ──────────────────────────────────────────────────────────────────────
// Extended: DI wiring helpers & middleware
// ──────────────────────────────────────────────────────────────────────

/// <summary>
/// Extension methods for registering Azure App Configuration services in DI.
/// </summary>
public static class AzureAppConfigurationExtensions
{
    /// <summary>
    /// Adds Azure App Configuration with automatic refresh for Warm settings only.
    /// If AZURE_APPCONFIG_ENDPOINT or AZURE_APPCONFIG_CONNECTION_STRING are not set,
    /// this method does nothing and all configuration comes from environment variables.
    /// </summary>
    public static IServiceCollection AddAzureAppConfigurationWithWarmRefresh(
        this IServiceCollection services,
        ILogger logger)
    {
        var endpoint = Environment.GetEnvironmentVariable("AZURE_APPCONFIG_ENDPOINT");
        var connectionString = Environment.GetEnvironmentVariable("AZURE_APPCONFIG_CONNECTION_STRING");

        if (string.IsNullOrEmpty(endpoint) && string.IsNullOrEmpty(connectionString))
        {
            logger.LogInformation("[CONFIG] Using environment variables for configuration (Azure App Configuration not configured)");
            return services;
        }

        // Register the SDK's IConfigurationRefresherProvider so we can resolve refreshers at runtime.
        // AddAzureAppConfiguration on IConfigurationBuilder sets up the config provider but does NOT
        // register DI services — this call does.
        services.AddAzureAppConfiguration();

        services.AddSingleton<IConfigurationRefresher>(sp =>
        {
            var refresher = sp.GetRequiredService<IConfigurationRefresherProvider>().Refreshers.FirstOrDefault();
            return refresher ?? throw new InvalidOperationException("No configuration refresher available");
        });

        services.AddSingleton<AppConfigurationSnapshot>();
        services.AddSingleton<ConfigChangeNotifier>();
        services.AddSingleton<AzureAppConfigurationRefreshService>();
        services.AddHostedService(sp => sp.GetRequiredService<AzureAppConfigurationRefreshService>());

        logger.LogInformation("[CONFIG] ✓ Azure App Configuration initialized with Warm settings refresh");

        return services;
    }

    /// <summary>
    /// Configures the configuration builder to use Azure App Configuration.
    /// If AZURE_APPCONFIG_ENDPOINT or AZURE_APPCONFIG_CONNECTION_STRING are not set,
    /// this method does nothing and configuration comes from environment variables only.
    /// Call this in Program.cs before building the host.
    /// </summary>
    public static IConfigurationBuilder AddAzureAppConfigurationWithWarmSupport(
        this IConfigurationBuilder builder,
        DefaultCredential defaultCredential,
        ILogger? logger = null)
    {
        var endpoint = Environment.GetEnvironmentVariable("AZURE_APPCONFIG_ENDPOINT");
        var connectionString = Environment.GetEnvironmentVariable("AZURE_APPCONFIG_CONNECTION_STRING");

        if (string.IsNullOrEmpty(endpoint) && string.IsNullOrEmpty(connectionString))
        {
            return builder;
        }

        var labelFilter = Environment.GetEnvironmentVariable("AZURE_APPCONFIG_LABEL");
        // Treat unset, empty, or the Azure CLI null-label convention "\0" as null label
        if (string.IsNullOrEmpty(labelFilter) || labelFilter == "\\0" || labelFilter == "\0")
        {
            labelFilter = LabelFilter.Null;
        }
        logger?.LogInformation("[CONFIG] App Configuration label filter: {Label}",
            labelFilter == LabelFilter.Null ? "(null / no label)" : labelFilter);
        var refreshIntervalSeconds = int.TryParse(
            Environment.GetEnvironmentVariable("AZURE_APPCONFIG_REFRESH_SECONDS"),
            out var interval) ? interval : 30;

        builder.AddAzureAppConfiguration(options =>
        {
            if (!string.IsNullOrEmpty(endpoint))
            {
                var credential = defaultCredential.Credential;

                options.Connect(new Uri(endpoint), credential);
                logger?.LogInformation("[CONFIG] Connecting to Azure App Configuration via Managed Identity: {Endpoint}", endpoint);
            }
            else
            {
                options.Connect(connectionString);
                logger?.LogInformation("[CONFIG] Connecting to Azure App Configuration via connection string");
            }

            // Disable replica discovery to prevent noisy DNS SRV lookup failures
            // (_origin._tcp.*.azconfig.io) in environments where SRV records are
            // unreachable (WSL, restricted networks, single-region deployments).
            // Set AZURE_APPCONFIG_REPLICA_DISCOVERY=true to re-enable for geo-replicated stores.
            var replicaDiscovery = string.Equals(
                Environment.GetEnvironmentVariable("AZURE_APPCONFIG_REPLICA_DISCOVERY"),
                "true", StringComparison.OrdinalIgnoreCase);
            options.ReplicaDiscoveryEnabled = replicaDiscovery;
            if (!replicaDiscovery)
                logger?.LogInformation("[CONFIG] Replica discovery disabled (set AZURE_APPCONFIG_REPLICA_DISCOVERY=true to enable)");

            // Load Warm settings (hot-reloadable, prefix = Warm:)
            options.Select("Warm:*", labelFilter);
            // Load Cold settings (require restart, prefix = Cold:)
            options.Select("Cold:*", labelFilter);

            options.ConfigureRefresh(refresh =>
            {
                // Sentinel is only on the Warm label — Cold settings aren't
                // hot-reloaded so they don't need refresh triggers.
                refresh.Register("Warm:Sentinel", labelFilter, refreshAll: true)
                       .SetRefreshInterval(TimeSpan.FromSeconds(refreshIntervalSeconds));
            });

            logger?.LogInformation("[CONFIG] ✓ Azure App Configuration configured with {RefreshInterval}s refresh interval (prefixes: Warm:*, Cold:*)",
                refreshIntervalSeconds);
        });

        return builder;
    }
}