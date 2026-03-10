using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimpleL7Proxy.Backend;

namespace SimpleL7Proxy.Config;

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
    private readonly IHostHealthCollection? _hostCollection;
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
        ConfigChangeNotifier notifier,
        IHostHealthCollection? hostCollection = null)
    {
        _refresher = refresher;
        _configuration = configuration;
        _appConfigurationSnapshot = appConfigurationSnapshot;
        _backendOptions = backendOptions;
        _logger = logger;
        _notifier = notifier;
        _hostCollection = hostCollection;

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
        _logger.LogDebug("[CONFIG] Warm BackendOptions tracked for in-place update: {WarmConfigs}",
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

    /// <summary>
    /// Reads the current Warm configuration section and stores a filtered snapshot
    /// containing descriptor-backed warm keys and dynamic host-family keys
    /// (Host*, Probe_path*, IP*). The snapshot is used later to detect changes
    /// between refresh cycles.
    /// </summary>
    /// <param name="alwaysLog">
    /// When <c>true</c>, logs the snapshot size at Information level (used during
    /// initial startup). Otherwise the capture is silent.
    /// </param>
    private Dictionary<string, string> CaptureWarmConfigurationSnapshot(bool alwaysLog = false)
    {
        // Capture Warm section into the snapshot — includes descriptor-backed
        // warm keys plus dynamic host-family keys (Host*, Probe_path*, IP*).
        // Keys arrive as "Warm:<KeyPath>" since makePathsRelative is false.
        var warmKvps = _configuration
            .GetSection("Warm")
            .AsEnumerable(makePathsRelative: false)
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key)
                && kvp.Value != null
                && (_warmSnapshotKeys.Contains(kvp.Key)
                    || ConfigParser.IsBackendHostConfigName(kvp.Key["Warm:".Length..])));

        var dictionary = warmKvps
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!, StringComparer.OrdinalIgnoreCase);

        _appConfigurationSnapshot.Replace(dictionary);

        if (alwaysLog)
        {
            _logger.LogInformation("[CONFIG] Configuration snapshot loaded ({Count} keys: Warm)", dictionary.Count);
        }

        return dictionary;
    }

    private List<ConfigChange> ApplyParsedWarmChanges(Dictionary<string, object?> parsedWarmValues, IReadOnlyList<ConfigChange> changesToApply)
    {
        var applied = new List<ConfigChange>(changesToApply.Count);
        var changedProperties = new List<System.Reflection.PropertyInfo>(changesToApply.Count);
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
            changedProperties.Add(descriptor.Property);
            applied.Add(change);
        }

        ConfigParser.ApplyDerivedSettings(target, [.. changedProperties]);

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
        if (_notifier is null )
           return;

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
            .Where(change => subscribedFields.Contains(change.PropertyName)
                || ConfigParser.IsBackendHostConfigName(change.PropertyName))
            .ToList();
    }

    private async Task ProcessRefreshCycleAsync(CancellationToken stoppingToken)
    {
        // updates _configuration
        var refreshed = await _refresher.TryRefreshAsync(stoppingToken);

        if (!refreshed || !HasSentinelChanged())
        {
            return;
        }

        // Read refreshed Warm configuration into snapshot first, then parse
        // BackendOptions change candidates from the same refreshed view.
        var snapshot = CaptureWarmConfigurationSnapshot();

        _logger.LogInformation("[CONFIG] Sentinel change detected, processing configuration changes...");

        // detect configs that changed
        var (detectedWarmChanges, parsedWarmValues, hostChanges) = ConfigOptions.DetectWarmChanges(_backendOptions.Value, snapshot, _logger);
        var appliedChanges = SetSubscribedConfigs(parsedWarmValues, detectedWarmChanges);

        // Notify subscribers for descriptor-backed (non-host) changes.
        if (appliedChanges.Count > 0)
        {
            var changedProperties = string.Join(", ", appliedChanges.Select(c => c.PropertyName));

            _logger.LogInformation("[CONFIG] Warm config changes detected: {Count} changed config(s) ({Configs})",
                appliedChanges.Count,
                changedProperties);

            _logger.LogDebug("[CONFIG] Updated BackendOptions fields: {Fields}",
                changedProperties);

            await NotifySubscribersAsync(appliedChanges, stoppingToken);
        }

        if (hostChanges.Count > 0)
        {
            // CALL HOST REPARSER
            ConfigBootstrapper.RegisterBackends(_backendOptions.Value, null, hostChanges, _hostCollection);
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
            Console.WriteLine(ex.StackTrace);
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
