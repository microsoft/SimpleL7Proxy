using Microsoft.Extensions.Logging;
using Azure.Data.AppConfiguration;
using Azure.Identity;

namespace SimpleL7Proxy.Config;

/// <summary>
/// Bootstraps the Azure App Configuration download early in startup so that
/// values are available as environment variables before LoadBackendOptions runs.
/// Uses <see cref="ConfigurationClient"/> to do a one-shot fetch, then maps
/// each App Config key path to its environment variable name via
/// <see cref="ConfigOptions.Descriptors"/>.
/// Call <see cref="Start"/> at the beginning of Main (before building the host),
/// then <see cref="WaitForDownload"/> at the top of LoadBackendOptions.
/// </summary>
public class AppConfigBootstrap
{
    private Task<Dictionary<string, string>?>? _downloadTask;
    private readonly ILogger<AppConfigBootstrap> _logger;
    private readonly string? _endpoint;
    private readonly string? _connectionString;
    private readonly string? _labelFilter;

    public AppConfigBootstrap(ILogger<AppConfigBootstrap> logger)
    {
        _logger = logger;
        _endpoint = Environment.GetEnvironmentVariable("AZURE_APPCONFIG_ENDPOINT");
        _connectionString = Environment.GetEnvironmentVariable("AZURE_APPCONFIG_CONNECTION_STRING");

        var labelFilter = Environment.GetEnvironmentVariable("AZURE_APPCONFIG_LABEL");
        _labelFilter = string.IsNullOrEmpty(labelFilter) || labelFilter == "\\0" || labelFilter == "\0"
            ? null
            : labelFilter;
    }

    /// <summary>
    /// Kicks off an async download of Warm: and Cold: keys from App Configuration.
    /// Returns immediately; the download runs on a thread-pool thread.
    /// No-op when AZURE_APPCONFIG_ENDPOINT / AZURE_APPCONFIG_CONNECTION_STRING are not set.
    /// </summary>
    public void Start()
    {
        if (string.IsNullOrEmpty(_endpoint) && string.IsNullOrEmpty(_connectionString))
        {
            _logger.LogInformation("[BOOTSTRAP] App Configuration not configured, skipping bootstrap download");
            _downloadTask = Task.FromResult<Dictionary<string, string>?>(null);
            return;
        }

        _logger.LogInformation("[BOOTSTRAP] Starting App Configuration bootstrap download...");
        _downloadTask = Task.Run(DownloadConfig);
    }

    /// <summary>
    /// Blocks until the bootstrap download completes and returns the dictionary
    /// keyed by environment variable name (ConfigName) with the App Configuration value.
    /// Returns null if not configured or download failed.
    /// Safe to call when Start was never called (no-op).
    /// </summary>
    public Dictionary<string, string>? WaitForDownload()
    {
        if (_downloadTask == null) return null;

        Dictionary<string, string>? settings;
        try
        {
            settings = _downloadTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[BOOTSTRAP] Failed awaiting App Configuration download");
            return null;
        }

        if (settings == null || settings.Count == 0)
        {
            _logger.LogInformation("[BOOTSTRAP] No App Configuration settings downloaded");
            return null;
        }

        _logger.LogInformation("[BOOTSTRAP] Retrieved {Count} App Configuration value(s)", settings.Count);
        return settings;
    }

    private Dictionary<string, string>? DownloadConfig()
    {
        try
        {
            ConfigurationClient client = !string.IsNullOrEmpty(_endpoint)
                ? new ConfigurationClient(new Uri(_endpoint), new DefaultAzureCredential())
                : new ConfigurationClient(_connectionString!);

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
                var selector = new SettingSelector { KeyFilter = $"{prefix}*", LabelFilter = _labelFilter };
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
                        _logger.LogDebug("[BOOTSTRAP] {Key} → {EnvVar}", setting.Key, envVarName);
                    }
                    else if (ConfigParser.IsBackendHostConfigName(keyPath))
                    {
                        // Host entries are not descriptor-backed (Host1..N, Probe_path1..N, IP1..N).
                        // Keep their key names so RegisterBackends can resolve them.
                        settings[keyPath] = setting.Value ?? "";
                        _logger.LogDebug("[BOOTSTRAP] {Key} → {EnvVar}", setting.Key, keyPath);
                    }
                    else
                    {
                        _logger.LogDebug("[BOOTSTRAP] No descriptor for key {Key}, skipping", setting.Key);
                    }
                }
            }

            _logger.LogInformation("[BOOTSTRAP] ✓ Downloaded {Count} setting(s) from App Configuration", settings.Count);
            return settings;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[BOOTSTRAP] ✗ App Configuration download failed — continuing with env vars only");
            return null;
        }
    }
}
