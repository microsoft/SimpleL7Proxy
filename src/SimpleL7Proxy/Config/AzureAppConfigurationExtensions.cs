using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#if AZURE_APPCONFIG_FULL
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
#endif

namespace SimpleL7Proxy.Config;

public class AppConfigurationSnapshot
{
    private readonly object _lock = new();
    private IReadOnlyDictionary<string, string> _snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public void Replace(IDictionary<string, string> values)
    {
        lock (_lock)
        {
            _snapshot = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyDictionary<string, string> GetSnapshot()
    {
        lock (_lock)
        {
            return _snapshot;
        }
    }
}

/// <summary>
/// Azure App Configuration integration for hot-reloading [Warm] settings.
/// 
/// Core behaviour (always compiled):
///   - AzureAppConfigurationRefreshService: a BackgroundService that polls
///     the App Configuration sentinel key every N seconds (configurable via
///     AZURE_APPCONFIG_REFRESH_SECONDS, default 30) and applies changes to
///     BackendOptions.
///
/// Extended wiring helpers and ASP.NET-style middleware are available when
/// the compile-time constant AZURE_APPCONFIG_FULL is defined.
///   csproj:  &lt;DefineConstants&gt;$(DefineConstants);AZURE_APPCONFIG_FULL&lt;/DefineConstants&gt;
/// </summary>

// ──────────────────────────────────────────────────────────────────────
// Core: Background polling service (always compiled)
// ──────────────────────────────────────────────────────────────────────

/// <summary>
/// Background service that periodically triggers configuration refresh from Azure App Configuration.
/// Refresh interval is controlled by the AZURE_APPCONFIG_REFRESH_SECONDS environment variable (default: 30).
/// </summary>
public class AzureAppConfigurationRefreshService : BackgroundService
{
    private readonly IConfigurationRefresher _refresher;
    private readonly IConfiguration _configuration;
    private readonly AppConfigurationSnapshot _appConfigurationSnapshot;
    private readonly IOptions<BackendOptions> _backendOptions;
    private readonly ILogger<AzureAppConfigurationRefreshService> _logger;
    private readonly TimeSpan _refreshInterval;
    private readonly SemaphoreSlim _initialRefreshGate = new(1, 1);
    private volatile bool _initialRefreshCompleted;
    private readonly IReadOnlyList<ConfigOptionDescriptor> _warmDescriptors;
    private string? _lastSentinel;

    public AzureAppConfigurationRefreshService(
        IConfigurationRefresher refresher,
        IConfiguration configuration,
        AppConfigurationSnapshot appConfigurationSnapshot,
        IOptions<BackendOptions> backendOptions,
        ILogger<AzureAppConfigurationRefreshService> logger)
    {
        _refresher = refresher;
        _configuration = configuration;
        _appConfigurationSnapshot = appConfigurationSnapshot;
        _backendOptions = backendOptions;
        _logger = logger;
        _warmDescriptors = ConfigOptions.GetWarmDescriptors();

        var intervalSeconds = int.TryParse(
            Environment.GetEnvironmentVariable("AZURE_APPCONFIG_REFRESH_SECONDS"),
            out var interval) ? interval : 30;
        _refreshInterval = TimeSpan.FromSeconds(intervalSeconds);

        _logger.LogInformation("[CONFIG] Discovered {Count} warm-decorated BackendOptions properties", _warmDescriptors.Count);
    }

    /// <summary>
    /// Performs the initial configuration download once.
    /// Call this during startup when configuration is required before other initialization.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await EnsureInitialRefreshAsync(cancellationToken);
    }

    public IReadOnlyDictionary<string, string> GetCurrentConfigurationDictionary()
    {
        return _appConfigurationSnapshot.GetSnapshot();
    }

    private void CaptureWarmSettingsDictionary(bool alwaysLog = false)
    {
        // Capture both Warm: and Cold: sections into the snapshot
        var warmKvps = _configuration
            .GetSection("Warm")
            .AsEnumerable(makePathsRelative: false)
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key) && kvp.Value != null);

        var coldKvps = _configuration
            .GetSection("Cold")
            .AsEnumerable(makePathsRelative: false)
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key) && kvp.Value != null);

        var dictionary = warmKvps.Concat(coldKvps)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!, StringComparer.OrdinalIgnoreCase);

        _appConfigurationSnapshot.Replace(dictionary);

        if (alwaysLog)
        {
            _logger.LogInformation("[CONFIG] Configuration snapshot loaded ({Count} keys: Warm + Cold)", dictionary.Count);
        }
    }

    private int ApplyDecoratedWarmOptions(bool alwaysLog = false)
    {
        try
        {
            var changedCount = ConfigOptions.ApplyWarmTo(_backendOptions.Value, _configuration.GetSection("Warm"), _logger);

            if (alwaysLog || changedCount > 0)
            {
                _logger.LogInformation("[CONFIG] ✓ Applied {Count} warm option change(s) to BackendOptions", changedCount);
            }

            return changedCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CONFIG] ✗ Failed to apply warm settings to BackendOptions");
            return 0;
        }
    }

    /// <summary>Reads the current Warm:Sentinel value from the configuration.</summary>
    private string? ReadSentinel() => _configuration["Warm:Sentinel"];

    /// <summary>
    /// Returns true if the sentinel value has changed since the last check.
    /// Updates the stored sentinel on change.
    /// </summary>
    private bool HasSentinelChanged()
    {
        var current = ReadSentinel();
        if (string.Equals(_lastSentinel, current, StringComparison.Ordinal))
            return false;

        _logger.LogInformation("[CONFIG] Sentinel changed: {Old} → {New}", _lastSentinel ?? "(none)", current ?? "(none)");
        _lastSentinel = current;
        return true;
    }

    private async Task EnsureInitialRefreshAsync(CancellationToken cancellationToken)
    {
        if (_initialRefreshCompleted)
        {
            return;
        }

        await _initialRefreshGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialRefreshCompleted)
            {
                return;
            }

            _logger.LogInformation("[CONFIG] Performing initial configuration download...");
            var initialRefresh = await _refresher.TryRefreshAsync(cancellationToken);

            if (initialRefresh)
            {
                _logger.LogInformation("[CONFIG] ✓ Initial configuration downloaded successfully");
            }
            else
            {
                _logger.LogInformation("[CONFIG] Initial configuration is already up-to-date");
            }

            _lastSentinel = ReadSentinel();
            _logger.LogInformation("[CONFIG] Initial sentinel: {Sentinel}", _lastSentinel ?? "(none)");

            CaptureWarmSettingsDictionary(alwaysLog: true);
            ApplyDecoratedWarmOptions(alwaysLog: true);

            _initialRefreshCompleted = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CONFIG] ✗ Initial configuration download failed - will continue with defaults and retry");
        }
        finally
        {
            _initialRefreshGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[CONFIG] Azure App Configuration refresh service started with {Interval}s interval",
            _refreshInterval.TotalSeconds);

        await EnsureInitialRefreshAsync(stoppingToken);

        // ── Periodic polling loop ──
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_refreshInterval, stoppingToken);

            try
            {
                var refreshed = await _refresher.TryRefreshAsync(stoppingToken);

                if (refreshed && HasSentinelChanged())
                {
                    var changedCount = ApplyDecoratedWarmOptions();
                    CaptureWarmSettingsDictionary();

                    if (changedCount > 0)
                    {
                        _logger.LogInformation("[CONFIG] Configuration refresh: {Count} value(s) changed", changedCount);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CONFIG] Configuration refresh failed - will retry");
            }
        }

        _logger.LogInformation("[CONFIG] Azure App Configuration refresh service stopped");
    }
}

// ──────────────────────────────────────────────────────────────────────
// Extended: DI wiring helpers & middleware
// Compile with:  AZURE_APPCONFIG_FULL
// ──────────────────────────────────────────────────────────────────────
#if AZURE_APPCONFIG_FULL

/// <summary>
/// Extension methods for registering Azure App Configuration services in DI.
/// Only compiled when AZURE_APPCONFIG_FULL is defined.
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
                options.Connect(new Uri(endpoint), new DefaultAzureCredential());
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

/// <summary>
/// Middleware to trigger configuration refresh on each HTTP request.
/// Only compiled when AZURE_APPCONFIG_FULL is defined.
/// Note: This project uses the Worker SDK, not ASP.NET Core,
/// so this middleware is provided for reference only.
/// </summary>
public class AzureAppConfigurationRefreshMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfigurationRefresher _refresher;

    public AzureAppConfigurationRefreshMiddleware(RequestDelegate next, IConfigurationRefresher refresher)
    {
        _next = next;
        _refresher = refresher;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        _ = _refresher.TryRefreshAsync();
        await _next(context);
    }
}

// Placeholder types - this app uses Worker SDK, not ASP.NET Core
public class HttpContext { }
public delegate Task RequestDelegate(HttpContext context);

#endif // AZURE_APPCONFIG_FULL
