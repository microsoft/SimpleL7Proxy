using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Azure.Data.AppConfiguration;
using Azure.Identity;

#if AZURE_APPCONFIG_FULL
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
/// Bootstraps the Azure App Configuration download early in startup so that
/// values are available as environment variables before LoadBackendOptions runs.
/// Uses <see cref="ConfigurationClient"/> to do a one-shot fetch, then maps
/// each App Config key path to its environment variable name via
/// <see cref="ConfigOptions.Descriptors"/>.
/// Call <see cref="Start"/> at the beginning of Main (before building the host),
/// then <see cref="WaitForDownload"/> at the top of LoadBackendOptions.
/// </summary>
public static class AppConfigBootstrap
{
    private static Task<Dictionary<string, string>?>? _downloadTask;
    private static ILogger? _logger;

    /// <summary>
    /// Kicks off an async download of Warm: and Cold: keys from App Configuration.
    /// Returns immediately; the download runs on a thread-pool thread.
    /// No-op when AZURE_APPCONFIG_ENDPOINT / AZURE_APPCONFIG_CONNECTION_STRING are not set.
    /// </summary>
    public static void Start(ILogger logger)
    {
        _logger = logger;

        var endpoint = Environment.GetEnvironmentVariable("AZURE_APPCONFIG_ENDPOINT");
        var connectionString = Environment.GetEnvironmentVariable("AZURE_APPCONFIG_CONNECTION_STRING");

        if (string.IsNullOrEmpty(endpoint) && string.IsNullOrEmpty(connectionString))
        {
            logger.LogInformation("[BOOTSTRAP] App Configuration not configured, skipping bootstrap download");
            _downloadTask = Task.FromResult<Dictionary<string, string>?>(null);
            return;
        }

        logger.LogInformation("[BOOTSTRAP] Starting App Configuration bootstrap download...");
        _downloadTask = Task.Run(() => DownloadConfig(endpoint, connectionString, logger));
    }

    /// <summary>
    /// Blocks until the bootstrap download completes and returns the dictionary
    /// keyed by environment variable name (ConfigName) with the App Configuration value.
    /// Returns null if not configured or download failed.
    /// Safe to call when Start was never called (no-op).
    /// </summary>
    public static Dictionary<string, string>? WaitForDownload()
    {
        if (_downloadTask == null) return null;

        Dictionary<string, string>? settings;
        try
        {
            settings = _downloadTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[BOOTSTRAP] Failed awaiting App Configuration download");
            return null;
        }

        if (settings == null || settings.Count == 0)
        {
            _logger?.LogInformation("[BOOTSTRAP] No App Configuration settings downloaded");
            return null;
        }

        _logger?.LogInformation("[BOOTSTRAP] Retrieved {Count} App Configuration value(s)", settings.Count);
        return settings;
    }

    private static Dictionary<string, string>? DownloadConfig(string? endpoint, string? connectionString, ILogger logger)
    {
        try
        {
            var labelFilter = Environment.GetEnvironmentVariable("AZURE_APPCONFIG_LABEL");
            if (string.IsNullOrEmpty(labelFilter) || labelFilter == "\\0" || labelFilter == "\0")
                labelFilter = null; // null = no label filter

            ConfigurationClient client = !string.IsNullOrEmpty(endpoint)
                ? new ConfigurationClient(new Uri(endpoint), new DefaultAzureCredential())
                : new ConfigurationClient(connectionString);

            // Build a lookup from App Config key path → env var name using the descriptors.
            // e.g. "Logging:LogConsole" → "LogConsole", "Async:Timeout" → "AsyncTimeout"
            var keyPathToEnvVar = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var descriptor in ConfigOptions.Descriptors)
            {
                keyPathToEnvVar[descriptor.Attribute.KeyPath] = descriptor.ConfigName;
            }

            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var prefix in new[] { "Warm:", "Cold:" })
            {
                var selector = new SettingSelector { KeyFilter = $"{prefix}*", LabelFilter = labelFilter };
                foreach (var setting in client.GetConfigurationSettings(selector))
                {
                    // Strip prefix: "Warm:Logging:LogConsole" → "Logging:LogConsole"
                    var keyPath = setting.Key.Substring(prefix.Length);
                    if (string.IsNullOrEmpty(keyPath)
                        || keyPath.Equals("Sentinel", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Map the key path to the env var name via the descriptors
                    if (keyPathToEnvVar.TryGetValue(keyPath, out var envVarName))
                    {
                        settings[envVarName] = setting.Value ?? "";
                        logger.LogDebug("[BOOTSTRAP] {Key} → {EnvVar}", setting.Key, envVarName);
                    }
                    else
                    {
                        logger.LogDebug("[BOOTSTRAP] No descriptor for key {Key}, skipping", setting.Key);
                    }
                }
            }

            logger.LogInformation("[BOOTSTRAP] ✓ Downloaded {Count} setting(s) from App Configuration", settings.Count);
            return settings;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[BOOTSTRAP] ✗ App Configuration download failed — continuing with env vars only");
            return null;
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
    private readonly ConfigChangeNotifier _notifier;
    private readonly TimeSpan _refreshInterval;
    private readonly SemaphoreSlim _initialRefreshGate = new(1, 1);
    private volatile bool _initialRefreshCompleted;
    private readonly IReadOnlyList<ConfigOptionDescriptor> _warmDescriptors;
    private readonly Dictionary<string, ConfigOptionDescriptor> _warmDescriptorByConfigName;
    private readonly HashSet<string> _warmConfigNames;
    private readonly HashSet<string> _warmSnapshotKeys;
    private string? _lastSentinel;

    public AzureAppConfigurationRefreshService(
        IConfigurationRefresher refresher,
        IConfiguration configuration,
        AppConfigurationSnapshot appConfigurationSnapshot,
        IOptions<BackendOptions> backendOptions,
        ILogger<AzureAppConfigurationRefreshService> logger,
        ConfigChangeNotifier notifier)
    {
        _refresher = refresher;
        _configuration = configuration;
        _appConfigurationSnapshot = appConfigurationSnapshot;
        _backendOptions = backendOptions;
        _logger = logger;
        _notifier = notifier;
        // Warm vs Cold is defined by code attributes (ConfigOption.Mode).
        // A Cold option only becomes Warm after a code change and process restart.
        _warmDescriptors = ConfigOptions.GetWarmDescriptors();
        _warmDescriptorByConfigName = _warmDescriptors
            .ToDictionary(d => d.ConfigName, d => d, StringComparer.OrdinalIgnoreCase);
        _warmConfigNames = _warmDescriptors
            .Select(d => d.ConfigName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _warmSnapshotKeys = _warmDescriptors
            .Select(d => $"Warm:{d.Attribute.KeyPath}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var intervalSeconds = int.TryParse(
            Environment.GetEnvironmentVariable("AZURE_APPCONFIG_REFRESH_SECONDS"),
            out var interval) ? interval : 30;
        _refreshInterval = TimeSpan.FromSeconds(intervalSeconds);

        _logger.LogInformation("[CONFIG] Discovered {Count} warm-decorated BackendOptions properties", _warmDescriptors.Count);
        _logger.LogInformation("[CONFIG] Warm BackendOptions tracked for in-place update: {WarmConfigs}",
            string.Join(", ", _warmConfigNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)));
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

    private void CaptureWarmConfigurationSnapshot(bool alwaysLog = false)
    {
        // Capture Warm section into the snapshot.
        var warmKvps = _configuration
            .GetSection("Warm")
            .AsEnumerable(makePathsRelative: false)
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key)
                && kvp.Value != null
                && _warmSnapshotKeys.Contains(kvp.Key));

        var dictionary = warmKvps
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!, StringComparer.OrdinalIgnoreCase);

        _appConfigurationSnapshot.Replace(dictionary);

        if (alwaysLog)
        {
            _logger.LogInformation("[CONFIG] Configuration snapshot loaded ({Count} keys: Warm)", dictionary.Count);
        }
    }

    private (Dictionary<string, object?> ParsedWarmValues, List<ConfigChange> DetectedWarmChanges) ParseWarmRefreshCandidates(bool alwaysLog = false)
    {
        try
        {
            var currentOptions = _backendOptions.Value;
            var (changes, parsedValues) = ConfigOptions.ParseWarmChanges(currentOptions, _configuration.GetSection("Warm"), _logger);

            if (alwaysLog || changes.Count > 0)
            {
                _logger.LogDebug("[CONFIG] ✓ Parsed {Count} warm option change candidate(s)", changes.Count);
            }

            return (parsedValues, changes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CONFIG] ✗ Failed to parse warm settings");
            return (new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase), []);
        }
    }

    private List<ConfigChange> ApplyParsedWarmChanges(Dictionary<string, object?> parsedWarmValues, IReadOnlyList<ConfigChange> changesToApply)
    {
        var applied = new List<ConfigChange>(changesToApply.Count);
        var target = _backendOptions.Value;

        foreach (var change in changesToApply)
        {
            if (!_warmDescriptorByConfigName.TryGetValue(change.PropertyName, out var descriptor))
            {
                _logger.LogWarning("[CONFIG] Unknown warm config '{ConfigName}' in selected changes, skipping", change.PropertyName);
                continue;
            }

            if (!parsedWarmValues.TryGetValue(change.PropertyName, out var value))
            {
                _logger.LogWarning("[CONFIG] Missing parsed value for warm config '{ConfigName}', skipping", change.PropertyName);
                continue;
            }

            descriptor.Property.SetValue(target, value);
            applied.Add(change);
        }

        return applied;
    }

    private List<ConfigChange> SetSubscribedConfigs(
        Dictionary<string, object?> parsedWarmValues,
        IReadOnlyList<ConfigChange> detectedWarmChanges)
    {
        if (detectedWarmChanges.Count == 0)
        {
            _logger.LogDebug("[CONFIG] Warm config refresh: no BackendOptions changes detected");
            return [];
        }

        var subscribedChanges = SelectSubscribedChanges(detectedWarmChanges);
        if (subscribedChanges.Count == 0)
        {
            _logger.LogInformation("[CONFIG] Warm changes detected ({Count}) but none selected for BackendOptions update",
                detectedWarmChanges.Count);
            return [];
        }

        var appliedChanges = ApplyParsedWarmChanges(parsedWarmValues, subscribedChanges);
        if (appliedChanges.Count == 0)
        {
            _logger.LogDebug("[CONFIG] Warm config refresh: no subscribed warm changes were applied");
        }

        return appliedChanges;
    }

    private async Task NotifySubscribersAsync(List<ConfigChange> changes, CancellationToken cancellationToken)
    {
        await _notifier.NotifyAsync(changes, _backendOptions.Value, cancellationToken);
    }

    private List<ConfigChange> SelectSubscribedChanges(IReadOnlyList<ConfigChange> detectedWarmChanges)
    {
        var (hasWildcardSubscriber, subscribedFields) = _notifier.GetSubscribedFieldSet();

        if (hasWildcardSubscriber)
        {
            return [.. detectedWarmChanges];
        }

        if (subscribedFields.Count == 0)
        {
            return [];
        }

        return detectedWarmChanges
            .Where(change => subscribedFields.Contains(change.PropertyName))
            .ToList();
    }

    private async Task ProcessRefreshCycleAsync(CancellationToken stoppingToken)
    {
        var refreshed = await _refresher.TryRefreshAsync(stoppingToken);

        if (!refreshed || !HasSentinelChanged())
        {
            return;
        }

        // Read refreshed Warm configuration into snapshot first, then parse
        // BackendOptions change candidates from the same refreshed view.
        CaptureWarmConfigurationSnapshot();
        var (parsedWarmValues, detectedWarmChanges) = ParseWarmRefreshCandidates();

        var appliedChanges = SetSubscribedConfigs(parsedWarmValues, detectedWarmChanges);
        if (appliedChanges.Count == 0)
        {
            return;
        }

        var changedProperties = string.Join(", ", appliedChanges.Select(c => c.PropertyName));

        _logger.LogInformation("[CONFIG] Warm config changes detected: {Count} changed config(s) ({Configs})",
            appliedChanges.Count,
            changedProperties);

        _logger.LogDebug("[CONFIG] Updated BackendOptions fields: {Fields}",
            changedProperties);
        await NotifySubscribersAsync(appliedChanges, stoppingToken);
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

        _logger.LogDebug("[CONFIG] Sentinel changed: {Old} → {New}", _lastSentinel ?? "(none)", current ?? "(none)");
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

            // Only capture the snapshot for future change detection.
            // Do NOT re-apply warm options here — the bootstrap path already
            // loaded and parsed all values (including math expressions) via
            // LoadBackendOptions. Re-applying would redundantly parse the same
            // raw strings and fail on expressions like "30 * 60000".
            CaptureWarmConfigurationSnapshot(alwaysLog: true);

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
                await ProcessRefreshCycleAsync(stoppingToken);
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
