using CompanionApp.Components.Shared;
using CompanionApp.Components.Shared.EventHub;
using Microsoft.Extensions.Options;
using static Program;

namespace CompanionApp.Startup;

internal static class StartupAppConfig
{
        /// <summary>Loads App Configuration and creates the label when missing. Returns the drafts and the profile URL keys that were defaulted.</summary>
    internal static async Task<(IReadOnlyList<AppConfigurationScaffoldSetting> Drafts, IReadOnlyList<string> DefaultedKeys)> InitializeAsync(WebApplication app)
    {
        if (Environment.GetEnvironmentVariable("DEBUGLEVEL") is string debugLevelStr
            && int.TryParse(debugLevelStr, out var debugLevel))
        {
            Program.DEBUGLEVEL = debugLevel;
        }

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
            checkpointHelper("Configuration loading skipped: no App Configuration endpoint configured.", "endpoint missing, skipped");

            return ([], []);
        }

        var configuredLabel = appConfiguration.DefaultLabel;
        var labelDisplay = string.IsNullOrEmpty(configuredLabel) ? "(No label)" : configuredLabel;
        var phase = "loading settings";
        (IReadOnlyList<AppConfigurationScaffoldSetting> Drafts, IReadOnlyList<string> DefaultedKeys) profileDefaults = ([], []);

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
            checkpointHelper($"settings loaded={settings.Count}, label exists={labelExists}, label settings={labelSettingCount}", "settings loaded");
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
            checkpointHelper($"event hub defaults present={hasEventHubDefaults}", "event hub defaults read");

            if (!labelExists || labelSettingCount == 0)
            {
                phase = "creating configured label";
                checkpointHelper("label create path chosen", phase);
                logger.LogInformation(
                    "Creating configuration label {Label} with defaults (LabelExists={LabelExists}, TargetLabelSettings={TargetLabelSettings}). Include Event Hub settings={EventHubDefaultsApplied}.",
                    labelDisplay,
                    labelExists,
                    labelSettingCount,
                    hasEventHubDefaults);
                profileDefaults = await InitializeAppConfigurationLabelAsync(
                    appConfiguration,
                    logger,
                    configuredLabel,
                    labelDisplay,
                    eventHubDefaults,
                    hasEventHubDefaults);
                phase = "label created";
                checkpointHelper("label create path completed", phase);
            }
            else
            {
                appConfiguration.CachedLabel = configuredLabel;
                checkpointHelper("label already has settings", "label already has settings");
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
            checkpointHelper($"failed while {phase} ({exception.GetType().Name})", phase);
            logger.LogDebug(exception, "Configuration failure details.");
        }

        if (Program.DEBUGLEVEL >= 1)
        {
            LogCheckpoint(logger, checkpoints.ToString());
        }
        return profileDefaults;
    }

    private static async Task<(IReadOnlyList<AppConfigurationScaffoldSetting> Drafts, IReadOnlyList<string> DefaultedKeys)> InitializeAppConfigurationLabelAsync(
        AppConfigurationScaffoldService appConfiguration,
        ILogger logger,
        string configuredLabel,
        string labelDisplay,
        Dictionary<string, string> eventHubDefaults,
        bool hasEventHubDefaults)
    {
        checkpointHelper("InitializeAppConfigurationLabelAsync: entered", "entered");
        var drafts = appConfiguration.CreateLabelDraft(configuredLabel);
        logger.LogDebug(
            "Prepared App Configuration label {Label} with {SettingCount} draft settings",
            labelDisplay,
            drafts.Count);
        checkpointHelper($"drafts created={drafts.Count}", "drafts created");

        if (hasEventHubDefaults)
        {
            foreach (var entry in eventHubDefaults.Where(entry =>
                !string.IsNullOrWhiteSpace(entry.Value)))
            {
                drafts.Single(setting => setting.Key == entry.Key).DraftValue = entry.Value;
            }

            drafts.Single(setting => setting.Key == "Cold:Logging:EventLoggers").DraftValue = "eventhub";
            checkpointHelper("event hub defaults applied", "event hub defaults applied");
        }

        var defaultedKeys = StartupEnvOverrides.ApplyStartupOverrides(drafts, appConfiguration.DefaultEndpoint);
        checkpointHelper($"overrides applied={defaultedKeys.Count}", "overrides applied");

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
        checkpointHelper("UpdateAsync completed", "UpdateAsync completed");
        var initializedSettingCount = result.Settings.Count(setting =>
            string.Equals(setting.Label, configuredLabel, StringComparison.Ordinal));

        if (initializedSettingCount == 0)
        {
            logger.LogError(
                "Configuration verification failed: saving label {Label} at {Endpoint} returned no published settings. Startup will continue; review the label in Admin Configuration.",
                labelDisplay,
                appConfiguration.DefaultEndpoint);
            checkpointHelper("no published settings", "no published settings");
            return (drafts, defaultedKeys);
        }

        logger.LogInformation(
            "Configuration created: label {Label} now has {SettingCount} published proxy settings at {Endpoint}.",
            labelDisplay,
            initializedSettingCount,
            appConfiguration.DefaultEndpoint);
        checkpointHelper($"published settings={initializedSettingCount}", "settings published");
        return (drafts, defaultedKeys);
    }
}
