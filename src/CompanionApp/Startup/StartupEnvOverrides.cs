using CompanionApp.Components.Shared;
using SimpleL7Proxy.Tokenomics;

namespace CompanionApp.Startup;

internal static class StartupEnvOverrides
{
    internal static void ApplyStartupOverrides(
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
