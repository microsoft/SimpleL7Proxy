using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Azure.Data.AppConfiguration;
using SimpleL7Proxy.Backend;

namespace SimpleL7Proxy.Config;

/// <summary>
/// Owns the full Azure App Configuration lifecycle:
/// initial download before DI, then periodic sentinel-based refresh
/// running as a BackgroundService inside the host.
/// </summary>
public class AppConfigService : BackgroundService
{
    private Task<(Dictionary<string, string> warm, Dictionary<string, string> cold)?>? _downloadTask;
    private readonly ILogger<AppConfigService> _logger;
    private readonly string? _endpoint;
    private readonly string? _connectionString;
    private readonly string? _labelFilter;
    private ProxyConfig _options;
    private readonly DefaultCredential _defaultCredential;
    private readonly TimeSpan _refreshInterval;
    private bool _isInitialized = false;

    // Warm-keyed snapshot: "Warm:KeyPath" → value, used by DetectWarmChanges.
    private Dictionary<string, string> _warmSnapshot = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastSentinel;

    // Set from Program.cs after the DI container is built.
    public ConfigChangeNotifier? Notifier { get; set; }
    public IHostHealthCollection? HostCollection { get; set; }

    /// <summary>The downloaded settings (merged warm + cold), available after <see cref="Start"/> has been awaited.</summary>
    public Dictionary<string, string>? Settings { get; private set; }

    /// <summary>Only warm-prefixed settings, available after <see cref="Start"/> has been awaited.</summary>
    public Dictionary<string, string>? WarmSettings { get; private set; }

    /// <summary>Only cold-prefixed settings, available after <see cref="Start"/> has been awaited.</summary>
    public Dictionary<string, string>? ColdSettings { get; private set; }

    public static ProxyConfig DEFAULT_OPTIONS { get; set; }


    public AppConfigService(ILogger<AppConfigService> logger, ProxyConfig backendOptions, DefaultCredential defaultCredential)
    {
        _logger = logger;
        _defaultCredential = defaultCredential;
        _endpoint = backendOptions.AppConfigEndpoint;
        _connectionString = backendOptions.AppConfigConnectionString;
        _refreshInterval = TimeSpan.FromSeconds(
            backendOptions.AppConfigRefreshIntervalSeconds > 0
                ? backendOptions.AppConfigRefreshIntervalSeconds
                : 30);

        var labelFilter = backendOptions.AppConfigLabel;
        _labelFilter = string.IsNullOrEmpty(labelFilter) || labelFilter == "\\0" || labelFilter == "\0"
            ? null
            : labelFilter;
    }

    /// <summary>
    /// Kicks off the App Configuration download in the background. Idempotent — safe to
    /// call multiple times; the download only runs once. Use <see cref="GetSettingsAsync"/>
    /// to await completion and retrieve the settings.
    /// </summary>
    public void Start()
    {
        if (_downloadTask != null) return;

        if (string.IsNullOrEmpty(_endpoint) && string.IsNullOrEmpty(_connectionString))
        {
            _logger.LogInformation("[BOOTSTRAP] App Configuration not configured, skipping bootstrap download");
            _downloadTask = Task.FromResult<(Dictionary<string, string> warm, Dictionary<string, string> cold)?>(null);
            return;
        }

        _logger.LogInformation("[BOOTSTRAP] Starting App Configuration bootstrap download...");
        _downloadTask = Task.Run(DownloadConfig);
    }

    /// <summary>
    /// Awaits the background download started by <see cref="Start"/> and returns the settings.
    /// Populates <see cref="Settings"/> on first call.
    /// </summary>
    public async Task<(Dictionary<string, string>?, Dictionary<string, string>?)> GetSettingsAsync()
    {
        if (_downloadTask == null)
        {
            _logger.LogWarning("[BOOTSTRAP] GetSettingsAsync called before Start; returning null");
            return (null, null);
        }

        var result = await _downloadTask.ConfigureAwait(false);

        if (result == null)
        {
            _logger.LogInformation("[BOOTSTRAP] No App Configuration settings downloaded");
            return (null, null);
        }

        var (warm, cold) = result.Value;
        if (warm.Count == 0 && cold.Count == 0)
        {
            _logger.LogInformation("[BOOTSTRAP] No App Configuration settings downloaded");
            return (null, null);
        }

        _logger.LogInformation("[BOOTSTRAP] Retrieved {WarmCount} warm and {ColdCount} cold App Configuration value(s)", warm.Count, cold.Count);
        CommitDownload(warm, cold);
        _isInitialized = true;
        return (WarmSettings, ColdSettings);
    }

    public (Dictionary<string, string>?, Dictionary<string, string>?) WaitForDownload() => (Settings, ColdSettings);

    /// <summary>
    /// Commits a freshly downloaded config: stores Settings, swaps snapshot, extracts sentinel.
    /// </summary>
    private void CommitDownload(Dictionary<string, string> warm, Dictionary<string, string> cold)
    {
        WarmSettings = warm;
        ColdSettings = cold;
        // Merge: cold first, then warm overwrites (warm takes precedence)
        // var merged = new Dictionary<string, string>(cold, StringComparer.OrdinalIgnoreCase);
        // foreach (var kvp in warm) merged[kvp.Key] = kvp.Value;
        // Settings = merged;
        // _snapshot = new Dictionary<string, string>(_warmSnapshot, StringComparer.OrdinalIgnoreCase);
        _lastSentinel = _warmSnapshot.TryGetValue("Warm:Sentinel", out var s) ? s : null;
    }

    /// <summary>
    /// Registers the services needed for periodic hot-refresh and wires up dependencies
    /// once the DI container is built. Only registers when App Configuration was reachable.
    /// </summary>
    public void RegisterServices(IServiceCollection services, ProxyConfig options)
    {
        _options = options;
        if (!_isInitialized) return;

        services.AddHostedService(sp => this);
    }

    // ── BackgroundService: periodic hot-refresh loop ───────────────────────
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(_endpoint) && string.IsNullOrEmpty(_connectionString))
            return; // No App Config — nothing to refresh.

        _logger.LogInformation("[BOOTSTRAP] App Configuration refresh: {Interval} seconds", _refreshInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_refreshInterval, stoppingToken);
            try { 
                await ProcessRefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogWarning(ex, "[BOOTSTRAP] Refresh failed, will retry"); }
        }
    }

    private async Task ProcessRefreshAsync(CancellationToken ct)
    {
        var sentinel = await ReadSentinelAsync(ct);

        if (string.Equals(sentinel, _lastSentinel, StringComparison.Ordinal)) return;

        _logger.LogInformation("[APP-CONFIG] Sentinel changed ({Old} → {New}), re-downloading...",
            _lastSentinel ?? "(none)", sentinel ?? "(none)");

        var result = await Task.Run(DownloadConfig, ct);

        var (warm, cold) = result.Value;
        CommitDownload(warm, cold);

        if (result == null || Notifier == null)
            return;

        await ConfigFactory.ApplyRefresh(
            _options, DEFAULT_OPTIONS, warm, Notifier, HostCollection, _logger, ct);
    }

    private async Task<string?> ReadSentinelAsync(CancellationToken ct)
    {
        try
        {
            var client = GetConfigurationClient();
            var response = await client.GetConfigurationSettingAsync("Warm:Sentinel", _labelFilter, ct);
            return response.Value?.Value;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404) { return null; }
        catch (Exception) { return _lastSentinel; } // assume no change on transient error
    }

    private ConfigurationClient? _cachedClient;
    private ConfigurationClient GetConfigurationClient()
    {
        if (_cachedClient != null) return _cachedClient;

        try
        {
            _cachedClient = !string.IsNullOrEmpty(_endpoint)
                ? new ConfigurationClient(new Uri(_endpoint), _defaultCredential.Credential)
                : new ConfigurationClient(_connectionString!);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CONFIG] Failed to create ConfigurationClient");
            throw;
        }

        return _cachedClient;
    } 

    private (Dictionary<string, string> warm, Dictionary<string, string> cold)? DownloadConfig()
    {
        try
        {
            var client = GetConfigurationClient();

            // Build a lookup from App Config key path → env var name using the descriptors.
            // e.g. "Logging:LogConsole" → "LogConsole", "Async:Timeout" → "AsyncTimeout"
            var keyPathToEnvVar = ConfigMetadata.Descriptors
                .ToDictionary(d => d.Attribute.KeyPath, d => d.ConfigName, StringComparer.OrdinalIgnoreCase);

            var warm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var cold = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var warmSnapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var prefix in new[] { "Warm:", "Cold:" })
            {
                var isWarm = prefix == "Warm:";
                var target = isWarm ? warm : cold;
                var selector = new SettingSelector { KeyFilter = $"{prefix}*", LabelFilter = _labelFilter };
                foreach (var setting in client.GetConfigurationSettings(selector))
                {
                    var keyPath = setting.Key.Substring(prefix.Length);
                    if (string.IsNullOrEmpty(keyPath)) continue;

                    var value = setting.Value ?? "";

                    if (isWarm && keyPath.Equals("Sentinel", StringComparison.OrdinalIgnoreCase))
                    {
                        warmSnapshot[setting.Key] = value;
                        continue;
                    }

                    if (keyPathToEnvVar.TryGetValue(keyPath, out var envVarName))
                    {
                        target[envVarName] = value;
                        if (isWarm) warmSnapshot[setting.Key] = value;
                        _logger.LogDebug("[CONFIG] {Key} → {EnvVar}", setting.Key, envVarName);
                    }
                    else if (ConfigParser.IsBackendHostConfigName(keyPath))
                    {
                        target[keyPath] = value;
                        if (isWarm) warmSnapshot[setting.Key] = value;
                        _logger.LogDebug("[CONFIG] {Key} → {KeyPath}", setting.Key, keyPath);
                    }
                    else
                    {
                        _logger.LogDebug("[CONFIG] No descriptor for key {Key}, skipping", setting.Key);
                    }
                }
            }

            _warmSnapshot = warmSnapshot;

            return (warm, cold);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CONFIG] ✗ App Configuration download failed — continuing with env vars only");
            return null;
        }
    }
}
