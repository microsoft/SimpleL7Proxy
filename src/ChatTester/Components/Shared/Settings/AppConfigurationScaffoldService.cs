using Azure.Data.AppConfiguration;
using Azure.Identity;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace chat_tester.Components.Shared;

public sealed record AppConfigurationScaffoldSetting(
    string Key,
    string Mode,
    string Section,
    string Name,
    string Label,
    string LoadedValue)
{
    public string DraftValue { get; set; } = LoadedValue;

    // Editable identifier for newly-added rows (existing settings keep their Name).
    public string DraftName { get; set; } = Name;

    public bool IsNew { get; set; }

    public bool IsChanged => !string.Equals(LoadedValue, DraftValue, StringComparison.Ordinal);
}

public sealed class AppConfigurationScaffoldService
{
    private readonly ChatTesterOptions _options;
    private readonly ILogger<AppConfigurationScaffoldService> _logger;
    private readonly DefaultAzureCredential _defaultCredential;
    private ConfigurationClient? _cachedClient;
    private Uri? _cachedEndpoint;

    public IReadOnlyList<AppConfigurationScaffoldSetting>? CachedSettings { get; private set; }

    public string? CachedEndpoint { get; private set; }

    public string? CachedLabel { get; set; }

    public IReadOnlyList<string>? CachedLabels { get; private set; }

    public string? CachedLabelsEndpoint { get; private set; }

    // Time to first returned setting (connect + auth + first byte) and the remaining enumeration time.
    public long LastConnectMs { get; private set; }

    public long LastDownloadMs { get; private set; }

    public bool LastLoadFromDisk { get; private set; }

    private readonly IHostEnvironment _environment;

    public AppConfigurationScaffoldService(
        IOptions<ChatTesterOptions> options,
        ILogger<AppConfigurationScaffoldService> logger,
        DefaultAzureCredential defaultCredential,
        IHostEnvironment environment)
    {
        _options = options.Value;
        _logger = logger;
        _defaultCredential = defaultCredential;
        _environment = environment;
    }

    public string DefaultEndpoint => _options.AppConfigurationEndpoint;

    public string DefaultLabel => _options.AppConfigurationLabel;

    public IReadOnlyDictionary<string, string[]> HostFieldChoices => _options.Hosts.FieldChoices;

    public string HostFieldsDocUrl => _options.Hosts.FieldsDocUrl;

    public IReadOnlyList<string> HostListColumns => _options.Hosts.ListColumns;

    public async Task<IReadOnlyList<AppConfigurationScaffoldSetting>> LoadAsync(
        string endpoint,
        CancellationToken cancellationToken = default)
    {
        // Dev-only fast path: serve the previously saved snapshot from disk (no auth, no network).
        if (_options.BypassConfig)
        {
            var diskStopwatch = Stopwatch.StartNew();
            if (TryReadDiskCache(endpoint, out var diskSettings))
            {
                LastConnectMs = diskStopwatch.ElapsedMilliseconds;
                LastDownloadMs = 0;
                LastLoadFromDisk = true;
                CachedSettings = diskSettings;
                CachedEndpoint = endpoint;
                CachedLabels = diskSettings
                    .Select(setting => setting.Label)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                CachedLabelsEndpoint = endpoint;
                _logger.LogInformation("Loaded {Count} settings from disk cache (BypassConfig)", diskSettings.Count);
                return diskSettings;
            }
        }

        LastLoadFromDisk = false;
        var endpointUri = ParseEndpoint(endpoint);
        var client = GetClient(endpointUri);

        return await DownloadAsync(endpoint, endpointUri, client, cancellationToken);
    }

    public async Task<(IReadOnlyList<AppConfigurationScaffoldSetting> Settings, string PreviousSentinel, string NewSentinel, int UpdatedCount)> UpdateAsync(
        string endpoint,
        string label,
        IReadOnlyCollection<AppConfigurationScaffoldSetting> settings,
        CancellationToken cancellationToken = default)
    {
        var endpointUri = ParseEndpoint(endpoint);
        var client = GetClient(endpointUri);
        var changedSettings = settings
            .Where(setting => setting.IsChanged
                && string.Equals(setting.Label, label, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(setting.Key, "Warm:Sentinel", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var sentinel = settings.FirstOrDefault(setting =>
            string.Equals(setting.Key, "Warm:Sentinel", StringComparison.OrdinalIgnoreCase)
            && string.Equals(setting.Label, label, StringComparison.OrdinalIgnoreCase));

        if (sentinel is null)
        {
            throw new InvalidOperationException($"Warm:Sentinel was not found for label '{label}'.");
        }

        var previousSentinel = sentinel.LoadedValue;
        var nextSentinelValue = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (long.TryParse(previousSentinel, out var currentSentinel))
        {
            nextSentinelValue = Math.Max(nextSentinelValue, currentSentinel + 1);
        }
        var newSentinel = nextSentinelValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var configurationLabel = string.IsNullOrEmpty(label) ? null : label;

        foreach (var setting in changedSettings)
        {
            await client.SetConfigurationSettingAsync(
                setting.Key,
                setting.DraftValue,
                configurationLabel,
                cancellationToken);
        }

        await client.SetConfigurationSettingAsync(
            "Warm:Sentinel",
            newSentinel,
            configurationLabel,
            cancellationToken);

        var downloadedSettings = await DownloadAsync(endpoint, endpointUri, client, cancellationToken);
        foreach (var submittedSetting in changedSettings)
        {
            var downloadedSetting = downloadedSettings.FirstOrDefault(setting =>
                string.Equals(setting.Key, submittedSetting.Key, StringComparison.Ordinal)
                && string.Equals(setting.Label, label, StringComparison.OrdinalIgnoreCase));
            if (downloadedSetting is null
                || !string.Equals(downloadedSetting.LoadedValue, submittedSetting.DraftValue, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Verification failed after re-downloading '{submittedSetting.Key}'.");
            }
        }

        var downloadedSentinel = downloadedSettings.FirstOrDefault(setting =>
            string.Equals(setting.Key, "Warm:Sentinel", StringComparison.OrdinalIgnoreCase)
            && string.Equals(setting.Label, label, StringComparison.OrdinalIgnoreCase));
        if (!string.Equals(downloadedSentinel?.LoadedValue, newSentinel, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Verification failed after re-downloading Warm:Sentinel.");
        }

        return (downloadedSettings, previousSentinel, newSentinel, changedSettings.Count);
    }

    private async Task<IReadOnlyList<AppConfigurationScaffoldSetting>> DownloadAsync(
        string endpoint,
        Uri endpointUri,
        ConfigurationClient client,
        CancellationToken cancellationToken)
    {
        LastLoadFromDisk = false;

        // Single round-trip across all labels — mirrors the proxy's one-call download.
        var selector = new SettingSelector { KeyFilter = "*" };
        var settings = new List<AppConfigurationScaffoldSetting>();
        var labels = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var stopwatch = Stopwatch.StartNew();
        long firstItemMs = -1;

        await foreach (var setting in client.GetConfigurationSettingsAsync(selector, cancellationToken))
        {
            if (firstItemMs < 0)
            {
                firstItemMs = stopwatch.ElapsedMilliseconds;
            }

            if (!TryParsePublishedKey(setting.Key, out var mode, out var section, out var name))
            {
                continue;
            }

            var label = setting.Label ?? string.Empty;
            labels.Add(label);
            settings.Add(new AppConfigurationScaffoldSetting(
                setting.Key,
                mode,
                section,
                name,
                label,
                setting.Value ?? string.Empty));
        }

        var elapsedMs = stopwatch.ElapsedMilliseconds;
        LastConnectMs = firstItemMs < 0 ? elapsedMs : firstItemMs;
        LastDownloadMs = elapsedMs - LastConnectMs;

        _logger.LogInformation(
            "Loaded {SettingCount} published proxy settings across {LabelCount} labels from {Endpoint} in one call",
            settings.Count,
            labels.Count,
            endpointUri.Host);

        var ordered = settings
            .OrderBy(setting => setting.Section, StringComparer.OrdinalIgnoreCase)
            .ThenBy(setting => setting.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        CachedSettings = ordered;
        CachedEndpoint = endpoint;
        CachedLabels = labels.ToList();
        CachedLabelsEndpoint = endpoint;

        if (_options.BypassConfig)
        {
            TryWriteDiskCache(endpoint, ordered);
        }

        return ordered;
    }

    private sealed record CachedSettingDto(
        string Key,
        string Mode,
        string Section,
        string Name,
        string Label,
        string LoadedValue);

    private string DiskCachePath(string endpoint)
    {
        var host = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : "endpoint";
        var safeHost = string.Concat(host.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '.' ? character : '_'));
        return Path.Combine(_environment.ContentRootPath, "data", "appconfig-cache", safeHost + ".json");
    }

    private bool TryReadDiskCache(string endpoint, out List<AppConfigurationScaffoldSetting> settings)
    {
        settings = new List<AppConfigurationScaffoldSetting>();
        try
        {
            var path = DiskCachePath(endpoint);
            if (!File.Exists(path))
            {
                return false;
            }

            var dtos = JsonSerializer.Deserialize<List<CachedSettingDto>>(File.ReadAllText(path));
            if (dtos is null)
            {
                return false;
            }

            settings = dtos
                .Select(dto => TryParsePublishedKey(dto.Key, out var mode, out var section, out var name)
                    ? new AppConfigurationScaffoldSetting(dto.Key, mode, section, name, dto.Label, dto.LoadedValue)
                    : new AppConfigurationScaffoldSetting(dto.Key, dto.Mode, dto.Section, dto.Name, dto.Label, dto.LoadedValue))
                .OrderBy(setting => setting.Section, StringComparer.OrdinalIgnoreCase)
                .ThenBy(setting => setting.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read App Configuration disk cache; falling back to live load");
            return false;
        }
    }

    private void TryWriteDiskCache(string endpoint, IReadOnlyList<AppConfigurationScaffoldSetting> settings)
    {
        try
        {
            var path = DiskCachePath(endpoint);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var dtos = settings
                .Select(setting => new CachedSettingDto(
                    setting.Key, setting.Mode, setting.Section, setting.Name, setting.Label, setting.LoadedValue))
                .ToList();
            File.WriteAllText(path, JsonSerializer.Serialize(dtos, new JsonSerializerOptions { WriteIndented = true }));
            _logger.LogInformation("Saved {Count} settings to disk cache at {Path} (BypassConfig)", settings.Count, path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write App Configuration disk cache");
        }
    }

    private static Uri ParseEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
            || endpointUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Enter a valid HTTPS App Configuration endpoint.", nameof(endpoint));
        }

        return endpointUri;
    }

    private ConfigurationClient GetClient(Uri endpointUri)
    {
        if (_cachedClient != null && _cachedEndpoint == endpointUri)
        {
            return _cachedClient;
        }

        var clientOptions = new ConfigurationClientOptions();
        clientOptions.Diagnostics.IsDistributedTracingEnabled = false;
        clientOptions.Diagnostics.IsLoggingEnabled = false;

        var client = new ConfigurationClient(endpointUri, _defaultCredential, clientOptions);
        _cachedClient = client;
        _cachedEndpoint = endpointUri;
        return client;
    }

    private bool TryParsePublishedKey(
        string key,
        out string mode,
        out string section,
        out string name)
    {
        mode = string.Empty;
        section = string.Empty;
        name = string.Empty;

        var separatorIndex = key.IndexOf(':');
        if (separatorIndex <= 0)
        {
            return false;
        }

        mode = key[..separatorIndex];
        if (!string.Equals(mode, "Warm", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mode, "Cold", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var keyPath = key[(separatorIndex + 1)..];
        if (string.IsNullOrWhiteSpace(keyPath))
        {
            return false;
        }

        var sectionSeparatorIndex = keyPath.IndexOf(':');
        string defaultSection;
        if (sectionSeparatorIndex > 0)
        {
            defaultSection = keyPath[..sectionSeparatorIndex];
            name = keyPath[(sectionSeparatorIndex + 1)..];
        }
        else
        {
            name = keyPath;
            defaultSection = "General";
        }

        // Config-driven guidance overrides the derived section (e.g. "Host*" -> "Hosts").
        section = ResolveSection(keyPath, defaultSection);
        if (string.Equals(defaultSection, "EventHub", StringComparison.OrdinalIgnoreCase)
            && string.Equals(section, "Logging", StringComparison.OrdinalIgnoreCase))
        {
            name = keyPath;
        }
        return true;
    }

    private string ResolveSection(string keyPath, string defaultSection)
    {
        foreach (var rule in _options.AppConfigurationRules)
        {
            if (string.IsNullOrWhiteSpace(rule.Match) || string.IsNullOrWhiteSpace(rule.Section))
            {
                continue;
            }

            if (MatchesPattern(keyPath, rule.Match))
            {
                return rule.Section!;
            }
        }

        return defaultSection;
    }

    private static bool MatchesPattern(string value, string pattern)
    {
        if (pattern == "*")
        {
            return true;
        }

        var startWild = pattern.StartsWith('*');
        var endWild = pattern.EndsWith('*');
        var core = pattern.Trim('*');

        if (startWild && endWild)
        {
            return value.Contains(core, StringComparison.OrdinalIgnoreCase);
        }
        if (endWild)
        {
            return value.StartsWith(core, StringComparison.OrdinalIgnoreCase);
        }
        if (startWild)
        {
            return value.EndsWith(core, StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(value, core, StringComparison.OrdinalIgnoreCase);
    }
}