using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Azure.Data.AppConfiguration;
using System.Collections.Frozen;
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
    private readonly DefaultCredential _defaultCredential;
    private readonly TimeSpan _refreshInterval;
    private bool _isInitialized = false;
    private DateTime _lastRefreshTime = DateTime.MinValue;

    // App Config key path → config name (e.g. "Logging:LogConsole" → "LogConsole").
    // Built once from static descriptors; never changes.
    private static readonly FrozenDictionary<string, string> s_keyPathToConfigName =
        ConfigMetadata.Descriptors.ToFrozenDictionary(
            d => d.Attribute.KeyPath, d => d.ConfigName, StringComparer.OrdinalIgnoreCase);

    private string? _lastSentinel;

    // Set from Program.cs after the DI container is built.
    public ConfigChangeNotifier? Notifier { get; set; }
    public IHostHealthCollection? HostCollection { get; set; }

    /// <summary>Only warm-prefixed settings, available after <see cref="Start"/> has been awaited.</summary>
    public Dictionary<string, string>? WarmSettings { get; private set; }

    /// <summary>Only cold-prefixed settings, available after <see cref="Start"/> has been awaited.</summary>
    public Dictionary<string, string>? ColdSettings { get; private set; }

    private ProxyConfig _options = null!;
    public static ProxyConfig DEFAULT_OPTIONS { get; set; } = null!;

    public String Status()
    {
        if (_lastRefreshTime == DateTime.MinValue)
            return "AppConfigService not initialized";
            
        return $"Label: {_labelFilter} Last Refresh: {_lastRefreshTime}, Last Sentinel: {_lastSentinel}";
    }

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

        _logger.LogInformation("[BOOTSTRAP] App Configuration- Warm: {WarmCount}, Cold: {ColdCount} Refresh: {interval} secs", warm.Count, cold.Count, _refreshInterval.TotalSeconds);
        WarmSettings = warm;
        ColdSettings = cold;
        warm.TryGetValue("Sentinel", out _lastSentinel);
        if (_lastSentinel == null)
            _logger.LogInformation("[APP-CONFIG] Sentinel missing");
        _isInitialized = true;

        return (WarmSettings, ColdSettings);
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

        DateTime nextRefreshTime = DateTime.UtcNow.Add(_refreshInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = nextRefreshTime - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, stoppingToken);
            }
            try { 
                await ProcessRefreshAsync(stoppingToken);
                nextRefreshTime = nextRefreshTime.Add(_refreshInterval);
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
        WarmSettings = warm;
        ColdSettings = cold;
        warm.TryGetValue("Sentinel", out _lastSentinel);

        if (result == null || Notifier == null)
            return;

        await ConfigFactory.ApplyRefreshV2(
            _options, DEFAULT_OPTIONS, warm, Notifier, HostCollection, _logger, ct);
    }

    private async Task<string?> ReadSentinelAsync(CancellationToken ct)
    {
        try
        {
            var client = GetConfigurationClient();
            var response = await client.GetConfigurationSettingAsync("Warm:Sentinel", _labelFilter, ct);
            var value = response.Value?.Value;
            response.GetRawResponse().Dispose();
            return value;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404) { return null; }
        catch (Exception) { return _lastSentinel; }
    }

    private ConfigurationClient? _cachedClient;
    private ConfigurationClient GetConfigurationClient()
    {
        if (_cachedClient != null) return _cachedClient;

        try
        {
            // Disable distributed tracing and logging on the client used for periodic polling.
            // Each sentinel check goes through the Azure SDK HTTP pipeline, which creates
            // Activity objects that Application Insights converts into DependencyTelemetry.
            // At short refresh intervals this promotes ~10 KB/cycle into Gen2.
            var options = new ConfigurationClientOptions();
            options.Diagnostics.IsDistributedTracingEnabled = false;
            options.Diagnostics.IsLoggingEnabled = false;

            _cachedClient = !string.IsNullOrEmpty(_endpoint)
                ? new ConfigurationClient(new Uri(_endpoint), _defaultCredential.Credential, options)
                : new ConfigurationClient(_connectionString!, options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CONFIGS] Failed to create ConfigurationClient");
            throw;
        }

        return _cachedClient;
    } 

    /// <summary>
    /// Downloads all settings from Azure App Configuration in a single round-trip,
    /// filtered by label. Each key is expected to have a "Warm:" or "Cold:" prefix;
    /// unrecognised prefixes default to cold. The prefix is stripped and the remaining
    /// key path is resolved to a config name via <see cref="s_keyPathToConfigName"/>
    /// or passed through directly for backend host keys.
    /// Returns null if the download fails.
    /// </summary>
    private (Dictionary<string, string> warm, Dictionary<string, string> cold)? DownloadConfig()
    {
        try
        {
            var client = GetConfigurationClient();

            var warm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var cold = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var selector = new SettingSelector { KeyFilter = "*", LabelFilter = _labelFilter };
            foreach (var setting in client.GetConfigurationSettings(selector))
            {
                var (key, value) = (setting.Key, setting.Value ?? "");

                if (key.Length < 6) continue;

                var keyPath = key[5..];
                Dictionary<string, string>? target =
                    key.StartsWith("Warm:", StringComparison.OrdinalIgnoreCase) ? warm :
                    key.StartsWith("Cold:", StringComparison.OrdinalIgnoreCase) ? cold :
                    cold;

                var resolvedKey = s_keyPathToConfigName.TryGetValue(keyPath, out var envVarName) ? envVarName
                    : ConfigParser.IsBackendHostConfigName(keyPath) ? keyPath
                    : null;

                if (resolvedKey == null)
                {
                    _logger.LogInformation("[CONFIGS] No descriptor for key {Key}, skipping", key);
                    continue;
                }

                target[resolvedKey] = value;
            }
            

            _lastRefreshTime = DateTime.UtcNow;
            return (warm, cold);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CONFIGS] ✗ App Configuration download failed — continuing with env vars only");
            return null;
        }
    }
}
