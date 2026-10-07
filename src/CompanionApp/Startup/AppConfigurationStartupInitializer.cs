using CompanionApp.Components.Shared;
using CompanionApp.Components.Shared.EventHub;
using Microsoft.Extensions.Options;
using SimpleL7Proxy.Tokenomics;

namespace CompanionApp.Startup;

internal sealed class AppConfigurationStartupInitializer
{
    private readonly AppConfigurationScaffoldService _appConfiguration;
    private readonly IOptions<EventHubMonitorOptions> _eventHubOptions;
    private readonly ILogger _logger;

    public AppConfigurationStartupInitializer(
        AppConfigurationScaffoldService appConfiguration,
        IOptions<EventHubMonitorOptions> eventHubOptions,
        ILoggerFactory loggerFactory)
    {
        _appConfiguration = appConfiguration;
        _eventHubOptions = eventHubOptions;
        _logger = loggerFactory.CreateLogger("CompanionApp.Startup");
    }

    public async Task InitializeAsync(bool uiOnly)
    {
        _logger.LogInformation(
            "App Configuration startup initialization entered. UiOnly={UiOnly}, EndpointConfigured={EndpointConfigured}, Label={Label}",
            uiOnly,
            !string.IsNullOrWhiteSpace(_appConfiguration.DefaultEndpoint),
            string.IsNullOrEmpty(_appConfiguration.DefaultLabel) ? "(No label)" : _appConfiguration.DefaultLabel);

        if (uiOnly)
        {
            _logger.LogInformation(
                "UI-only mode: App Configuration startup access and Event Hub reader are disabled");
            return;
        }

        if (string.IsNullOrWhiteSpace(_appConfiguration.DefaultEndpoint))
        {
            _logger.LogWarning(
                "App Configuration startup check skipped because CompanionApp:AppConfigurationEndpoint is empty");
            return;
        }

        var configuredLabel = _appConfiguration.DefaultLabel;
        var labelDisplay = string.IsNullOrEmpty(configuredLabel) ? "(No label)" : configuredLabel;
        var phase = "loading settings";

        try
        {
            var settings = await _appConfiguration.LoadAsync(_appConfiguration.DefaultEndpoint);
            var labelExists = _appConfiguration.CachedLabels?
                .Contains(configuredLabel, StringComparer.Ordinal) == true;
            var labelSettingCount = settings.Count(setting =>
                string.Equals(setting.Label, configuredLabel, StringComparison.Ordinal));
            _logger.LogInformation(
                "App Configuration startup load completed. Endpoint={Endpoint}, TotalSettings={TotalSettings}, Labels={LabelCount}, TargetLabel={Label}, TargetLabelExists={LabelExists}, TargetLabelSettings={TargetLabelSettings}",
                _appConfiguration.DefaultEndpoint,
                settings.Count,
                _appConfiguration.CachedLabels?.Count ?? 0,
                labelDisplay,
                labelExists,
                labelSettingCount);

            var eventHubOptions = _eventHubOptions.Value;
            var eventHubDefaults = GetEventHubDefaults(eventHubOptions);
            var hasEventHubDefaults = HasEventHubDefaults(eventHubOptions, eventHubDefaults);

            if (!labelExists)
            {
                phase = "creating configured label";
                _logger.LogInformation(
                    "Configured App Configuration label {Label} was not found; startup will create it. EventHubDefaultsApplied={EventHubDefaultsApplied}",
                    labelDisplay,
                    hasEventHubDefaults);
                await InitializeAppConfigurationLabelAsync(
                    configuredLabel,
                    labelDisplay,
                    eventHubDefaults,
                    hasEventHubDefaults);
                phase = "label created";
            }
            else if (labelSettingCount == 0)
            {
                _logger.LogError(
                    "App Configuration startup check failed: label {Label} at {Endpoint} contains no published proxy settings",
                    labelDisplay,
                    _appConfiguration.DefaultEndpoint);
            }
            else
            {
                _appConfiguration.CachedLabel = configuredLabel;
                _logger.LogInformation(
                    "App Configuration startup check succeeded: label {Label} contains {SettingCount} published proxy settings at {Endpoint}",
                    labelDisplay,
                    labelSettingCount,
                    _appConfiguration.DefaultEndpoint);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "App Configuration startup initialization failed during phase {Phase} for label {Label} at {Endpoint}; the admin page remains available for recovery",
                phase,
                labelDisplay,
                _appConfiguration.DefaultEndpoint);
        }
    }

    private static Dictionary<string, string> GetEventHubDefaults(
        EventHubMonitorOptions eventHubOptions)
    {
        return new Dictionary<string, string>
        {
            ["Cold:Logging:EventHub:Name"] =
                Environment.GetEnvironmentVariable("EVENTHUB_NAME") is { Length: > 0 } hubName
                && !string.IsNullOrWhiteSpace(hubName)
                    ? hubName
                    : eventHubOptions.EventHubName,
            ["Cold:Logging:EventHub:Namespace"] =
                Environment.GetEnvironmentVariable("EVENTHUB_NAMESPACE") is { Length: > 0 } hubNamespace
                && !string.IsNullOrWhiteSpace(hubNamespace)
                    ? hubNamespace
                    : eventHubOptions.EventHubNamespace,
            ["Cold:Logging:EventHub:ConnectionString"] =
                Environment.GetEnvironmentVariable("EVENTHUB_CONNECTIONSTRING") is { Length: > 0 } hubConnectionString
                && !string.IsNullOrWhiteSpace(hubConnectionString)
                    ? hubConnectionString
                    : eventHubOptions.ConnectionString
        };
    }

    private static bool HasEventHubDefaults(
        EventHubMonitorOptions eventHubOptions,
        Dictionary<string, string> eventHubDefaults)
    {
        return eventHubOptions.EventHubEnabled
            && !string.IsNullOrWhiteSpace(eventHubDefaults["Cold:Logging:EventHub:Name"])
            && (!string.IsNullOrWhiteSpace(eventHubDefaults["Cold:Logging:EventHub:Namespace"])
                || !string.IsNullOrWhiteSpace(
                    eventHubDefaults["Cold:Logging:EventHub:ConnectionString"]));
    }

    private async Task InitializeAppConfigurationLabelAsync(
        string configuredLabel,
        string labelDisplay,
        Dictionary<string, string> eventHubDefaults,
        bool hasEventHubDefaults)
    {
        var drafts = _appConfiguration.CreateLabelDraft(configuredLabel);
        _logger.LogInformation(
            "Prepared App Configuration label {Label} with {SettingCount} draft settings",
            labelDisplay,
            drafts.Count);

        if (hasEventHubDefaults)
        {
            foreach (var entry in eventHubDefaults.Where(entry =>
                !string.IsNullOrWhiteSpace(entry.Value)))
            {
                drafts.Single(setting => setting.Key == entry.Key).DraftValue = entry.Value;
            }

            drafts.Single(setting => setting.Key == "Cold:Logging:EventLoggers").DraftValue = "eventhub";
        }

        ApplyStartupOverrides(drafts);

        _logger.LogInformation(
            "Writing App Configuration label {Label} to {Endpoint} with {SettingCount} draft settings",
            labelDisplay,
            _appConfiguration.DefaultEndpoint,
            drafts.Count);

        var result = await _appConfiguration.UpdateAsync(
            _appConfiguration.DefaultEndpoint,
            configuredLabel,
            drafts,
            createLabel: true);

        _appConfiguration.CachedLabel = configuredLabel;
        var initializedSettingCount = result.Settings.Count(setting =>
            string.Equals(setting.Label, configuredLabel, StringComparison.Ordinal));

        if (initializedSettingCount == 0)
        {
            _logger.LogError(
                "App Configuration label {Label} write returned successfully but verification found zero settings at {Endpoint}",
                labelDisplay,
                _appConfiguration.DefaultEndpoint);
            return;
        }

        _logger.LogInformation(
            "App Configuration startup initialized label {Label} with {SettingCount} published settings at {Endpoint}",
            labelDisplay,
            initializedSettingCount,
            _appConfiguration.DefaultEndpoint);
    }

    private static void ApplyStartupOverrides(
        IReadOnlyList<AppConfigurationScaffoldSetting> drafts)
    {
        var metricsServerOverride = Environment.GetEnvironmentVariable("MetricsServerOverride");
        if (metricsServerOverride is not null)
        {
            drafts.Single(setting => setting.Key == "Warm:Tokenomics:MetricsServer")
                .DraftValue = metricsServerOverride;
            drafts.Single(setting => setting.Key == "Warm:Tokenomics:Enable")
                .DraftValue = "true";
            drafts.Single(setting => setting.Key == "Warm:Tokenomics:Options")
                .DraftValue = new TokenomicsSettings().ToString();
        }

        var appInsightsConnectionStringOverride =
            Environment.GetEnvironmentVariable("AppInsightsConnectionStringOverride");
        if (appInsightsConnectionStringOverride is not null)
        {
            drafts.Single(setting => setting.Key == "Cold:Logging:AppInsightsConnectionString")
                .DraftValue = appInsightsConnectionStringOverride;
        }

        var sidecarOverride = Environment.GetEnvironmentVariable("SidecarOverride");
        if (sidecarOverride is not null)
        {
            drafts.Single(setting => setting.Key == "Warm:HealthProbe:Sidecar")
                .DraftValue = $"Enabled=true;url={sidecarOverride}";
        }
    }
}