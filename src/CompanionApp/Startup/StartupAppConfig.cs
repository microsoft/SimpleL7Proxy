using CompanionApp.Components.Shared;
using CompanionApp.Components.Shared.EventHub;
using Microsoft.Extensions.Options;

namespace CompanionApp.Startup;

internal static class StartupAppConfig
{
    internal static async Task InitializeAsync(WebApplication app)
    {
        var appConfiguration = app.Services.GetRequiredService<AppConfigurationScaffoldService>();
        var eventHubMonitorOptions = app.Services.GetRequiredService<IOptions<EventHubMonitorOptions>>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("CompanionApp.Startup");

        logger.LogDebug(
            "App Configuration startup initialization entered. EndpointConfigured={EndpointConfigured}, Label={Label}",
            !string.IsNullOrWhiteSpace(appConfiguration.DefaultEndpoint),
            string.IsNullOrEmpty(appConfiguration.DefaultLabel) ? "(No label)" : appConfiguration.DefaultLabel);

        if (string.IsNullOrWhiteSpace(appConfiguration.DefaultEndpoint))
        {
            logger.LogWarning(
                "Configuration loading skipped: no App Configuration endpoint configured.");
            return;
        }

        var configuredLabel = appConfiguration.DefaultLabel;
        var labelDisplay = string.IsNullOrEmpty(configuredLabel) ? "(No label)" : configuredLabel;
        var phase = "loading settings";

        try
        {
            logger.LogDebug(
                "Configuration: loading proxy settings for label {Label} from {Endpoint}.",
                labelDisplay,
                appConfiguration.DefaultEndpoint);
            var settings = await appConfiguration.LoadAsync(appConfiguration.DefaultEndpoint);
            var labelExists = appConfiguration.CachedLabels?
                .Contains(configuredLabel, StringComparer.Ordinal) == true;
            var labelSettingCount = settings.Count(setting =>
                string.Equals(setting.Label, configuredLabel, StringComparison.Ordinal));
            logger.LogDebug(
                "App Configuration startup load completed. Endpoint={Endpoint}, TotalSettings={TotalSettings}, Labels={LabelCount}, TargetLabel={Label}, TargetLabelExists={LabelExists}, TargetLabelSettings={TargetLabelSettings}",
                appConfiguration.DefaultEndpoint,
                settings.Count,
                appConfiguration.CachedLabels?.Count ?? 0,
                labelDisplay,
                labelExists,
                labelSettingCount);

            var eventHubOptions = eventHubMonitorOptions.Value;
            var eventHubDefaults = StartupEventHub.GetEventHubDefaults(eventHubOptions);
            var hasEventHubDefaults = StartupEventHub.HasEventHubDefaults(eventHubOptions, eventHubDefaults);

            if (!labelExists)
            {
                phase = "creating configured label";
                logger.LogInformation(
                    "Creating configuration label {Label} with defaults. Include Event Hub settings={EventHubDefaultsApplied}.",
                    labelDisplay,
                    hasEventHubDefaults);
                await InitializeAppConfigurationLabelAsync(
                    appConfiguration,
                    logger,
                    configuredLabel,
                    labelDisplay,
                    eventHubDefaults,
                    hasEventHubDefaults);
                phase = "label created";
            }
            else if (labelSettingCount == 0)
            {
                logger.LogError(
                    "No proxy settings found for label {Label} at {Endpoint}. Open Admin Configuration to save settings.",
                    labelDisplay,
                    appConfiguration.DefaultEndpoint);
            }
            else
            {
                appConfiguration.CachedLabel = configuredLabel;
                logger.LogInformation(
                    "Configuration loaded: {SettingCount} settings from label {Label}.",
                    labelSettingCount,
                    labelDisplay);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Configuration failed while {Phase} for label {Label} at {Endpoint}. Check access in Admin Configuration. Startup will continue.",
                phase,
                labelDisplay,
                appConfiguration.DefaultEndpoint);
            logger.LogDebug(exception, "Configuration failure details.");
        }
    }

    private static async Task InitializeAppConfigurationLabelAsync(
        AppConfigurationScaffoldService appConfiguration,
        ILogger logger,
        string configuredLabel,
        string labelDisplay,
        Dictionary<string, string> eventHubDefaults,
        bool hasEventHubDefaults)
    {
        var drafts = appConfiguration.CreateLabelDraft(configuredLabel);
        logger.LogDebug(
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

        StartupEnvOverrides.ApplyStartupOverrides(drafts);

        logger.LogInformation(
            "Writing App Configuration label {Label} to {Endpoint} with {SettingCount} draft settings",
            labelDisplay,
            appConfiguration.DefaultEndpoint,
            drafts.Count);

        var result = await appConfiguration.UpdateAsync(
            appConfiguration.DefaultEndpoint,
            configuredLabel,
            drafts,
            createLabel: true);

        appConfiguration.CachedLabel = configuredLabel;
        var initializedSettingCount = result.Settings.Count(setting =>
            string.Equals(setting.Label, configuredLabel, StringComparison.Ordinal));

        if (initializedSettingCount == 0)
        {
            logger.LogError(
                "Configuration verification failed: saving label {Label} at {Endpoint} returned no published settings. Startup will continue; review the label in Admin Configuration.",
                labelDisplay,
                appConfiguration.DefaultEndpoint);
            return;
        }

        logger.LogInformation(
            "Configuration created: label {Label} now has {SettingCount} published proxy settings at {Endpoint}.",
            labelDisplay,
            initializedSettingCount,
            appConfiguration.DefaultEndpoint);
    }
}
